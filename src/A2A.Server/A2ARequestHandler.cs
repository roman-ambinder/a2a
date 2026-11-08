using System.Runtime.CompilerServices;
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
/// <see cref="Telemetry.A2AServerTelemetry"/>) around the <see cref="A2AFilterStage.BeforeAuthorization"/> filters, authorization, the <see cref="A2AFilterStage.AfterAuthorization"/>
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
    private readonly CallValidator _validator;
    private readonly PushConfigService _pushConfigs;
    private readonly TaskExecutionEngine _engine;
    private readonly TaskStreamer _streamer;
    private readonly AcceptMessage _accept;
    private readonly OperationPipeline _pipeline;

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

    private static readonly A2AOperationDescriptor SendStreamingOperation = A2AOperationDescriptor.For(A2AOperation.SendStreamingMessage);
    private static readonly A2AOperationDescriptor SubscribeOperation = A2AOperationDescriptor.For(A2AOperation.SubscribeToTask);

    /// <summary>Creates the handler. In an ASP.NET Core app the DI container does this; the parameters are the registered services.</summary>
    /// <remarks>
    /// The handler does not own <paramref name="engine"/>: its owner (the DI container) disposes it. The pipeline is built here,
    /// once, from <paramref name="filters"/> in their order (registered with <see cref="A2AServerBuilder.AddOperationFilter{T}"/>).
    /// </remarks>
    public A2ARequestHandler(
        TaskStore tasks,
        EventStore events,
        IdempotencyCoordinator idempotency,
        A2AAuthorizationHandler authorization,
        A2AServerOptions options,
        CallValidator validator,
        PushConfigService pushConfigs,
        TaskExecutionEngine engine,
        TaskStreamer streamer,
        IEnumerable<A2AOperationFilterRegistration> filters)
    {
        _tasks = tasks;
        _events = events;
        _idempotency = idempotency;
        _bus = engine.Bus; // the bus the engine publishes to: SendMessage subscribes to the same one
        _options = options;
        _validator = validator;
        _pushConfigs = pushConfigs;
        _engine = engine;
        _streamer = streamer;
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

    /// <summary>The public Agent Card from <see cref="A2AServerOptions.AgentCard"/>.</summary>
    public AgentCard AgentCard => _options.AgentCard;

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
        var (claim, replay) = await _idempotency.BeginAsync(request, call, cancellationToken).ConfigureAwait(false);
        if (replay is not null) return await _idempotency.ReplayAsync(replay, request, call, _engine.WaitUntilSettledAsync, cancellationToken).ConfigureAwait(false);
        var taskId = string.IsNullOrEmpty(request.Message?.TaskId) ? TaskIds.New() : request.Message.TaskId;
        var key = Key(call, taskId);
        var scope = ScopeKey(call.Tenant, taskId);
        // Subscribe before the agent starts, so no event of another execution is missed.
        using var sub = _bus.Subscribe(scope);
        var (exec, afterSequence) = await AcceptAsync(request, call, claim, taskId, cancellationToken).ConfigureAwait(false);
        var reply = await _streamer.WaitForOutcomeAsync(exec, key, sub, afterSequence, request.Configuration?.ReturnImmediately == true,
            cancellationToken).ConfigureAwait(false);
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
    /// <see cref="A2ALimits.StreamReauthInterval"/> and <see cref="A2ALimits.MaxStreamDuration"/>.
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
    private sealed record Prepared(AgentRequestContext Context, TaskPushNotificationConfig? PushConfig, long LastSequence);

    /// <summary>
    /// Accepts the message and starts the agent. The idempotency claim is abandoned when the message is refused or the agent
    /// cannot start; a follow-up records its task at once (the message is already in the task's history), and a new task
    /// records it when it is first stored.
    /// </summary>
    /// <returns>The execution, and the task's last sequence before it (0 for a new task; see <see cref="Prepared.LastSequence"/>).</returns>
    private async Task<(Execution.Execution Execution, long AfterSequence)> AcceptAsync(SendMessageRequest request, ServerCallContext call, IdempotencyClaim claim, string? newTaskId, CancellationToken ct)
    {
        try
        {
            var prepared = await PrepareAsync(request, call, ct, newTaskId).ConfigureAwait(false);
            if (prepared.Context.IsNewTask) return (_engine.Start(prepared.Context, prepared.PushConfig, claim), 0);

            // Record after the execution is registered, so a blocking retry sees it running and waits for its outcome.
            var exec = _engine.Start(prepared.Context, prepared.PushConfig, IdempotencyClaim.None);
            var taskId = prepared.Context.TaskId;
            await _idempotency.RecordAsync(claim, new IdempotencyOutcome.TaskCreated(taskId, prepared.LastSequence), taskId).ConfigureAwait(false);
            return (exec, prepared.LastSequence);
        }
        catch
        {
            await _idempotency.AbandonAsync(claim).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Prepared> PrepareAsync(SendMessageRequest request, ServerCallContext call, CancellationToken ct, string? newTaskId = null)
    {
        call.BindTenant(request.Tenant);
        A2AValidator.ValidateIncoming(request);
        RequestLimitValidator.Validate(request.Message, _options.Limits);
        // A copy: the caller's message is never changed (a retry with the same instance must hash the same). This copy goes to
        // the executor; the stored history gets copies of its own.
        var message = request.Message.DeepCopy();
        var isFollowUp = !string.IsNullOrEmpty(message.TaskId);
        var taskId = isFollowUp ? message.TaskId! : newTaskId ?? TaskIds.New();
        AgentTask? existing = null;
        long lastSequence = 0;
        string contextId;

        // Check the push config before anything changes, so a refused request leaves no trace and a retry is refused the same way.
        TaskPushNotificationConfig? push = null;
        if (request.Configuration?.TaskPushNotificationConfig is { } p)
        {
            RequirePush();
            push = p.DeepCopy(); // the caller's config is never changed
            push.TaskId = taskId;
            push.Id = string.IsNullOrEmpty(push.Id) ? TaskIds.New() : push.Id;
            push.Tenant = call.Tenant;
            await _pushConfigs.ValidateInlineAsync(Key(call, taskId), push, ct).ConfigureAwait(false);
        }

        if (isFollowUp)
        {
            using (await _engine.LockAsync(Key(call, taskId), ct).ConfigureAwait(false))
            {
                for (var attempt = 0; ; attempt++)
                {
                    var record = await _tasks.GetAsync(Key(call, taskId), ct).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(taskId);
                    var state = record.Task.Status.State;
                    if (!string.IsNullOrEmpty(message.ContextId) && message.ContextId != record.Task.ContextId)
                        throw A2AException.InvalidParams("message.contextId does not match the task's contextId");
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
                    if (push is not null) await _pushConfigs.EnsureCapacityAsync(Key(call, taskId), push.Id!, ct).ConfigureAwait(false);
                    contextId = record.Task.ContextId;
                    message.ContextId = contextId;
                    // The stored record belongs to the store: append to a copy, with a copy of the message the executor gets.
                    var next = record.Task.CopyForUpdate();
                    (next.History ??= []).Add(message.DeepCopy());
                    try
                    {
                        await _tasks.UpdateAsync(record with { Task = next }, record.Version, ct).ConfigureAwait(false);
                        existing = next;
                        // Still under the task lock. The message is committed: a cancelled request must not stop here, or
                        // the idempotency entry would be abandoned and a retry would append the message again.
                        lastSequence = await _events.LastSequenceAsync(Key(call, taskId), CancellationToken.None).ConfigureAwait(false);
                        break;
                    }
                    catch (ConcurrencyException) when (attempt < TaskExecutionEngine.MaxConflictRetries)
                    {
                        // The task changed under us: reload, re-check the state, and append again.
                    }
                }
            }
        }
        else
        {
            contextId = await ResolveContextAsync(message.ContextId, call, ct).ConfigureAwait(false);
            message.ContextId = contextId;
        }

        // A new task saves its push config when the task is created; a follow-up saves it now (capacity checked above), past
        // cancellation for the same reason as the last sequence: its message is already committed.
        if (push is not null && existing is not null) await _pushConfigs.SaveInlineAsync(Key(call, taskId), push, CancellationToken.None).ConfigureAwait(false);

        var context = new AgentRequestContext
        {
            Message = message,
            TaskId = taskId,
            ContextId = contextId,
            Task = existing?.DeepCopy(), // the executor's own view: changing it never changes the stored task
            Configuration = request.Configuration,
            Metadata = request.Metadata,
            Call = call,
        };
        return new Prepared(context, existing is null ? push : null, lastSequence);
    }

    /// <summary>
    /// Returns the context id for a new task and makes sure the context belongs to the caller. A context owned by
    /// someone else looks like a missing one.
    /// </summary>
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

    /// <summary>CancelTask (spec §3.1.5). Asks the running agent to stop, waits up to <see cref="A2AServerOptions.CancelGracePeriod"/>, and returns the task. Throws TaskNotCancelable when the task is already terminal.</summary>
    public async Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        return await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.CancelTask), call, request, _cancelTask, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AgentTask> CancelTaskCoreAsync(CancelTaskRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        A2AValidator.RequireId(request.Id, "id");
        var record = await _tasks.GetAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        if (record.Task.Status.State.IsTerminal()) throw NotCancelable(request.Id);

        var running = _engine.CancelRunning(Key(call, request.Id));
        var exec = _engine.StartCancel(Key(call, request.Id), record.Task, request.Metadata);
        exec.Detach();

        var waitAll = Task.WhenAll(running.Select(r => r.Completion).Append(exec.Completion));
        await Task.WhenAny(waitAll, Task.Delay(_options.CancelGracePeriod, cancellationToken)).ConfigureAwait(false);

        record = await _tasks.GetAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        if (record.Task.Status.State != TaskState.Canceled) throw NotCancelable(request.Id);
        return Shape(record.Task, null);
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
        RequirePush();
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
        RequirePush();
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
        RequirePush();
        A2AValidator.RequireId(request.TaskId, "taskId");
        var key = Key(call, request.TaskId);
        _ = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.TaskId);
        return await _pushConfigs.ListAsync(key, request.PageSize ?? 0, request.PageToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DeleteTaskPushNotificationConfig (spec §3.1.10). Throws when the registration does not exist.</summary>
    public async Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.DeleteTaskPushNotificationConfig), call, request, _deletePush, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<object?> DeleteTaskPushNotificationConfigCoreAsync(DeleteTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        RequirePush();
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

    /// <summary>GetExtendedAgentCard (spec §3.1.11). Needs an authenticated caller and a card from <see cref="A2AServerOptions.ExtendedAgentCardProvider"/>.</summary>
    public async Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        return await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.GetExtendedAgentCard), call, request, _extendedCard, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AgentCard> GetExtendedAgentCardCoreAsync(GetExtendedAgentCardRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        if (_options.AgentCard.Capabilities.ExtendedAgentCard != true)
            throw new A2AException(A2AErrorCode.UnsupportedOperation, "This agent does not provide an extended agent card.");
        if (!call.IsAuthenticated) throw new A2AException(A2AErrorCode.Unauthenticated, "The extended agent card requires authentication.");
        var card = _options.ExtendedAgentCardProvider is null ? null : await _options.ExtendedAgentCardProvider(call, cancellationToken).ConfigureAwait(false);
        return card ?? throw new A2AException(A2AErrorCode.ExtendedAgentCardNotConfigured);
    }

    /// <summary>Status text of a task failed because its updates could not be stored (see <see cref="TaskExecutionEngine"/>).</summary>
    internal const string StoreFailureText = TaskExecutionEngine.StoreFailureText;

    // =====================================================================
    // Helpers
    // =====================================================================

    private static TaskKey Key(ServerCallContext call, string taskId) => new(call.Tenant, call.Principal, taskId);

    private AgentTask Shape(AgentTask task, int? historyLength, bool includeArtifacts = true) =>
        TaskShaper.Shape(task, historyLength, _options.Limits, includeArtifacts);

    private void RequirePush()
    {
        if (_options.AgentCard.Capabilities.PushNotifications != true)
            throw new A2AException(A2AErrorCode.PushNotificationNotSupported);
    }

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
