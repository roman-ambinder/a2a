using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using A2A.Server.Agents;
using A2A.Server.Execution;
using A2A.Server.Idempotency;
using A2A.Server.Pipeline;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using A2A.Validation;
using A2A.Server.Authorization;

namespace A2A.Server;

/// <summary>
/// The default <see cref="IA2ARequestHandler"/>: transport-agnostic implementation of every A2A operation. Protocol bindings
/// (JSON-RPC, HTTP+JSON, gRPC) resolve the interface, parse the wire format, build a <see cref="ServerCallContext"/>, and call
/// these methods. Errors are thrown as <see cref="A2AException"/>.
/// Every operation binds the tenant from the request, then runs through the operation pipeline: telemetry (see
/// the spans and counters named in <see cref="A2A.Diagnostics.A2ATelemetry"/>) around the <see cref="A2AFilterStage.BeforeAuthorization"/> filters, authorization, the <see cref="A2AFilterStage.AfterAuthorization"/>
/// filters, then the operation (see <see cref="IA2AOperationFilter"/>).
/// The tasks, messages and events these methods return may share instances with the stores: serialize them, never change them.
/// </summary>
public sealed class A2ARequestHandler : IA2ARequestHandler, IDisposable
{
    private readonly TaskStore _tasks;
    private readonly EventStore _events;
    private readonly IdempotencyCoordinator _idempotency;
    private readonly TaskEventBus _bus;
    private readonly A2AServerOptions _options;
    private readonly IAgentCardProvider _cards;
    private readonly RequestCards _requestCards;
    private readonly A2AAuthorizationHandler _authorization;
    private readonly CallValidator _validator;
    private readonly PushConfigService _pushConfigs;
    private readonly TaskExecutionEngine _engine;
    private readonly TaskStreamer _streamer;
    private readonly AcceptMessage _accept;
    private readonly OperationPipeline _pipeline;
    private readonly A2A.Delegation.ITokenExchanger? _tokenExchanger;

    // One cached delegate per operation, so a call through the pipeline creates no closure for its operation.
    private readonly Func<SendMessageRequest, ServerCallContext, CancellationToken, ValueTask<SendMessageResponse>> _sendMessage;
    private readonly Func<SendMessageRequest, ServerCallContext, CancellationToken, ValueTask<IAsyncEnumerable<SequencedEvent>>> _sendStreaming;
    private readonly Func<GetTaskRequest, ServerCallContext, CancellationToken, ValueTask<AgentTask>> _getTask;
    private readonly Func<ListTasksRequest, ServerCallContext, CancellationToken, ValueTask<ListTasksResponse>> _listTasks;
    private readonly Func<CancelTaskRequest, ServerCallContext, CancellationToken, ValueTask<AgentTask>> _cancelTask;
    private readonly Func<TaskPushNotificationConfig, ServerCallContext, CancellationToken, ValueTask<TaskPushNotificationConfig>> _createPush;
    private readonly Func<GetTaskPushNotificationConfigRequest, ServerCallContext, CancellationToken, ValueTask<TaskPushNotificationConfig>> _getPush;
    private readonly Func<ListTaskPushNotificationConfigsRequest, ServerCallContext, CancellationToken, ValueTask<ListTaskPushNotificationConfigsResponse>> _listPush;
    private readonly Func<DeleteTaskPushNotificationConfigRequest, ServerCallContext, CancellationToken, ValueTask<object?>> _deletePush;
    private readonly Func<GetExtendedAgentCardRequest, ServerCallContext, CancellationToken, ValueTask<AgentCard>> _extendedCard;

    /// <summary>The registered extension operations by method name (case-sensitive), each with its descriptor.</summary>
    private readonly Dictionary<string, (A2AExtensionOperation Operation, A2AOperationDescriptor Descriptor)> _extensions;

    private static readonly A2AOperationDescriptor SendStreamingOperation = A2AOperationDescriptor.For(A2AOperation.SendStreamingMessage);
    private static readonly A2AOperationDescriptor SubscribeOperation = A2AOperationDescriptor.For(A2AOperation.SubscribeToTask);

    /// <summary>Creates the handler. In an ASP.NET Core app the DI container does this; the parameters are the registered services.</summary>
    /// <remarks>
    /// The handler does not own <paramref name="engine"/>: its owner (the DI container) disposes it. The pipeline is built here,
    /// once, from <paramref name="filters"/> in their order (registered with <see cref="A2AServerBuilder.AddOperationFilter{T}"/>).
    /// <paramref name="extensionOperations"/> are the operations registered with <see cref="A2AServerBuilder.AddExtensionOperation"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Two extension operations have the same method name.</exception>
    public A2ARequestHandler(
        TaskStore tasks,
        EventStore events,
        IdempotencyCoordinator idempotency,
        A2AAuthorizationHandler authorization,
        A2AServerOptions options,
        CallValidator validator,
        PushConfigService pushConfigs,
        TaskExecutionEngine engine,
        IEnumerable<A2AOperationFilterRegistration> filters,
        IEnumerable<A2AExtensionOperation> extensionOperations,
        IAgentCardProvider cards,
        A2A.Delegation.ITokenExchanger? tokenExchanger = null)
    {
        _tokenExchanger = tokenExchanger;
        _cards = cards;
        _authorization = authorization;
        _requestCards = new RequestCards(cards);
        ArgumentNullException.ThrowIfNull(extensionOperations);
        _extensions = new(StringComparer.Ordinal);
        foreach (var extension in extensionOperations)
        {
            if (!_extensions.TryAdd(extension.Method, (extension, A2AOperationDescriptor.Extension(extension.Method))))
                throw new InvalidOperationException($"More than one extension operation is registered as '{extension.Method}'.");
        }
        _tasks = tasks;
        _events = events;
        _idempotency = idempotency;
        _bus = engine.Bus; // the bus the engine publishes to: SendMessage subscribes to the same one
        _options = options;
        _validator = validator;
        _pushConfigs = pushConfigs;
        _engine = engine;
        // Built here, not registered: it shares the handler's services and has no public surface of its own.
        _streamer = new TaskStreamer(tasks, events, engine, idempotency, authorization, options, cards);
        _accept = AcceptAsync; // one delegate for every stream
        _pipeline = new OperationPipeline(new AuthorizationFilter(authorization, options), filters);
        _sendMessage = SendMessageCoreAsync;
        _sendStreaming = (r, c, ct) => new(_streamer.StreamExecutionAsync(r, c, _accept, SendStreamingOperation, ct));
        _getTask = GetTaskCoreAsync;
        _listTasks = ListTasksCoreAsync;
        _cancelTask = CancelTaskCoreAsync;
        _createPush = CreateTaskPushNotificationConfigCoreAsync;
        _getPush = GetTaskPushNotificationConfigCoreAsync;
        _listPush = ListTaskPushNotificationConfigsCoreAsync;
        _deletePush = DeleteTaskPushNotificationConfigCoreAsync;
        _extendedCard = GetExtendedAgentCardCoreAsync;
    }

    /// <summary>
    /// The public Agent Card of the host: <see cref="IAgentCardProvider.Get"/>, the completed copy of <see cref="A2AServerOptions.AgentCard"/>.
    /// With a <see cref="TenantCardProvider"/> each tenant has its own card, and requests are checked against that one.
    /// </summary>
    public AgentCard AgentCard => _cards.Get();

    /// <summary>The refusal text for a tenant that no agent serves (its card is null).</summary>
    internal const string NoAgentText = RequestCards.NoAgentText;

    // =====================================================================
    // Call validation: version, extensions, authentication
    // =====================================================================

    /// <summary>Checks protocol version, required extensions, and authentication; fills <see cref="ServerCallContext.ActivatedExtensions"/>.</summary>
    public void ValidateCall(ServerCallContext call) => _validator.Validate(call);

    // =====================================================================
    // Operation pipeline
    // =====================================================================

    /// <summary>
    /// A streaming operation. Lazy like the stream it returns: on the first read it binds the tenant and runs the pipeline,
    /// so a refusal surfaces from the first <c>MoveNextAsync</c>, before any event, and bindings turn it into an error response.
    /// </summary>
    private async IAsyncEnumerable<SequencedEvent> StreamAsync<TRequest>(A2AOperationDescriptor operation, string? tenant, ServerCallContext call,
        TRequest request, Func<TRequest, ServerCallContext, CancellationToken, ValueTask<IAsyncEnumerable<SequencedEvent>>> open,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where TRequest : class
    {
        call.BindTenant(tenant);
        var events = await _pipeline.RunAsync(operation, call, request, open, cancellationToken).ConfigureAwait(false);
        await foreach (var e in events.WithCancellation(cancellationToken).ConfigureAwait(false)) yield return e;
    }

    // =====================================================================
    // SendMessage / SendStreamingMessage
    // =====================================================================

    /// <summary>
    /// Sends a message. Unless <see cref="SendMessageConfiguration.ReturnImmediately"/> is set, waits until the task is
    /// terminal or interrupted, by an event of this execution or of any other execution of the task (for example CancelTask),
    /// or until no execution of the task is running any more.
    /// </summary>
    public async Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        return await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.SendMessage), call, request, _sendMessage, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SendMessageResponse> SendMessageCoreAsync(SendMessageRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        await _requestCards.RequireAgentAsync(call, cancellationToken).ConfigureAwait(false);
        // Before the idempotency claim and anything stored: a tenant without an agent leaves no trace.
        await using var agent = await _engine.RequireAgentAsync(call.Tenant, cancellationToken).ConfigureAwait(false);
        var (claim, replay) = await _idempotency.BeginAsync(request, call, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            await agent.DisposeAsync().ConfigureAwait(false); // nothing runs
            return await _idempotency.ReplayAsync(replay, request, call, _engine.WaitUntilSettledAsync, cancellationToken).ConfigureAwait(false);
        }
        var taskId = string.IsNullOrEmpty(request.Message?.TaskId) ? TaskIds.New() : request.Message.TaskId;
        var key = Key(call, taskId);
        var scope = ScopeKey(call.Tenant, taskId);
        // Subscribe before the agent starts, so no event of another execution is missed.
        using var sub = _bus.Subscribe(scope);
        var (accepted, afterSequence, acceptedStatusTimestamp) = await AcceptAsync(request, call, claim, taskId, agent, cancellationToken)
            .ConfigureAwait(false);
        if (accepted is not { } exec)
        {
            // A duplicate follow-up: nothing started, so there is nothing to wait for.
            var current = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(taskId);
            return new TaskResponse(Shape(current.Task, request.Configuration?.HistoryLength));
        }
        var reply = await _streamer.WaitForOutcomeAsync(exec, key, sub, afterSequence, acceptedStatusTimestamp,
            request.Configuration?.ReturnImmediately == true, cancellationToken).ConfigureAwait(false);
        if (reply is not null) return new MessageResponse(reply);

        var record = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new A2AException(A2AErrorCode.InvalidAgentResponse, "The agent finished without replying or creating a task.");
        return new TaskResponse(Shape(record.Task, request.Configuration?.HistoryLength));
    }

    /// <summary>
    /// Sends a message and streams the task's events, including those of other executions of the task (for example the
    /// CANCELED event of a CancelTask), in sequence order and without repeats. The stream ends after the task's final event,
    /// or when this execution ended and no other execution of the task is running. The stream is limited like every stream: see
    /// <see cref="A2ALimits.MaxConcurrentStreamsPerTask"/>, <see cref="A2ALimits.MaxConcurrentStreamsPerPrincipal"/>,
    /// <see cref="A2ALimits.StreamReauthInterval"/> and <see cref="A2ALimits.MaxStreamDuration"/>. A retry that resumes the task
    /// instead of sending (the idempotency key's task, or a follow-up whose message id is already in the history) is authorized
    /// as <see cref="A2AOperation.SubscribeToTask"/>, like a <paramref name="lastEventId"/> resume.
    /// </summary>
    /// <param name="request">The message to send.</param>
    /// <param name="call">The caller.</param>
    /// <param name="lastEventId">
    /// A valid <paramref name="lastEventId"/> (a <see cref="StreamCursor"/>, from the <c>Last-Event-ID</c> header) makes the
    /// call a reconnect: it resumes the task the cursor names, as a <see cref="SubscribeToTaskAsync"/> of the caller's task,
    /// and the message is not sent again. A resume is authorized as <see cref="A2AOperation.SubscribeToTask"/> with a
    /// <see cref="SubscribeToTaskRequest"/>. The header is ignored, and the message sent normally, when it is null, does not
    /// parse, or when the message names a different task than the cursor.
    /// </param>
    /// <param name="onCaughtUp">Passed to the resume (see <see cref="IA2ARequestHandler.SendStreamingMessageAsync"/>).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public IAsyncEnumerable<SequencedEvent> SendStreamingMessageAsync(SendMessageRequest request, ServerCallContext call, string? lastEventId = null,
        Action? onCaughtUp = null, CancellationToken cancellationToken = default)
    {
        var messageTaskId = request.Message?.TaskId;
        return StreamCursor.TryParse(lastEventId, out var taskId, out var sequence)
               && (string.IsNullOrEmpty(messageTaskId) || string.Equals(messageTaskId, taskId, StringComparison.Ordinal))
            ? SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId, Tenant = request.Tenant }, call, sequence, onCaughtUp, cancellationToken)
            : StreamAsync(SendStreamingOperation, request.Tenant, call, request, _sendStreaming, cancellationToken);
    }

    /// <param name="Context">The request context for the agent.</param>
    /// <param name="PushConfig">The push config to save when a new task is created.</param>
    /// <param name="LastSequence">For a follow-up, the task's last event sequence right after the message was appended; 0 for a new task.</param>
    /// <param name="Duplicate">The follow-up's message id was already in the task's history: nothing was appended and no agent must start.</param>
    /// <param name="AcceptedStatus">For a follow-up, the task's status when its message was committed (a follow-up does not change it).</param>
    private sealed record Prepared(AgentRequestContext Context, TaskPushNotificationConfig? PushConfig, long LastSequence, bool Duplicate = false,
        AgentTaskStatus? AcceptedStatus = null);

    /// <summary>
    /// Accepts the message and starts the agent. The idempotency claim is abandoned when the message is refused or the agent
    /// cannot start; a follow-up records its task at once (the message is already in the task's history), and a new task
    /// records it when it is first stored.
    /// </summary>
    /// <returns>
    /// The execution, the task's last sequence before it (0 for a new task; see <see cref="Prepared.LastSequence"/>), and for a
    /// follow-up the timestamp of the task's status when the message was committed. The execution is null for a duplicate
    /// follow-up (see <see cref="Prepared.Duplicate"/>): nothing was started.
    /// </returns>
    /// <remarks>
    /// When a follow-up's execution cannot start after its message was committed, the task is failed
    /// (<see cref="TaskExecutionEngine.StartFailureText"/>), so it does not wait for good for an agent that never runs.
    /// </remarks>
    private async Task<(Execution.Execution? Execution, long AfterSequence, DateTimeOffset? AcceptedStatusTimestamp)> AcceptAsync(
        SendMessageRequest request, ServerCallContext call, IdempotencyClaim claim, string? newTaskId, RentedAgent agent, CancellationToken ct)
    {
        try
        {
            var prepared = await PrepareAsync(request, call, ct, newTaskId).ConfigureAwait(false);
            // The request resolved the tenant's card already; its push capability holds for the whole execution, so no card
            // provider call happens per event (or under a task lock).
            var pushEnabled = call.ResolvedCard?.Card?.Capabilities.PushNotifications == true;
            if (prepared.Context.IsNewTask)
                return (await _engine.StartAsync(prepared.Context, prepared.PushConfig, claim, agent, pushEnabled).ConfigureAwait(false), 0, null);
            if (prepared.Duplicate)
            {
                // The message is already in the history (an earlier request of it failed after the commit): its outcome is
                // the task as it is. A retry with this claim gets the task as it is too.
                await _idempotency.RecordAsync(claim, new IdempotencyOutcome.TaskCreated(prepared.Context.TaskId, 0), prepared.Context.TaskId).ConfigureAwait(false);
                return (null, 0, null);
            }

            // Record after the execution is registered (on every instance), so a blocking retry sees it running and waits for
            // its outcome.
            var taskId = prepared.Context.TaskId;
            var acceptedStatus = prepared.AcceptedStatus!;
            Execution.Execution exec;
            try
            {
                exec = await _engine.StartAsync(prepared.Context, prepared.PushConfig, IdempotencyClaim.None, agent, pushEnabled).ConfigureAwait(false);
            }
            catch
            {
                // The message is committed, but no agent runs for it: fail the task rather than leave it waiting for good.
                await _engine.TryFailUnstartedAsync(Key(call, taskId), acceptedStatus).ConfigureAwait(false);
                throw;
            }
            await _idempotency.RecordAsync(claim, new IdempotencyOutcome.TaskCreated(taskId, prepared.LastSequence, acceptedStatus.Timestamp), taskId)
                .ConfigureAwait(false);
            return (exec, prepared.LastSequence, acceptedStatus.Timestamp);
        }
        catch
        {
            await _idempotency.AbandonAsync(claim).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Throws ContentTypeNotSupported when a part's media type is not accepted by the card (default input modes plus every
    /// skill's input modes). Parts without a media type are never checked, and then the card is not even resolved. A card that
    /// declares no modes accepts everything.
    /// </summary>
    private async ValueTask RequireSupportedMediaTypesAsync(Message message, ServerCallContext call, CancellationToken ct)
    {
        var hasMediaType = false;
        foreach (var part in message.Parts)
            if (!string.IsNullOrEmpty(part.MediaType)) { hasMediaType = true; break; }
        if (!hasMediaType) return;

        var card = await _requestCards.RequireAgentAsync(call, ct).ConfigureAwait(false);
        if (!HasAnyInputMode(card)) return;
        foreach (var part in message.Parts)
        {
            if (string.IsNullOrEmpty(part.MediaType) || AcceptsMediaType(card, part.MediaType)) continue;
            throw new A2AException(A2AErrorCode.ContentTypeNotSupported, $"Media type '{part.MediaType}' is not supported by this agent.");
        }
    }

    private static bool HasAnyInputMode(AgentCard card)
    {
        if (card.DefaultInputModes is { Count: > 0 }) return true;
        if (card.Skills is not null)
            foreach (var skill in card.Skills)
                if (skill.InputModes is { Count: > 0 }) return true;
        return false;
    }

    private static bool AcceptsMediaType(AgentCard card, string mediaType)
    {
        var wanted = MediaTypeEssence(mediaType);
        if (card.DefaultInputModes is not null)
            foreach (var mode in card.DefaultInputModes)
                if (ModeMatches(mode, wanted)) return true;
        if (card.Skills is not null)
            foreach (var skill in card.Skills)
                if (skill.InputModes is not null)
                    foreach (var mode in skill.InputModes)
                        if (ModeMatches(mode, wanted)) return true;
        return false;
    }

    /// <summary>The media type without parameters, trimmed.</summary>
    private static ReadOnlySpan<char> MediaTypeEssence(string mediaType)
    {
        var span = mediaType.AsSpan();
        var semi = span.IndexOf(';');
        if (semi >= 0) span = span[..semi];
        return span.Trim();
    }

    private static bool ModeMatches(string? mode, ReadOnlySpan<char> wanted)
    {
        if (string.IsNullOrEmpty(mode)) return false;
        var m = MediaTypeEssence(mode);
        if (m.Equals(wanted, StringComparison.OrdinalIgnoreCase) || m.Equals("*/*", StringComparison.Ordinal)) return true;
        if (m.Length > 2 && m[^2..].SequenceEqual("/*"))
        {
            var slash = wanted.IndexOf('/');
            return slash > 0 && m[..^1].Equals(wanted[..(slash + 1)], StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private async Task<Prepared> PrepareAsync(SendMessageRequest request, ServerCallContext call, CancellationToken ct, string? newTaskId = null)
    {
        call.BindTenant(request.Tenant);
        A2AValidator.ValidateIncoming(request);
        RequestLimitValidator.Validate(request.Message, _options.Limits);
        ActorChainValidator.Validate(request.Message, _options.Limits, call);
        await RequireSupportedMediaTypesAsync(request.Message, call, ct).ConfigureAwait(false);
        await SkillSelectionValidator.ValidateAsync(request.Message, call, _requestCards, _cards, _authorization, ct).ConfigureAwait(false);
        // A copy: the caller's message is never changed (a retry with the same instance must hash the same). This copy goes to
        // the executor; the stored history gets copies of its own.
        var message = request.Message.DeepCopy();
        var isFollowUp = !string.IsNullOrEmpty(message.TaskId);
        var taskId = isFollowUp ? message.TaskId! : newTaskId ?? TaskIds.New();
        AgentTask? existing = null;
        long lastSequence = 0;
        string contextId;
        var duplicate = false;
        var approval = ApprovalState.None;

        // Check the push config before anything changes, so a refused request leaves no trace and a retry is refused the same way.
        TaskPushNotificationConfig? push = null;
        if (request.Configuration?.TaskPushNotificationConfig is { } p)
        {
            RequestCards.RequirePush(await _requestCards.RequireAgentAsync(call, ct).ConfigureAwait(false));
            push = p.DeepCopy(); // the caller's config is never changed
            push.TaskId = taskId;
            push.Id = string.IsNullOrEmpty(push.Id) ? TaskIds.New() : push.Id;
            push.Tenant = call.Tenant;
            await _pushConfigs.ValidateInlineAsync(push, ct).ConfigureAwait(false);
        }

        if (isFollowUp)
        {
            var key = Key(call, taskId);
            var pushIsNew = false;   // the config id did not exist before this request
            var pushSaved = false;
            var committed = false;
            var gate = await _engine.LockAsync(key, ct).ConfigureAwait(false);
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    var record = await _tasks.GetAsync(key, ct).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(taskId);
                    var state = record.Task.Status.State;
                    if (!string.IsNullOrEmpty(message.ContextId) && message.ContextId != record.Task.ContextId)
                        throw A2AException.InvalidParams("message.contextId does not match the task's contextId");
                    if (record.Task.History?.Exists(m => string.Equals(m.MessageId, message.MessageId, StringComparison.Ordinal)) == true)
                    {
                        // Already accepted (an earlier request of it failed after the commit): no push config, no append, no
                        // new execution. Checked before the state, so a retry after the task settled gets its outcome.
                        duplicate = true;
                        existing = record.Task;
                        contextId = record.Task.ContextId;
                        break;
                    }
                    if (state.IsTerminal())
                        throw new A2AException(A2AErrorCode.UnsupportedOperation,
                            "The task is in a terminal state. Start a new task and list this one in referenceTaskIds.")
                        {
                            Metadata = new() { ["taskId"] = taskId },
                        };
                    if (!TaskStateMachine.AcceptsFollowUp(state, _options.FollowUpPolicy))
                        throw new A2AException(A2AErrorCode.UnsupportedOperation, $"The task does not accept messages in state {state}.")
                        {
                            Metadata = new() { ["taskId"] = taskId },
                        };
                    EnsureTurnCapacity(record.Task);
                    approval = ResolveApproval(record.Task, message, out var consume);
                    if (push is not null && !pushSaved)
                    {
                        // Saved atomically under the limit before the message is committed, so a refused request leaves no message.
                        pushIsNew = await _pushConfigs.SaveInlineUnderLimitAsync(key, push, ct).ConfigureAwait(false);
                        pushSaved = true;
                    }
                    contextId = record.Task.ContextId;
                    message.ContextId = contextId;
                    // The stored record belongs to the store: append to a copy, with a copy of the message the executor gets.
                    var next = record.Task.CopyForUpdate();
                    (next.History ??= []).Add(message.DeepCopy());
                    // Same update as the approving message: the approval cannot be used twice, also across instances (CAS).
                    if (consume is not null) (next.Metadata ??= [])[A2AProtocol.ApprovalConsumedKey] = StringElement(consume);
                    // The latest well-formed chain wins; a follow-up without one leaves the echo as it is.
                    if (ActorChainValidator.WellFormedChain(message) is { } chain) (next.Metadata ??= [])[A2A.Delegation.ActorChain.MetadataKey] = chain;
                    try
                    {
                        await _tasks.UpdateAsync(record with { Task = next }, record.Version, ct).ConfigureAwait(false);
                        existing = next;
                        committed = true;
                        // Still under the task lock. The message is committed: a cancelled request must not stop here, or
                        // the idempotency entry would be abandoned and a retry would append the message again.
                        lastSequence = await _events.LastSequenceAsync(key, CancellationToken.None).ConfigureAwait(false);
                        break;
                    }
                    catch (ConcurrencyException) when (attempt < TaskExecutionEngine.MaxConflictRetries)
                    {
                        // The task changed under us: wait a moment, reload, re-check the state, and append again. The wait
                        // holds the striped task lock (inherited from main), so tasks on the same stripe wait too; conflicts
                        // come only from other instances (writers here share the lock), so this is rare and short.
                        await TaskExecutionEngine.ConflictBackoffAsync(attempt, ct).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                // The message was not committed (state refused, conflicts, cancel): do not keep the config it came with.
                if (pushSaved && pushIsNew && !committed) await _pushConfigs.TryDeleteAsync(key, push!.Id!).ConfigureAwait(false);
                gate.Dispose();
            }
        }
        else
        {
            contextId = await ResolveContextAsync(message.ContextId, call, ct).ConfigureAwait(false);
            message.ContextId = contextId;
        }

        // A new task saves its push config when the task is created; a follow-up saved it above, before its message was committed.

        var context = new AgentRequestContext
        {
            Message = message,
            TaskId = taskId,
            ContextId = contextId,
            Task = existing?.DeepCopy(), // the executor's own view: changing it never changes the stored task
            Configuration = request.Configuration,
            Metadata = request.Metadata,
            Call = call,
            Approval = approval,
            TokenExchanger = _tokenExchanger,
        };
        return new Prepared(context, existing is null ? push : null, lastSequence, duplicate, isFollowUp ? existing?.Status : null);
    }

    /// <summary>
    /// Approval binding (spec design §6.6). The pending approval is the hash in the metadata of the status message of a task
    /// waiting in an interrupted state, so it is stored with the task and every instance sees it. It is used at most once:
    /// the commit of the approving message also records the status message's id in the task metadata
    /// (<see cref="A2AProtocol.ApprovalConsumedKey"/>), and a used approval is no longer pending. A follow-up without a hash is
    /// accepted (<see cref="ApprovalState.NotApproved"/> while an approval is pending); a hash that does not match the pending
    /// one, or when none is pending or it was used, is InvalidParams. A pending approval whose status message has no id fails
    /// closed (treated as used). <paramref name="consume"/> is the status message id to record when the result is
    /// <see cref="ApprovalState.Approved"/>.
    /// </summary>
    private static ApprovalState ResolveApproval(AgentTask task, Message message, out string? consume)
    {
        consume = null;
        string? pending = null;
        var status = task.Status.Message;
        if (task.Status.State.IsInterrupted() && status?.Metadata is { } statusMetadata
            && statusMetadata.TryGetValue(A2AProtocol.ApprovalHashKey, out var p) && p.ValueKind == JsonValueKind.String)
            pending = p.GetString();
        // A status message without an id could never be marked used: fail closed, as if it were.
        var used = pending is not null
            && (string.IsNullOrEmpty(status!.MessageId)
                || (task.Metadata is { } taskMetadata
                    && taskMetadata.TryGetValue(A2AProtocol.ApprovalConsumedKey, out var c) && c.ValueKind == JsonValueKind.String
                    && string.Equals(c.GetString(), status.MessageId, StringComparison.Ordinal)));

        if (message.Metadata is null || !message.Metadata.TryGetValue(A2AProtocol.ApprovalHashKey, out var value))
            return pending is null ? ApprovalState.None : ApprovalState.NotApproved;
        if (pending is null || used || value.ValueKind != JsonValueKind.String
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pending), Encoding.UTF8.GetBytes(value.GetString()!)))
            throw A2AException.InvalidParams("Approval does not match the pending request.");
        consume = status!.MessageId;
        return ApprovalState.Approved;
    }

    private static JsonElement StringElement(string value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) writer.WriteStringValue(value);
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    private static readonly JsonElement TrueElement = JsonDocument.Parse("true").RootElement.Clone();

    /// <summary>
    /// Returns the context id for a new task and makes sure the context belongs to the caller. A context owned by
    /// someone else looks like a missing one.
    /// </summary>
    /// <summary>
    /// Throws UnsupportedOperation when one more message would exceed <see cref="A2AServerOptions.MaxTurnsPerTask"/>.
    /// Turns are the user messages in the stored history (the first message and every accepted follow-up).
    /// </summary>
    private void EnsureTurnCapacity(AgentTask task)
    {
        if (_options.MaxTurnsPerTask is not { } max) return;
        var turns = 0;
        if (task.History is { } history)
            foreach (var m in history)
                if (m.Role == Role.User) turns++;
        if (turns < max) return;
        throw new A2AException(A2AErrorCode.UnsupportedOperation,
            $"The task reached its limit of {max} messages. Start a new task and list this one in referenceTaskIds.")
        {
            Metadata = new() { ["taskId"] = task.Id, ["maxTurnsPerTask"] = max.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        };
    }

    private async Task<string> ResolveContextAsync(string? requested, ServerCallContext call, CancellationToken ct)
    {
        const string NotFound = "contextId not found";
        if (string.IsNullOrEmpty(requested))
        {
            var created = await _tasks.GetOrCreateContextAsync(
                new ContextRecord(call.Tenant, call.Principal, TaskIds.New(), DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
            if (!string.Equals(created.Owner, call.Principal, StringComparison.Ordinal)) throw A2AException.InvalidParams(NotFound);
            return created.ContextId;
        }

        var existing = await _tasks.GetContextAsync(call.Tenant, requested, ct).ConfigureAwait(false);
        if (existing is null)
        {
            if (_options.ClientContextIdPolicy != ClientContextIdPolicy.AdoptIfUnknown) throw A2AException.InvalidParams(NotFound);
            existing = await _tasks.GetOrCreateContextAsync(
                new ContextRecord(call.Tenant, call.Principal, requested, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        }
        if (!string.Equals(existing.Owner, call.Principal, StringComparison.Ordinal)) throw A2AException.InvalidParams(NotFound);
        return existing.ContextId;
    }

    // =====================================================================
    // Tasks
    // =====================================================================

    /// <summary>GetTask (spec §3.1.3). Throws TaskNotFound when the task does not exist or belongs to another caller.</summary>
    public async Task<AgentTask> GetTaskAsync(GetTaskRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        return await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.GetTask), call, request, _getTask, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AgentTask> GetTaskCoreAsync(GetTaskRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        await _requestCards.RequireAgentForTaskAsync(call, request.Id, cancellationToken).ConfigureAwait(false);
        A2AValidator.RequireId(request.Id, "id");
        if (request.HistoryLength is < 0) throw A2AException.InvalidParams("historyLength must be >= 0");
        var record = await _tasks.GetAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        return Shape(record.Task, request.HistoryLength);
    }

    /// <summary>ListTasks (spec §3.1.4). Returns the caller's tasks, newest status first. The page size defaults to 50 and is capped by <see cref="A2ALimits.MaxListPageSize"/>.</summary>
    public async Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        return await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.ListTasks), call, request, _listTasks, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ListTasksResponse> ListTasksCoreAsync(ListTasksRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        await _requestCards.RequireAgentAsync(call, cancellationToken).ConfigureAwait(false);
        if (request.PageSize is < 1) throw A2AException.InvalidParams("pageSize must be between 1 and 100");
        if (request.HistoryLength is < 0) throw A2AException.InvalidParams("historyLength must be >= 0");
        var pageSize = Math.Min(request.PageSize ?? 50, _options.Limits.MaxListPageSize);

        var result = await _tasks.ListAsync(new TaskQuery(
            call.Tenant,
            call.Principal,
            string.IsNullOrEmpty(request.ContextId) ? null : request.ContextId,
            request.Status is null or TaskState.Unspecified ? null : request.Status,
            request.StatusTimestampAfter,
            pageSize,
            request.PageToken), cancellationToken).ConfigureAwait(false);

        var include = request.IncludeArtifacts == true;
        return new ListTasksResponse
        {
            Tasks = result.Records.Select(r => Shape(r.Task, request.HistoryLength, include)).ToList(),
            NextPageToken = result.NextPageToken,
            PageSize = pageSize,
            TotalSize = result.TotalSize,
        };
    }

    /// <summary>
    /// CancelTask (spec §3.1.5). Asks the running agent to stop (on this instance directly, on the others through the
    /// execution coordinator), waits up to <see cref="A2AServerOptions.CancelGracePeriod"/>, and returns the task. When an
    /// execution still runs after that, the response is the task with <see cref="A2AProtocol.CancelRequestedKey"/> = true in its
    /// metadata (the cancel is requested, not done). Throws TaskNotCancelable when the task is already terminal.
    /// </summary>
    public async Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        return await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.CancelTask), call, request, _cancelTask, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AgentTask> CancelTaskCoreAsync(CancelTaskRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        var card = await _requestCards.RequireAgentForTaskAsync(call, request.Id, cancellationToken).ConfigureAwait(false);
        A2AValidator.RequireId(request.Id, "id");
        var key = Key(call, request.Id);
        var record = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        if (record.Task.Status.State.IsTerminal()) throw NotCancelable(request.Id);

        // Executions here stop at once; the other instances stop theirs when the request reaches them.
        _engine.CancelRunning(key);
        await _engine.RequestCancelElsewhereAsync(key, cancellationToken).ConfigureAwait(false);

        // Without an agent for the tenant, the default cancel runs (the task is marked canceled), so a task whose agent is
        // gone can still be cancelled.
        await using var agent = await _engine.RentAgentAsync(call.Tenant, CancelOnlyAgent.Instance, cancellationToken).ConfigureAwait(false);
        var exec = await _engine.StartCancelAsync(key, record.Task, request.Metadata, agent, card.Capabilities.PushNotifications == true).ConfigureAwait(false);
        exec.Detach();

        await _engine.WaitUntilStoppedAsync(key, _options.CancelGracePeriod, cancellationToken).ConfigureAwait(false);

        record = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        var state = record.Task.Status.State;
        if (state == TaskState.Canceled) return Shape(record.Task, null);
        if (!state.IsTerminal() && await _engine.IsRunningAnywhereAsync(key, cancellationToken).ConfigureAwait(false))
        {
            // The execution has not stopped within the grace period: the cancel is requested, not done. Only the response says
            // so (the stored task is not changed); its later CANCELED arrives through the normal stream.
            var pending = Shape(record.Task, null);
            pending.Metadata = pending.Metadata is null ? [] : new(pending.Metadata);
            pending.Metadata[A2AProtocol.CancelRequestedKey] = TrueElement;
            return pending;
        }
        throw NotCancelable(request.Id); // the agent refused and nothing runs, or the task ended otherwise
    }

    /// <summary>Runs CancelTask for a tenant without an agent: the default cancel (the task is marked canceled).</summary>
    private sealed class CancelOnlyAgent : IAgentExecutor
    {
        public static readonly CancelOnlyAgent Instance = new();

        public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(NoAgentText);
    }

    /// <summary>
    /// Subscribes to a task (spec §3.1.6), or resumes a stream (spec design §7.4). Events never repeat and no sequence is skipped.
    /// </summary>
    /// <param name="request">The task to stream.</param>
    /// <param name="call">The caller.</param>
    /// <param name="resumeAfter">
    /// Null for a fresh subscribe: a task snapshot, then live events; a terminal task is UnsupportedOperation (spec-exact).
    /// Otherwise the last sequence the client saw (from <c>Last-Event-ID</c>):
    /// <list type="bullet">
    /// <item>still retained: the events after it, then live events;</item>
    /// <item>older than the retained events, nothing retained, beyond the last event, or negative (a cursor that could not be
    /// used): a task snapshot, stamped with the last sequence, then live events;</item>
    /// <item>task terminal and the cursor at or past its last event: a task snapshot, then the stream ends.</item>
    /// </list>
    /// </param>
    /// <param name="onCaughtUp">See <see cref="IA2ARequestHandler.SubscribeToTaskAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public IAsyncEnumerable<SequencedEvent> SubscribeToTaskAsync(SubscribeToTaskRequest request, ServerCallContext call, long? resumeAfter = null,
        Action? onCaughtUp = null, CancellationToken cancellationToken = default) =>
        StreamAsync(SubscribeOperation, request.Tenant, call, request,
            (r, c, ct) => new(_streamer.SubscribeAsync(r, c, resumeAfter, onCaughtUp, SubscribeOperation, ct)), cancellationToken);

    /// <summary>Open streams counted against the stream limits. For tests.</summary>
    internal int OpenStreams => _streamer.OpenStreams;

    // =====================================================================
    // Push notification configs
    // =====================================================================

    /// <summary>CreateTaskPushNotificationConfig (spec §3.1.7). Validates the webhook URL, stores the registration and returns it with secrets removed.</summary>
    public async Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(config.Tenant);
        return await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.CreateTaskPushNotificationConfig), call, config, _createPush, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TaskPushNotificationConfig> CreateTaskPushNotificationConfigCoreAsync(TaskPushNotificationConfig config, ServerCallContext call, CancellationToken cancellationToken)
    {
        RequestCards.RequirePush(await _requestCards.RequireAgentForTaskAsync(call, config.TaskId, cancellationToken).ConfigureAwait(false));
        A2AValidator.ValidatePushConfig(config, "config", requireTaskId: true);
        var key = Key(call, config.TaskId!);
        _ = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(config.TaskId!);
        return await _pushConfigs.CreateAsync(key, config, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>GetTaskPushNotificationConfig (spec §3.1.8). Returns one registration with secrets removed.</summary>
    public async Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        return await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.GetTaskPushNotificationConfig), call, request, _getPush, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TaskPushNotificationConfig> GetTaskPushNotificationConfigCoreAsync(GetTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        RequestCards.RequirePush(await _requestCards.RequireAgentForTaskAsync(call, request.TaskId, cancellationToken).ConfigureAwait(false));
        A2AValidator.RequireId(request.TaskId, "taskId");
        A2AValidator.RequireId(request.Id, "id");
        var key = Key(call, request.TaskId);
        _ = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.TaskId);
        return await _pushConfigs.GetAsync(key, request.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>ListTaskPushNotificationConfigs (spec §3.1.9). Returns the registrations of a task, 100 per page at most, with secrets removed.</summary>
    public async Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        return await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.ListTaskPushNotificationConfigs), call, request, _listPush, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsCoreAsync(ListTaskPushNotificationConfigsRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        RequestCards.RequirePush(await _requestCards.RequireAgentForTaskAsync(call, request.TaskId, cancellationToken).ConfigureAwait(false));
        A2AValidator.RequireId(request.TaskId, "taskId");
        var key = Key(call, request.TaskId);
        _ = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.TaskId);
        return await _pushConfigs.ListAsync(key, request.PageSize ?? 0, request.PageToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DeleteTaskPushNotificationConfig (spec §3.1.10). Idempotent: a registration that does not exist is not an error. An unknown task is TaskNotFound.</summary>
    public async Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.DeleteTaskPushNotificationConfig), call, request, _deletePush, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<object?> DeleteTaskPushNotificationConfigCoreAsync(DeleteTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        RequestCards.RequirePush(await _requestCards.RequireAgentForTaskAsync(call, request.TaskId, cancellationToken).ConfigureAwait(false));
        A2AValidator.RequireId(request.TaskId, "taskId");
        A2AValidator.RequireId(request.Id, "id");
        var key = Key(call, request.TaskId);
        _ = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.TaskId);
        await _pushConfigs.DeleteAsync(key, request.Id, cancellationToken).ConfigureAwait(false);
        return null;
    }

    // =====================================================================
    // Extended Agent Card
    // =====================================================================

    /// <summary>
    /// GetExtendedAgentCard (spec §3.1.11). Needs an authenticated caller and a card from <see cref="IAgentCardProvider.GetExtendedAsync"/>
    /// (by default <see cref="A2AServerOptions.ExtendedAgentCardProvider"/>, or the <see cref="TenantCardProvider"/>).
    /// </summary>
    public async Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        return await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.GetExtendedAgentCard), call, request, _extendedCard, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AgentCard> GetExtendedAgentCardCoreAsync(GetExtendedAgentCardRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        var publicCard = await _requestCards.RequireAgentAsync(call, cancellationToken).ConfigureAwait(false);
        if (publicCard.Capabilities.ExtendedAgentCard != true)
            throw new A2AException(A2AErrorCode.UnsupportedOperation, "This agent does not provide an extended agent card.");
        if (!call.IsAuthenticated) throw new A2AException(A2AErrorCode.Unauthenticated, "The extended agent card requires authentication.");
        var card = await _cards.GetExtendedAsync(call, publicCard, cancellationToken).ConfigureAwait(false);
        return card ?? throw new A2AException(A2AErrorCode.ExtendedAgentCardNotConfigured);
    }

    // =====================================================================
    // Extension operations
    // =====================================================================

    /// <summary>
    /// Runs the extension operation registered as <paramref name="method"/> through the operation pipeline (see
    /// <see cref="IA2ARequestHandler.InvokeExtensionAsync"/>). Throws MethodNotFound when no extension operation has that name.
    /// </summary>
    public async Task<JsonElement> InvokeExtensionAsync(string method, JsonElement parameters, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (method is null || !_extensions.TryGetValue(method, out var extension))
            throw new A2AException(A2AErrorCode.MethodNotFound, $"Method '{method}' not found");
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("tenant", out var tenant) && tenant.ValueKind == JsonValueKind.String)
            call.BindTenant(tenant.GetString());
        return await _pipeline.RunExtensionAsync(extension.Descriptor, call, parameters, extension.Operation.Handler, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Status text of a task failed because its updates could not be stored (see <see cref="TaskExecutionEngine"/>).</summary>
    internal const string StoreFailureText = TaskExecutionEngine.StoreFailureText;

    // =====================================================================
    // Helpers
    // =====================================================================

    private static TaskKey Key(ServerCallContext call, string taskId) => new(call.Tenant, call.Principal, taskId);

    private AgentTask Shape(AgentTask task, int? historyLength, bool includeArtifacts = true) =>
        TaskShaper.Shape(task, historyLength, _options.Limits, includeArtifacts);

    private static A2AException NotCancelable(string taskId) =>
        new(A2AErrorCode.TaskNotCancelable) { Metadata = new() { ["taskId"] = taskId } };

    private static string ScopeKey(string tenant, string taskId) => TaskExecutionEngine.ScopeKey(tenant, taskId);

    /// <summary>Waits for all running executions to finish. For tests and graceful shutdown.</summary>
    public Task WhenIdleAsync() => _engine.WhenIdleAsync();

    private int _disposed;

    /// <summary>
    /// Stops running executions. Safe to call more than once. The <see cref="TaskExecutionEngine"/> is not disposed here: its
    /// owner (the DI container) does that.
    /// </summary>
    /// <remarks>Disposing the handler alone only cancels running executions; the engine is disposed by the container.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _engine.Shutdown();
    }
}
