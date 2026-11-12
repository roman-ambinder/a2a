using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using A2A.Delegation;
using A2A.Diagnostics;
using A2A.Elicitations;
using A2A.Json;
using A2A.Server.Agents;
using A2A.Server.Audit;
using A2A.Server.Authorization;
using A2A.Server.Execution;
using A2A.Server.Idempotency;
using A2A.Server.Pipeline;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using A2A.Server.Telemetry;
using A2A.Server.Uploads;
using A2A.Validation;

namespace A2A.Server;

/// <summary>
///     The default <see cref="IA2ARequestHandler" />: transport-agnostic implementation of every A2A operation. Protocol
///     bindings
///     (JSON-RPC, HTTP+JSON, gRPC) resolve the interface, parse the wire format, build a <see cref="ServerCallContext" />,
///     and call
///     these methods. Errors are thrown as <see cref="A2AException" />.
///     Every operation binds the tenant from the request, then runs through the operation pipeline: telemetry (see
///     the spans and counters named in <see cref="A2A.Diagnostics.A2ATelemetry" />) around the
///     <see cref="A2AFilterStage.BeforeAuthorization" /> filters, authorization, the
///     <see cref="A2AFilterStage.AfterAuthorization" />
///     filters, then the operation (see <see cref="IA2AOperationFilter" />).
///     The tasks, messages and events these methods return may share instances with the stores: serialize them, never
///     change them.
/// </summary>
public sealed partial class A2ARequestHandler : IA2ARequestHandler, IDisposable
{
    /// <summary>The operation name an authorization handler sees for an inbound file upload.</summary>
    public const string UploadOperationName = "a2a.files.upload";

    /// <summary>The operation name an authorization handler sees for the owner's download of an uploaded file.</summary>
    public const string ReadUploadOperationName = "a2a.files.read";

    /// <summary>The refusal text for a tenant that no agent serves (its card is null).</summary>
    internal const string NoAgentText = RequestCards.NoAgentText;

    /// <summary>
    ///     Task metadata key (server-owned: the agent cannot write it) a committed follow-up leaves: <c>sequence:messageId</c>
    ///     , the
    ///     task's last event sequence just before the message was committed, and the message's id. While the task has no event
    ///     after that sequence, the agent did not run for the message (see <see cref="PendingStartAsync" />).
    /// </summary>
    internal const string PendingStartKey = "a2a.followUp.pendingStart";

    private const string NotFound = "contextId not found";

    /// <summary>Status text of a task failed because its updates could not be stored (see <see cref="TaskExecutionEngine" />).</summary>
    internal const string StoreFailureText = TaskExecutionEngine.StoreFailureText;

    private static readonly A2AOperationDescriptor SendStreamingOperation =
        A2AOperationDescriptor.For(A2AOperation.SendStreamingMessage);

    private static readonly string SendMessageOperationName = A2AOperationDescriptor.For(A2AOperation.SendMessage).Name;
    private static readonly string CancelOperationName = A2AOperationDescriptor.For(A2AOperation.CancelTask).Name;

    private static readonly A2AOperationDescriptor SubscribeOperation =
        A2AOperationDescriptor.For(A2AOperation.SubscribeToTask);

    private static readonly A2AOperationDescriptor UploadOperation =
        A2AOperationDescriptor.Extension(UploadOperationName);

    private static readonly A2AOperationDescriptor ReadUploadOperation =
        A2AOperationDescriptor.Extension(ReadUploadOperationName);

    private static readonly JsonElement TrueElement = JsonDocument.Parse("true").RootElement.Clone();
    private readonly AcceptMessage _accept;
    private readonly AnomalyDetector? _anomalies; // null unless A2AServerOptions.Anomalies.Enabled
    private readonly A2AAuditor _audit;
    private readonly A2AAuthorizationHandler _authorization;
    private readonly TaskEventBus _bus;
    private readonly Func<CancelTaskRequest, ServerCallContext, CancellationToken, ValueTask<AgentTask>> _cancelTask;
    private readonly IAgentCardProvider _cards;

    private readonly
        Func<TaskPushNotificationConfig, ServerCallContext, CancellationToken, ValueTask<TaskPushNotificationConfig>>
        _createPush;

    private readonly
        Func<DeleteTaskPushNotificationConfigRequest, ServerCallContext, CancellationToken, ValueTask<object?>>
        _deletePush;

    private readonly TaskExecutionEngine _engine;
    private readonly EventStore _events;

    private readonly Func<GetExtendedAgentCardRequest, ServerCallContext, CancellationToken, ValueTask<AgentCard>>
        _extendedCard;

    /// <summary>
    ///     The registered extension operations by method name (case-sensitive), each with its descriptor and its handler
    ///     wrapped
    ///     with the activation check (built once, so a call allocates no closure for it).
    /// </summary>
    private readonly Dictionary<string, (A2AExtensionOperation Operation, A2AOperationDescriptor Descriptor,
        Func<A2AOperationContext, JsonElement, CancellationToken, ValueTask<JsonElement>> Handler)> _extensions;

    private readonly Func<GetTaskPushNotificationConfigRequest, ServerCallContext, CancellationToken,
        ValueTask<TaskPushNotificationConfig>> _getPush;

    private readonly Func<GetTaskRequest, ServerCallContext, CancellationToken, ValueTask<AgentTask>> _getTask;
    private readonly IdempotencyCoordinator _idempotency;
    private readonly InboundFiles? _inbound;

    private readonly Func<ListTaskPushNotificationConfigsRequest, ServerCallContext, CancellationToken,
        ValueTask<ListTaskPushNotificationConfigsResponse>> _listPush;

    private readonly Func<ListTasksRequest, ServerCallContext, CancellationToken, ValueTask<ListTasksResponse>>
        _listTasks;

    private readonly ArtifactObjectStore? _objects;
    private readonly A2AServerOptions _options;
    private readonly OperationPipeline _pipeline;
    private readonly PushConfigService _pushConfigs;
    private readonly RequestCards _requestCards;

    // One cached delegate per operation, so a call through the pipeline creates no closure for its operation.
    private readonly Func<SendMessageRequest, ServerCallContext, CancellationToken, ValueTask<SendMessageResponse>>
        _sendMessage;

    private readonly
        Func<SendMessageRequest, ServerCallContext, CancellationToken, ValueTask<IAsyncEnumerable<SequencedEvent>>>
        _sendStreaming;

    private readonly SkillAuthorizer _skills;
    private readonly TaskStreamer _streamer;
    private readonly TaskStore _tasks;
    private readonly ITokenExchanger? _tokenExchanger;
    private readonly CallValidator _validator;

    private int _disposed;

    /// <summary>
    ///     Creates the handler. In an ASP.NET Core app the DI container does this; the parameters are the registered
    ///     services.
    /// </summary>
    /// <remarks>
    ///     The handler does not own <paramref name="engine" />: its owner (the DI container) disposes it. The pipeline is
    ///     built here,
    ///     once, from <paramref name="filters" /> in their order (registered with
    ///     <see cref="A2AServerBuilder.AddOperationFilter{T}" />).
    ///     <paramref name="extensionOperations" /> are the operations registered with
    ///     <see cref="A2AServerBuilder.AddExtensionOperation" />.
    ///     <paramref name="audit" /> writes the audit trail (see <see cref="A2AAuditSink" />); a handler built without it
    ///     audits nothing.
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
        ITokenExchanger? tokenExchanger = null,
        ArtifactObjectStore? objects = null,
        A2AAuditor? audit = null)
    {
        _audit = audit ?? A2AAuditor.None;
        _tokenExchanger = tokenExchanger;
        _objects = objects;
        _cards = cards;
        _authorization = authorization;
        _requestCards = new RequestCards(cards);
        ArgumentNullException.ThrowIfNull(extensionOperations);
        _extensions =
            new Dictionary<string, (A2AExtensionOperation Operation, A2AOperationDescriptor Descriptor,
                Func<A2AOperationContext, JsonElement, CancellationToken, ValueTask<JsonElement>> Handler)>(
                StringComparer.Ordinal);
        foreach (var extension in extensionOperations)
            if (!_extensions.TryAdd(extension.Method,
                    (extension, A2AOperationDescriptor.Extension(extension.Method), RequireActivation(extension))))
                throw new InvalidOperationException(
                    $"More than one extension operation is registered as '{extension.Method}'.");
        _tasks = tasks;
        _events = events;
        _idempotency = idempotency;
        _bus = engine.Bus; // the bus the engine publishes to: SendMessage subscribes to the same one
        _options = options;
        _validator = validator;
        _pushConfigs = pushConfigs;
        _engine = engine;
        if (objects is not null && options.ArtifactStorage.Uploads.Enabled)
            _inbound = new InboundFiles(objects, options.ArtifactStorage, engine.Clock);
        // Built here, not registered: it shares the handler's services and has no public surface of its own.
        _skills = new SkillAuthorizer(authorization, options.SkillAuthorization, cards, _requestCards, options, _audit);
        _streamer = new TaskStreamer(tasks, events, engine, idempotency, authorization, options, cards, _skills,
            _audit);
        _anomalies = AnomalyDetector.Create(options, engine.Clock);
        _accept = (r, c, claim, id, agent, ct) => AcceptAsync(r, c, claim, id, agent, true, ct); // one delegate for every stream
        // SDK operations that name a task (task deletion): a denial of one looks like a missing task.
        var taskScoped = _extensions.Values.Where(e => e.Operation.TaskIdMember is not null)
            .ToDictionary(e => e.Operation.Method, e => e.Operation.TaskIdMember!, StringComparer.Ordinal);
        _pipeline = new OperationPipeline(
            new AuthorizationFilter(authorization, options, _skills, _audit, taskScoped.Count == 0 ? null : taskScoped),
            filters);
        _sendMessage = SendMessageCoreAsync;
        _sendStreaming = (r, c, ct) =>
            new ValueTask<IAsyncEnumerable<SequencedEvent>>(
                _streamer.StreamExecutionAsync(r, c, _accept, SendStreamingOperation, ct));
        _getTask = GetTaskCoreAsync;
        _listTasks = ListTasksCoreAsync;
        _cancelTask = CancelTaskAuditedAsync;
        _createPush = CreateTaskPushNotificationConfigAuditedAsync;
        _getPush = GetTaskPushNotificationConfigCoreAsync;
        _listPush = ListTaskPushNotificationConfigsCoreAsync;
        _deletePush = DeleteTaskPushNotificationConfigAuditedAsync;
        _extendedCard = GetExtendedAgentCardCoreAsync;
    }

    /// <summary>Open streams counted against the stream limits. For tests.</summary>
    internal int OpenStreams => _streamer.OpenStreams;

    /// <summary>
    ///     The public Agent Card of the host: <see cref="IAgentCardProvider.Get" />, the completed copy of
    ///     <see cref="A2AServerOptions.AgentCard" />.
    ///     With a <see cref="TenantCardProvider" /> each tenant has its own card, and requests are checked against that one.
    /// </summary>
    public AgentCard AgentCard => _cards.Get();

    // =====================================================================
    // Call validation: version, extensions, authentication
    // =====================================================================

    /// <summary>
    ///     Checks protocol version, required extensions, and authentication; fills
    ///     <see cref="ServerCallContext.ActivatedExtensions" />.
    /// </summary>
    /// <remarks>
    ///     An unauthenticated caller is written to the audit trail as <see cref="A2AAuditAction.AccessDenied" />, without
    ///     waiting for
    ///     the sink: this method is synchronous, and a flood of anonymous calls must not pile up work.
    /// </remarks>
    public void ValidateCall(ServerCallContext call)
    {
        try
        {
            _validator.Validate(call);
        }
        catch (A2AException ex) when (ex.Code == A2AErrorCode.Unauthenticated)
        {
            _audit.RecordDetached(_audit.For(A2AAuditAction.AccessDenied, call)
                with
                {
                    Outcome = A2AAuditOutcome.Denied, Reason = "Unauthenticated"
                });
            throw;
        }
    }

    // =====================================================================
    // SendMessage / SendStreamingMessage
    // =====================================================================

    /// <summary>
    ///     Sends a message. Unless <see cref="SendMessageConfiguration.ReturnImmediately" /> is set, waits until the task is
    ///     terminal or interrupted, by an event of this execution or of any other execution of the task (for example
    ///     CancelTask),
    ///     or until no execution of the task is running any more.
    /// </summary>
    public async Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, ServerCallContext call,
        CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        return await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.SendMessage), call, request,
            _sendMessage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sends a message and streams the task's events, including those of other executions of the task (for example the
    ///     CANCELED event of a CancelTask), in sequence order and without repeats. The stream ends after the task's final
    ///     event,
    ///     or when this execution ended and no other execution of the task is running. The stream is limited like every
    ///     stream: see
    ///     <see cref="A2ALimits.MaxConcurrentStreamsPerTask" />, <see cref="A2ALimits.MaxConcurrentStreamsPerPrincipal" />,
    ///     <see cref="A2ALimits.StreamReauthInterval" /> and <see cref="A2ALimits.MaxStreamDuration" />. A retry that resumes
    ///     the task
    ///     instead of sending (the idempotency key's task, or a follow-up whose message id is already in the history) is
    ///     authorized
    ///     as <see cref="A2AOperation.SubscribeToTask" />, like a <paramref name="lastEventId" /> resume.
    /// </summary>
    /// <param name="request">The message to send.</param>
    /// <param name="call">The caller.</param>
    /// <param name="lastEventId">
    ///     A valid <paramref name="lastEventId" /> (a <see cref="StreamCursor" />, from the <c>Last-Event-ID</c> header) makes
    ///     the
    ///     call a reconnect: it resumes the task the cursor names, as a <see cref="SubscribeToTaskAsync" /> of the caller's
    ///     task,
    ///     and the message is not sent again. A resume is authorized as <see cref="A2AOperation.SubscribeToTask" /> with a
    ///     <see cref="SubscribeToTaskRequest" />. The header is ignored, and the message sent normally, when it is null, does
    ///     not
    ///     parse, or when the message names a different task than the cursor.
    /// </param>
    /// <param name="onCaughtUp">Passed to the resume (see <see cref="IA2ARequestHandler.SendStreamingMessageAsync" />).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public IAsyncEnumerable<SequencedEvent> SendStreamingMessageAsync(SendMessageRequest request,
        ServerCallContext call, string? lastEventId = null,
        Action? onCaughtUp = null, CancellationToken cancellationToken = default)
    {
        var messageTaskId = request.Message?.TaskId;
        if (request.Message is null)
            // A2A 1.1: client artifacts without a message: stored, then the stream follows the task like a subscription.
            return StreamAsync(SendStreamingOperation, request.Tenant, call, request, async (r, c, ct) =>
            {
                var updated = await StoreArtifactsOnlyAsync(r, c, ct).ConfigureAwait(false);
                var follow = new SubscribeToTaskRequest { Id = updated.Task.Id, Tenant = r.Tenant };
                return _streamer.SubscribeAsync(follow, c, null, onCaughtUp, SubscribeOperation, ct);
            }, cancellationToken);
        return StreamCursor.TryParse(lastEventId, out var taskId, out var sequence)
               && (string.IsNullOrEmpty(messageTaskId) ||
                   string.Equals(messageTaskId, taskId, StringComparison.Ordinal))
            ? SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId, Tenant = request.Tenant }, call, sequence,
                onCaughtUp, cancellationToken)
            : StreamAsync(SendStreamingOperation, request.Tenant, call, request, _sendStreaming, cancellationToken);
    }

    // =====================================================================
    // Tasks
    // =====================================================================

    /// <summary>GetTask (spec §3.1.3). Throws TaskNotFound when the task does not exist or belongs to another caller.</summary>
    public async Task<AgentTask> GetTaskAsync(GetTaskRequest request, ServerCallContext call,
        CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        return await _pipeline
            .RunAsync(A2AOperationDescriptor.For(A2AOperation.GetTask), call, request, _getTask, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     ListTasks (spec §3.1.4). Returns the caller's tasks, newest status first. The page size defaults to 50 and is
    ///     capped by <see cref="A2ALimits.MaxListPageSize" />.
    /// </summary>
    public async Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, ServerCallContext call,
        CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        return await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.ListTasks), call, request, _listTasks,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     CancelTask (spec §3.1.5). Asks the running agent to stop (on this instance directly, on the others through the
    ///     execution coordinator), waits up to <see cref="A2AServerOptions.CancelGracePeriod" />, and returns the task. When
    ///     an
    ///     execution still runs after that, the response is the task with <see cref="A2AProtocol.CancelRequestedKey" /> = true
    ///     in its
    ///     metadata (the cancel is requested, not done). Throws TaskNotCancelable when the task is already terminal.
    /// </summary>
    public async Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, ServerCallContext call,
        CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        return await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.CancelTask), call, request, _cancelTask,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Subscribes to a task (spec §3.1.6), or resumes a stream (spec design §7.4). Events never repeat and no sequence is
    ///     skipped.
    /// </summary>
    /// <param name="request">The task to stream.</param>
    /// <param name="call">The caller.</param>
    /// <param name="resumeAfter">
    ///     Null for a fresh subscribe: a task snapshot, then live events; a terminal task is UnsupportedOperation
    ///     (spec-exact).
    ///     Otherwise the last sequence the client saw (from <c>Last-Event-ID</c>):
    ///     <list type="bullet">
    ///         <item>still retained: the events after it, then live events;</item>
    ///         <item>
    ///             older than the retained events, nothing retained, beyond the last event, or negative (a cursor that could
    ///             not be
    ///             used): a task snapshot, stamped with the last sequence, then live events;
    ///         </item>
    ///         <item>task terminal and the cursor at or past its last event: a task snapshot, then the stream ends.</item>
    ///     </list>
    /// </param>
    /// <param name="onCaughtUp">See <see cref="IA2ARequestHandler.SubscribeToTaskAsync" />.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public IAsyncEnumerable<SequencedEvent> SubscribeToTaskAsync(SubscribeToTaskRequest request, ServerCallContext call,
        long? resumeAfter = null,
        Action? onCaughtUp = null, CancellationToken cancellationToken = default)
    {
        return StreamAsync(SubscribeOperation, request.Tenant, call, request,
            (r, c, ct) => new ValueTask<IAsyncEnumerable<SequencedEvent>>(_streamer.SubscribeAsync(r, c,
                resumeAfter ?? LastSeenGeneration(r, c), onCaughtUp,
                SubscribeOperation, ct)), cancellationToken);
    }

    // =====================================================================
    // Push notification configs
    // =====================================================================

    /// <summary>
    ///     CreateTaskPushNotificationConfig (spec §3.1.7). Validates the webhook URL, stores the registration and returns
    ///     it with secrets removed.
    /// </summary>
    public async Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(
        TaskPushNotificationConfig config, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(config.Tenant);
        return await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.CreateTaskPushNotificationConfig), call,
            config, _createPush, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>GetTaskPushNotificationConfig (spec §3.1.8). Returns one registration with secrets removed.</summary>
    public async Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(
        GetTaskPushNotificationConfigRequest request, ServerCallContext call,
        CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        return await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.GetTaskPushNotificationConfig), call,
            request, _getPush, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     ListTaskPushNotificationConfigs (spec §3.1.9). Returns the registrations of a task, 100 per page at most, with
    ///     secrets removed.
    /// </summary>
    public async Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(
        ListTaskPushNotificationConfigsRequest request, ServerCallContext call,
        CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        return await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.ListTaskPushNotificationConfigs), call,
            request, _listPush, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     DeleteTaskPushNotificationConfig (spec §3.1.10). Idempotent: a registration that does not exist is not an
    ///     error. An unknown task is TaskNotFound.
    /// </summary>
    public async Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request,
        ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.DeleteTaskPushNotificationConfig), call,
            request, _deletePush, cancellationToken).ConfigureAwait(false);
    }

    // =====================================================================
    // Extended Agent Card
    // =====================================================================

    /// <summary>
    ///     GetExtendedAgentCard (spec §3.1.11). Needs an authenticated caller and a card from
    ///     <see cref="IAgentCardProvider.GetExtendedAsync" />
    ///     (by default <see cref="A2AServerOptions.ExtendedAgentCardProvider" />, or the <see cref="TenantCardProvider" />).
    /// </summary>
    public async Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, ServerCallContext call,
        CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        return await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.GetExtendedAgentCard), call, request,
            _extendedCard, cancellationToken).ConfigureAwait(false);
    }

    // =====================================================================
    // Extension operations
    // =====================================================================

    /// <summary>
    ///     Runs the extension operation registered as <paramref name="method" /> through the operation pipeline (see
    ///     <see cref="IA2ARequestHandler.InvokeExtensionAsync" />). Throws MethodNotFound when no extension operation has that
    ///     name.
    /// </summary>
    public async Task<JsonElement> InvokeExtensionAsync(string method, JsonElement parameters, ServerCallContext call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (method is null || !_extensions.TryGetValue(method, out var extension))
            throw new A2AException(A2AErrorCode.MethodNotFound, $"Method '{method}' not found");
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("tenant", out var tenant) &&
            tenant.ValueKind == JsonValueKind.String)
            call.BindTenant(tenant.GetString());
        return await _pipeline
            .RunExtensionAsync(extension.Descriptor, call, parameters, extension.Handler, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Stops running executions. Safe to call more than once. The <see cref="TaskExecutionEngine" /> is not disposed here:
    ///     its
    ///     owner (the DI container) does that.
    /// </summary>
    /// <remarks>Disposing the handler alone only cancels running executions; the engine is disposed by the container.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _engine.Shutdown();
    }

    // =====================================================================
    // Operation pipeline
    // =====================================================================

    /// <summary>
    ///     A streaming operation. Lazy like the stream it returns: on the first read it binds the tenant and runs the
    ///     pipeline,
    ///     so a refusal surfaces from the first <c>MoveNextAsync</c>, before any event, and bindings turn it into an error
    ///     response.
    /// </summary>
    private async IAsyncEnumerable<SequencedEvent> StreamAsync<TRequest>(A2AOperationDescriptor operation,
        string? tenant, ServerCallContext call,
        TRequest request,
        Func<TRequest, ServerCallContext, CancellationToken, ValueTask<IAsyncEnumerable<SequencedEvent>>> open,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where TRequest : class
    {
        call.BindTenant(tenant);
        var events = await _pipeline.RunAsync(operation, call, request, open, cancellationToken).ConfigureAwait(false);
        await foreach (var e in events.WithCancellation(cancellationToken).ConfigureAwait(false))
            // The A2A 1.1 event kinds go only to 1.1 callers; every binding gets its events here.
            if (EventEncoder.IsVisibleTo(e.Event, call.ProtocolVersion))
                yield return e;
    }

    private async ValueTask<SendMessageResponse> SendMessageCoreAsync(SendMessageRequest request,
        ServerCallContext call, CancellationToken cancellationToken)
    {
        // A2A 1.1: client artifacts for an existing task, without a message: stored, and the task is the answer. No agent runs.
        if (request.Message is null)
        {
            var updated = await StoreArtifactsOnlyAsync(request, call, cancellationToken).ConfigureAwait(false);
            return new TaskResponse(Shape(updated.Task, request.Configuration?.HistoryLength,
                timelineLength: request.Configuration?.TimelineLength));
        }

        await _requestCards.RequireAgentAsync(call, cancellationToken).ConfigureAwait(false);
        // Before the idempotency claim and anything stored: a tenant without an agent leaves no trace.
        await using var agent = await _engine.RequireAgentAsync(call.Tenant, cancellationToken).ConfigureAwait(false);
        var (claim, replay) = await _idempotency.BeginAsync(request, call, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            await agent.DisposeAsync().ConfigureAwait(false); // nothing runs
            return await _idempotency
                .ReplayAsync(replay, request, call, _engine.WaitUntilSettledAsync, cancellationToken)
                .ConfigureAwait(false);
        }

        var taskId = string.IsNullOrEmpty(request.Message?.TaskId) ? TaskIds.New() : request.Message.TaskId;
        var key = Key(call, taskId);
        var scope = ScopeKey(call.Tenant, taskId);
        // Subscribe before the agent starts, so no event of another execution is missed.
        using var sub = _bus.Subscribe(scope);
        var (accepted, afterSequence, acceptedStatusTimestamp) =
            await AcceptAsync(request, call, claim, taskId, agent, false, cancellationToken)
                .ConfigureAwait(false);
        if (accepted is not { } exec)
        {
            // A duplicate follow-up: nothing started, so there is nothing to wait for.
            var current = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ??
                          throw A2AException.TaskNotFound(taskId);
            return new TaskResponse(Shape(current.Task, request.Configuration?.HistoryLength,
                timelineLength: request.Configuration?.TimelineLength));
        }

        var reply = await _streamer.WaitForOutcomeAsync(exec, key, sub, afterSequence, acceptedStatusTimestamp,
            request.Configuration?.ReturnImmediately == true, cancellationToken).ConfigureAwait(false);
        if (reply is not null) return new MessageResponse(reply);

        var record = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false)
                     ?? throw new A2AException(A2AErrorCode.InvalidAgentResponse,
                         "The agent finished without replying or creating a task.");
        return new TaskResponse(Shape(record.Task, request.Configuration?.HistoryLength,
            timelineLength: request.Configuration?.TimelineLength));
    }

    /// <summary>
    ///     Accepts the message and starts the agent. The idempotency claim is abandoned when the message is refused or the
    ///     agent
    ///     cannot start; a follow-up records its task at once (the message is already in the task's history), and a new task
    ///     records it when it is first stored.
    /// </summary>
    /// <returns>
    ///     The execution, the task's last sequence before it (0 for a new task; see <see cref="Prepared.LastSequence" />), and
    ///     for a
    ///     follow-up the timestamp of the task's status when the message was committed. The execution is null for a duplicate
    ///     follow-up (see <see cref="Prepared.Duplicate" />): nothing was started.
    /// </returns>
    /// <remarks>
    ///     When a follow-up's execution cannot start after its message was committed, the task is failed
    ///     (<see cref="TaskExecutionEngine.StartFailureText" />), so it does not wait for good for an agent that never runs.
    /// </remarks>
    private async Task<(Execution.Execution? Execution, long AfterSequence, DateTimeOffset? AcceptedStatusTimestamp)>
        AcceptAsync(
            SendMessageRequest request, ServerCallContext call, IdempotencyClaim claim, string? newTaskId,
            RentedAgent agent, bool streaming, CancellationToken ct)
    {
        var ticket = new AuditTicket();
        (Execution.Execution? Execution, long AfterSequence, DateTimeOffset? AcceptedStatusTimestamp) result;
        try
        {
            result = await AcceptCoreAsync(request, call, claim, newTaskId, agent, ticket, streaming, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Written here, outside the code that holds the pending start and the task lock. Only what has begun gets a row.
            await FinishAuditAsync(ticket, ex).ConfigureAwait(false);
            throw;
        }

        await FinishAuditAsync(ticket, null).ConfigureAwait(false);
        return result;
    }

    private async ValueTask FinishAuditAsync(AuditTicket ticket, Exception? failure)
    {
        if (ticket.Message is { } message)
        {
            var final = message with
            {
                Action = ticket.Action ?? message.Action, Resource = ticket.Resource ?? message.Resource,
                Reason = ticket.Reason
            };
            if (failure is null) await _audit.SucceedAsync(final).ConfigureAwait(false);
            else await _audit.FailAsync(final, failure).ConfigureAwait(false);
        }

        if (ticket.Push is { } push)
        {
            var final = push with { Reason = ticket.PushReason };
            if (failure is null) await _audit.SucceedAsync(final).ConfigureAwait(false);
            else await _audit.FailAsync(final, failure).ConfigureAwait(false);
        }
    }

    private async Task<(Execution.Execution? Execution, long AfterSequence, DateTimeOffset? AcceptedStatusTimestamp)>
        AcceptCoreAsync(
            SendMessageRequest request, ServerCallContext call, IdempotencyClaim claim, string? newTaskId,
            RentedAgent agent, AuditTicket ticket, bool streaming, CancellationToken ct)
    {
        try
        {
            // Before anything is stored (a refused follow-up leaves no trace) and after the idempotency replay check (a replay
            // takes no slot). The slot goes to the agent: the execution takes it, and a refusal or failure releases it.
            agent.AttachSlot(await _engine.AdmitAsync(call.Tenant, claim, newTaskId, ct).ConfigureAwait(false));
            var prepared = await PrepareAsync(request, call, ticket, ct, newTaskId).ConfigureAwait(false);
            try
            {
                // The request resolved the tenant's card already; its push capability holds for the whole execution, so no card
                // provider call happens per event (or under a task lock).
                var pushEnabled = call.ResolvedCard?.Card?.Capabilities.PushNotifications == true;
                if (prepared.Context.IsNewTask)
                {
                    var started = await _engine.StartAsync(prepared.Context, prepared.PushConfig, claim, agent,
                        pushEnabled).ConfigureAwait(false);
                    // Counted once the task exists: a refused or failed start, a follow-up and a replay are not creations.
                    _anomalies?.TaskCreated(call, streaming ? SendStreamingOperation.Name : SendMessageOperationName);
                    return (started, 0, null);
                }
                if (prepared.Duplicate)
                {
                    // The message is already in the history (an earlier request of it failed after the commit): its outcome is
                    // the task as it is. A retry with this claim gets the task as it is too.
                    await _idempotency.RecordAsync(claim, new IdempotencyOutcome.TaskCreated(prepared.Context.TaskId),
                        prepared.Context.TaskId).ConfigureAwait(false);
                    return (null, 0, null);
                }

                // Record after the execution is registered (on every instance), so a blocking retry sees it running and waits for
                // its outcome.
                var taskId = prepared.Context.TaskId;
                var acceptedStatus = prepared.AcceptedStatus!;
                Execution.Execution exec;
                try
                {
                    exec = await _engine
                        .StartAsync(prepared.Context, prepared.PushConfig, IdempotencyClaim.None, agent, pushEnabled)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // The message is committed, but no agent runs for it: fail the task rather than leave it waiting for good.
                    await _engine.TryFailUnstartedAsync(Key(call, taskId), acceptedStatus).ConfigureAwait(false);
                    throw;
                }

                await _idempotency.RecordAsync(claim,
                        new IdempotencyOutcome.TaskCreated(taskId, prepared.LastSequence, acceptedStatus.Timestamp),
                        taskId)
                    .ConfigureAwait(false);
                return (exec, prepared.LastSequence, acceptedStatus.Timestamp);
            }
            finally
            {
                // The execution is registered (or the start failed): a retry now sees it running (or the failed task).
                if (prepared.StartPending) _engine.EndStart(Key(call, prepared.Context.TaskId));
            }
        }
        catch
        {
            await _idempotency.AbandonAsync(claim).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     Throws ContentTypeNotSupported when a part's media type is not accepted by the card (default input modes plus every
    ///     skill's input modes). Parts without a media type are never checked, and then the card is not even resolved. A card
    ///     that
    ///     declares no modes accepts everything.
    /// </summary>
    private async ValueTask RequireSupportedMediaTypesAsync(Message message, ServerCallContext call,
        CancellationToken ct)
    {
        var hasMediaType = false;
        foreach (var part in message.Parts)
            if (!string.IsNullOrEmpty(part.MediaType))
            {
                hasMediaType = true;
                break;
            }

        if (!hasMediaType) return;

        var card = await _requestCards.RequireAgentAsync(call, ct).ConfigureAwait(false);
        if (!HasAnyInputMode(card)) return;
        foreach (var part in message.Parts)
        {
            if (string.IsNullOrEmpty(part.MediaType) || AcceptsMediaType(card, part.MediaType)) continue;
            throw new A2AException(A2AErrorCode.ContentTypeNotSupported,
                $"Media type '{part.MediaType}' is not supported by this agent.");
        }
    }

    private static bool HasAnyInputMode(AgentCard card)
    {
        if (card.DefaultInputModes is { Count: > 0 }) return true;
        if (card.Skills is not null)
            foreach (var skill in card.Skills)
                if (skill.InputModes is { Count: > 0 })
                    return true;
        return false;
    }

    private static bool AcceptsMediaType(AgentCard card, string mediaType)
    {
        var wanted = MediaTypeEssence(mediaType);
        if (card.DefaultInputModes is not null)
            foreach (var mode in card.DefaultInputModes)
                if (ModeMatches(mode, wanted))
                    return true;
        if (card.Skills is not null)
            foreach (var skill in card.Skills)
                if (skill.InputModes is not null)
                    foreach (var mode in skill.InputModes)
                        if (ModeMatches(mode, wanted))
                            return true;
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
        if (m.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
            m.Equals("*/*", StringComparison.Ordinal)) return true;
        if (m.Length > 2 && m[^2..].SequenceEqual("/*"))
        {
            var slash = wanted.IndexOf('/');
            return slash > 0 && m[..^1].Equals(wanted[..(slash + 1)], StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    ///     What a message is, as far as the request shows: the first audit row of it. A follow-up that answers a task
    ///     that waits for approval is refined once the task is read.
    /// </summary>
    private static A2AAuditAction ClassifyMessage(Message message, bool isFollowUp, bool answersForm)
    {
        if (!isFollowUp) return A2AAuditAction.TaskCreated;
        if (answersForm) return A2AAuditAction.ElicitationAnswered;
        return message.Metadata is not null && message.Metadata.ContainsKey(A2AProtocol.ApprovalHashKey)
            ? A2AAuditAction.ApprovalGranted
            : A2AAuditAction.FollowUpSent;
    }

    private async Task<Prepared> PrepareAsync(SendMessageRequest request, ServerCallContext call, AuditTicket ticket,
        CancellationToken ct, string? newTaskId = null)
    {
        var answersForm = await ValidateRequestAsync(request, call, ct).ConfigureAwait(false);
        // A copy: the caller's message is never changed (a retry with the same instance must hash the same). This copy goes to
        // the executor; the stored history gets copies of its own.
        var message = request.Message!.DeepCopy();
        // The time the server accepted it, before any copy: the history and the agent see this value; a client-sent one is overwritten.
        MessageStamp.Set(message, _engine.UtcNow);
        var isFollowUp = !string.IsNullOrEmpty(message.TaskId);
        if (answersForm && !isFollowUp)
            throw ElicitationValidator.Refuse("An answer to a form needs the id of the task that asked.");
        var taskId = isFollowUp ? message.TaskId! : newTaskId ?? TaskIds.New();
        var userArtifacts = await CheckArtifactUpdatesAsync(request, call, taskId, ct).ConfigureAwait(false);

        var push = await PreparePushConfigAsync(request, call, taskId, ct).ConfigureAwait(false);
        await BeginAuditAsync(call, ticket, message, isFollowUp, answersForm, taskId, push, ct).ConfigureAwait(false);

        Accepted accepted;
        // A2A 1.1 (§3.2.2): the precondition is part of the 1.1 request; a 1.0 caller cannot send it.
        var ifGenerationMatch = A2AProtocol.IsVersion11OrLater(call.ProtocolVersion)
            ? request.Configuration?.IfGenerationMatch
            : null;
        if (isFollowUp)
        {
            accepted = await AcceptFollowUpAsync(call, ticket, message, taskId, answersForm, push, ifGenerationMatch,
                userArtifacts, ct).ConfigureAwait(false);
        }
        else
        {
            var newContextId = await ResolveContextAsync(message.ContextId, call, ct).ConfigureAwait(false);
            message.ContextId = newContextId;
            if (userArtifacts is not null)
                foreach (var u in userArtifacts)
                    u.ContextId = newContextId;
            // A new task: nothing is stored yet, and no start was claimed, so a refusal needs no EndStart.
            if (_inbound is not null) await _inbound.AttachAsync(call, taskId, message, ct).ConfigureAwait(false);
            accepted = new Accepted(message, newContextId, null, 0, false, false, ApprovalState.None, null, null);
        }

        // A new task saves its push config when the task is created; a follow-up saved it above, before its message was committed.

        if (isFollowUp) ClassifyAccepted(ticket, accepted);

        try
        {
            var context = new AgentRequestContext
            {
                Message = accepted.Message,
                TaskId = taskId,
                ContextId = accepted.ContextId,
                Conversation = new ConversationMemory(_tasks, call.Tenant, call.Principal, accepted.ContextId, taskId),
                Task = accepted.Existing
                    ?.DeepCopy(), // the executor's own view: changing it never changes the stored task
                Configuration = request.Configuration,
                Metadata = request.Metadata,
                Call = call,
                Approval = accepted.Approval,
                ElicitationAnswer = accepted.Elicitation,
                TokenExchanger = _tokenExchanger,
                SkillAuthorizer = _skills,
                UploadOpener = _inbound is { } inbound
                    ? (part, range, token) => inbound.OpenAsync(call, part, range, token)
                    : null,
                UserArtifactUpdates = userArtifacts,
                LiveInput = new LiveInputBuffer(_options.Limits.LiveInputCapacity)
            };
            return new Prepared(context, accepted.Existing is null ? push : null, accepted.LastSequence,
                accepted.Duplicate, isFollowUp ? accepted.Existing?.Status : null, accepted.StartPending);
        }
        catch
        {
            if (accepted.StartPending) _engine.EndStart(Key(call, taskId));
            throw;
        }
    }

    /// <summary>
    ///     The checks on the request itself, before anything is copied, stored or locked. Returns whether the message
    ///     answers a form.
    /// </summary>
    private async Task<bool> ValidateRequestAsync(SendMessageRequest request, ServerCallContext call,
        CancellationToken ct)
    {
        call.BindTenant(request.Tenant);
        A2AValidator.ValidateIncoming(request); // a request without a message is refused here
        var message = request.Message!;
        RequestLimitValidator.Validate(message, _options.Limits);
        ActorChainValidator.Validate(message, _options.Limits, call);
        await RequireSupportedMediaTypesAsync(message, call, ct).ConfigureAwait(false);
        await SkillSelectionValidator.ValidateAsync(message, call, _requestCards, _cards, _authorization, ct)
            .ConfigureAwait(false);
        return await ElicitationValidator.IsAnswerAsync(message, call, _requestCards, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Check the push config before anything changes, so a refused request leaves no trace and a retry is refused the
    ///     same way.
    /// </summary>
    private async Task<TaskPushNotificationConfig?> PreparePushConfigAsync(SendMessageRequest request,
        ServerCallContext call, string taskId, CancellationToken ct)
    {
        if (request.Configuration?.TaskPushNotificationConfig is not { } p) return null;
        RequestCards.RequirePush(await _requestCards.RequireAgentAsync(call, ct).ConfigureAwait(false));
        var push = p.DeepCopy(); // the caller's config is never changed
        push.ProtocolVersion = PushVersion(push.ProtocolVersion, call, "configuration.taskPushNotificationConfig");
        push.TaskId = taskId;
        push.Id = string.IsNullOrEmpty(push.Id) ? TaskIds.New() : push.Id;
        push.Tenant = call.Tenant;
        await _pushConfigs.ValidateInlineAsync(push, call, ct).ConfigureAwait(false);
        return push;
    }

    /// <summary>
    ///     The audit rows begin here: the request is valid, and nothing is stored or locked yet, so with Audit.FailClosed a
    ///     sink
    ///     that fails refuses the request before it changes anything, and a slow sink never holds a task lock.
    /// </summary>
    private async Task BeginAuditAsync(ServerCallContext call, AuditTicket ticket, Message message, bool isFollowUp,
        bool answersForm, string taskId,
        TaskPushNotificationConfig? push, CancellationToken ct)
    {
        var messageEvent = _audit.For(ClassifyMessage(message, isFollowUp, answersForm), call, taskId);
        await _audit.BeginAsync(messageEvent, ct).ConfigureAwait(false);
        ticket.Message = messageEvent;
        if (push is not null)
        {
            var pushEvent = _audit.For(A2AAuditAction.PushConfigCreated, call, taskId, push.Id);
            await _audit.BeginAsync(pushEvent, ct).ConfigureAwait(false);
            ticket.Push = pushEvent;
        }
    }

    /// <summary>
    ///     Under the task lock: check that the task takes the message and commit it to the history (retried on a conflict), or
    ///     recognise it as already accepted. The lock, the retry loop and the cleanup of an unsaved push config stay together
    ///     here.
    /// </summary>
    private async Task<Accepted> AcceptFollowUpAsync(ServerCallContext call, AuditTicket ticket, Message message,
        string taskId, bool answersForm,
        TaskPushNotificationConfig? push, long? ifGenerationMatch,
        IReadOnlyList<TaskArtifactUpdateEvent>? userArtifacts, CancellationToken ct)
    {
        var key = Key(call, taskId);
        var pushIsNew = false; // the config id did not exist before this request
        var pushSaved = false;
        var committed = false;
        var attached = false; // the upload references are written once, however often the commit is retried
        // Never ask the card provider under the task lock: the locks are shared by many tasks.
        var pushEnabled = await _engine.PushEnabledForTenantAsync(key, ct).ConfigureAwait(false);
        var gate = await _engine.LockAsync(key, ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0;; attempt++)
            {
                var record = await _tasks.GetAsync(key, ct).ConfigureAwait(false) ??
                             throw A2AException.TaskNotFound(taskId);
                var state = record.Task.Status.State;
                if (!string.IsNullOrEmpty(message.ContextId) && message.ContextId != record.Task.ContextId)
                    throw A2AException.InvalidParams("message.contextId does not match the task's contextId");
                if (record.Task.History?.Exists(m =>
                        string.Equals(m.MessageId, message.MessageId, StringComparison.Ordinal)) == true)
                    return await AcceptAlreadyAcceptedAsync(key, ticket, record, message, ct).ConfigureAwait(false);
                if (userArtifacts is not null) RequireClientOwned(record.Task, userArtifacts);
                if (ifGenerationMatch is { } expected)
                {
                    // Checked on the record the commit is based on (CAS), so check and commit are one step: after a conflict
                    // the loop reads again and checks again. A retry of an accepted message was answered above.
                    var current = record.Task.Generation ??
                                  await _events.LastSequenceAsync(key, ct).ConfigureAwait(false);
                    if (current != expected) throw GenerationMismatch(taskId, expected, current);
                }

                if (state.IsTerminal())
                    throw new A2AException(A2AErrorCode.UnsupportedOperation,
                        "The task is in a terminal state. Start a new task and list this one in referenceTaskIds.")
                    {
                        Metadata = new Dictionary<string, string> { ["taskId"] = taskId }
                    };
                // A2A 1.1: an execution of the task that reads its live input takes the message, in any non-terminal state;
                // no new execution starts.
                var listening = _engine.ListeningInput(key);
                if (listening is { HasRoom: false })
                    // RESOURCE_EXHAUSTED (design D8): the agent's input buffer is full; nothing is committed.
                    throw new A2AException(A2AErrorCode.RateLimited, "The agent is not accepting more input right now.")
                    {
                        HttpStatus = 429,
                        RetryAfter = TimeSpan.FromSeconds(1),
                        Metadata = new Dictionary<string, string> { ["taskId"] = taskId }
                    };
                if (listening is null && !TaskStateMachine.AcceptsFollowUp(state, _options.FollowUpPolicy))
                    throw new A2AException(A2AErrorCode.UnsupportedOperation,
                        $"The task does not accept messages in state {state}.")
                    {
                        Metadata = new Dictionary<string, string> { ["taskId"] = taskId }
                    };
                EnsureTurnCapacity(record.Task);
                var approval = ResolveApproval(record.Task, message, out var consume);
                var approvalResource = consume;
                // A refused answer throws here: nothing is stored and the agent does not run. Same for a retry after a conflict.
                string? answeredForm = null;
                var elicitation =
                    answersForm ? ElicitationValidator.Resolve(record.Task, message, out answeredForm) : null;
                var contextId = record.Task.ContextId;
                message.ContextId = contextId;
                // The task is the caller's (read with its key) and the message is not a duplicate; nothing is committed yet. No start
                // is claimed either (BeginStart comes after the commit), so a refusal here needs no EndStart: the finally below only
                // drops the push config.
                if (_inbound is not null && !attached)
                {
                    await _inbound.AttachAsync(call, taskId, message, ct).ConfigureAwait(false);
                    attached = true;
                }

                // The stored record belongs to the store: append to a copy, with a copy of the message the executor gets.
                var next = record.Task.CopyForUpdate();
                var stored = message.DeepCopy();
                (next.History ??= []).Add(stored);
                // Same update as the approving message: the approval cannot be used twice, also across instances (CAS).
                if (consume is not null)
                    (next.Metadata ??= [])[A2AProtocol.ApprovalConsumedKey] = StringElement(consume);
                // Same for an answered form: a used form is no longer pending, also across instances (CAS).
                if (answeredForm is not null)
                    (next.Metadata ??= [])[Elicitation.AnsweredKey] = StringElement(answeredForm);
                // The latest well-formed chain wins; a follow-up without one leaves the echo as it is.
                if (ActorChainValidator.WellFormedChain(message) is { } chain)
                    (next.Metadata ??= [])[ActorChain.MetadataKey] = chain;
                // The last sequence before the commit: no event can land between this read and the commit (an event commit changes
                // the task's version, so the commit below would conflict and this loop would read again). Read before the commit,
                // so a failing read leaves nothing committed; the marker lets a retry tell that the agent never started.
                var sequenceBefore = await _events.LastSequenceAsync(key, ct).ConfigureAwait(false);
                // A2A 1.1: the message is one change of the task. It is committed as an event (a message update) with the
                // next sequence, so the task's generation (which equals its last sequence) moves by one, and the message is
                // the next timeline entry. A task stored before generations existed starts from its last sequence.
                var messageSequence = sequenceBefore + 1;
                var generation = (record.Task.Generation ?? sequenceBefore) + 1;
                next.Generation = generation;
                TaskTimeline.Append(next, TimelineEntry.FromMessage(stored, generation), _options.Limits.MaxTimelineEntries);
                (next.Metadata ??= [])[PendingStartKey] = StringElement(string.Create(CultureInfo.InvariantCulture,
                    $"{messageSequence}:{message.MessageId}"));
                EnsureTaskSizeCapacity(next);
                if (push is not null && !pushSaved)
                {
                    // Saved atomically under the limit before the message is committed, so a refused request leaves no message.
                    pushIsNew = await _pushConfigs.SaveInlineUnderLimitAsync(key, push, ct).ConfigureAwait(false);
                    pushSaved = true;
                }

                try
                {
                    var added = StreamResponse.Of(new TaskMessageUpdateEvent
                    {
                        TaskId = taskId,
                        ContextId = next.ContextId,
                        Message = stored,
                        Generation = generation
                    });
                    // The message update is a 1.1 event: only push configs of version 1.1 get it.
                    var encoded = EventEncoder.Encode(0, added);
                    var deliveries = await _engine.MessageDeliveriesAsync(key, pushEnabled, ct).ConfigureAwait(false);
                    var result = await _tasks
                        .CommitAsync(new TaskCommit(record with { Task = next }, record.Version, encoded, deliveries),
                            ct).ConfigureAwait(false);
                    if (deliveries.Count > 0) _engine.NotifyPushPending();
                    // Published under the task lock, like every stored event, so subscribers see it in sequence order. Only
                    // 1.1 streams write it (EventEncoder.IsVisibleTo).
                    _engine.PublishStored(key, encoded with { Sequence = result.Sequence });
                    committed = true;
                    // Until the execution is registered, a retry must not start the message a second time.
                    if (listening is null) _engine.BeginStart(key);
                    // A2A 1.1 (§3.2.1): then the client's artifacts, in list order, each one change. Still under the lock.
                    var (latest, last) = (result.Record, result.Sequence);
                    if (userArtifacts is not null)
                        foreach (var update in userArtifacts)
                        {
                            update.ContextId = contextId;
                            if (await _engine.StoreClientArtifactAsync(key, latest, update, pushEnabled, ct).ConfigureAwait(false) is
                                { } applied)
                                (latest, last) = applied;
                        }

                    if (listening is not null)
                    {
                        // Handed to the running agent: nothing starts, as for a message already accepted.
                        if (!listening.TryWrite(new LiveInputItem(message,
                                userArtifacts?.Select(u => u.Artifact).ToList() ?? [])))
                            ServerLog.LiveInputDropped(_engine.Logger, taskId);
                        return new Accepted(message, contextId, latest.Task, last, true, false, approval, elicitation,
                            approvalResource);
                    }

                    // The caller's own stream starts after its message (and artifacts): the task it gets already holds them.
                    return new Accepted(message, contextId, latest.Task, last, false, true, approval, elicitation,
                        approvalResource);
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
            if (pushSaved && pushIsNew && !committed)
                await _pushConfigs.TryDeleteAsync(key, push!.Id!).ConfigureAwait(false);
            gate.Dispose();
        }
    }

    /// <summary>
    ///     The message is already in the history (an earlier request of it failed after the commit): no push config, no
    ///     append, no
    ///     new execution. Checked before the state, so a retry after the task settled gets its outcome. Either a recovery (the
    ///     agent
    ///     never started: start it now, once) or a plain duplicate.
    /// </summary>
    private async Task<Accepted> AcceptAlreadyAcceptedAsync(TaskKey key, AuditTicket ticket, TaskRecord record,
        Message message, CancellationToken ct)
    {
        var existing = record.Task;
        var contextId = record.Task.ContextId;
        message.ContextId = contextId;
        // The earlier request committed the message but evidently never started the agent (it crashed, or its
        // start failed and could not even fail the task): start it now, once.
        if (await PendingStartAsync(key, record.Task, message, ct).ConfigureAwait(false) is { } pendingSequence
            && await _engine.TryClaimStartAsync(key, ct).ConfigureAwait(false))
        {
            // The agent gets the message that was committed, never the retry's content (same id, maybe other text).
            message = record.Task.History![^1].DeepCopy();
            message.ContextId = contextId;
            var approval = ResolveApproval(record.Task, message, out var approvalResource, true);
            var elicitation = message.GetElicitationAnswer();
            ticket.Reason = ticket.PushReason = "recovered";
            return new Accepted(message, contextId, existing, pendingSequence, false, true, approval, elicitation,
                approvalResource);
        }

        ticket.Reason = ticket.PushReason = "duplicate";
        return new Accepted(message, contextId, existing, 0, true, false, ApprovalState.None, null, null);
    }

    /// <summary>What the message turned out to be. A duplicate keeps its first classification: nothing was done for it.</summary>
    private static void ClassifyAccepted(AuditTicket ticket, Accepted accepted)
    {
        if (accepted.Duplicate) return;
        if (accepted.Elicitation is not null)
        {
            ticket.Action = A2AAuditAction.ElicitationAnswered;
            ticket.Reason = accepted.Elicitation.Action.ToString();
        }
        else if (accepted.Approval == ApprovalState.Approved)
        {
            ticket.Action = A2AAuditAction.ApprovalGranted;
            ticket.Resource = accepted.ApprovalResource;
        }
        else
        {
            ticket.Action = accepted.Approval == ApprovalState.NotApproved
                ? A2AAuditAction.ApprovalDeclined
                : A2AAuditAction.FollowUpSent;
        }
    }

    /// <summary>
    ///     Whether the agent evidently never started for <paramref name="message" />, which is already in the history of
    ///     <paramref name="task" />: the sequence the follow-up commit recorded (<see cref="PendingStartKey" />), or null when
    ///     the retry
    ///     is a plain duplicate. All of these must hold: the task is not final and still accepts a follow-up (it is as the
    ///     commit left
    ///     it), the message is the last in the history, the marker is this message's, and the event store has no event after
    ///     the
    ///     recorded sequence (an agent that ran and wrote anything, an artifact included, leaves one). The caller then checks
    ///     that no
    ///     execution runs and claims the start, under the task lock.
    /// </summary>
    private async ValueTask<long?> PendingStartAsync(TaskKey key, AgentTask task, Message message, CancellationToken ct)
    {
        var state = task.Status.State;
        if (state.IsTerminal() || !TaskStateMachine.AcceptsFollowUp(state, _options.FollowUpPolicy)) return null;
        if (task.History is not { Count: > 0 } history ||
            !string.Equals(history[^1].MessageId, message.MessageId, StringComparison.Ordinal)) return null;
        if (task.Metadata is null || !task.Metadata.TryGetValue(PendingStartKey, out var marker) ||
            marker.ValueKind != JsonValueKind.String) return null;
        var text = marker.GetString()!;
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !long.TryParse(text.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture,
                out var sequence)) return null;
        if (!string.Equals(text[(colon + 1)..], message.MessageId, StringComparison.Ordinal)) return null;
        // A read that fails fails this retry: the client retries again (nothing is started on a guess).
        return await _events.LastSequenceAsync(key, ct).ConfigureAwait(false) == sequence ? sequence : null;
    }

    /// <summary>
    ///     Approval binding (spec design §6.6). The pending approval is the hash in the metadata of the status message of a
    ///     task
    ///     waiting in an interrupted state, so it is stored with the task and every instance sees it. It is used at most once:
    ///     the commit of the approving message also records the status message's id in the task metadata
    ///     (<see cref="A2AProtocol.ApprovalConsumedKey" />), and a used approval is no longer pending. A follow-up without a
    ///     hash is
    ///     accepted (<see cref="ApprovalState.NotApproved" /> while an approval is pending); a hash that does not match the
    ///     pending
    ///     one, or when none is pending or it was used, is InvalidParams. A pending approval whose status message has no id
    ///     fails
    ///     closed (treated as used). <paramref name="consume" /> is the status message id to record when the result is
    ///     <see cref="ApprovalState.Approved" />.
    /// </summary>
    private static ApprovalState ResolveApproval(AgentTask task, Message message, out string? consume,
        bool recovering = false)
    {
        consume = null;
        string? pending = null;
        var status = task.Status.Message;
        if (task.Status.State.IsInterrupted() && status?.Metadata is { } statusMetadata
                                              && statusMetadata.TryGetValue(A2AProtocol.ApprovalHashKey, out var p) &&
                                              p.ValueKind == JsonValueKind.String)
            pending = p.GetString();
        // A status message without an id could never be marked used: fail closed, as if it were.
        // Recovering a committed message: the task is unchanged since, so the approval it consumed is this message's own.
        var used = pending is not null
                   && (string.IsNullOrEmpty(status!.MessageId)
                       || (!recovering && task.Metadata is { } taskMetadata
                                       && taskMetadata.TryGetValue(A2AProtocol.ApprovalConsumedKey, out var c) &&
                                       c.ValueKind == JsonValueKind.String
                                       && string.Equals(c.GetString(), status.MessageId, StringComparison.Ordinal)));

        if (message.Metadata is null || !message.Metadata.TryGetValue(A2AProtocol.ApprovalHashKey, out var value))
            return pending is null ? ApprovalState.None : ApprovalState.NotApproved;
        if (pending is null || used || value.ValueKind != JsonValueKind.String
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pending),
                Encoding.UTF8.GetBytes(value.GetString()!)))
            throw A2AException.InvalidParams("Approval does not match the pending request.");
        consume = status!.MessageId;
        return ApprovalState.Approved;
    }

    private static JsonElement StringElement(string value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(value);
        }

        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    /// <summary>
    ///     Returns the context id for a new task and makes sure the context belongs to the caller. A context owned by
    ///     someone else looks like a missing one.
    /// </summary>
    /// <summary>
    ///     Throws UnsupportedOperation when one more message would exceed <see cref="A2AServerOptions.MaxTurnsPerTask" />.
    ///     Turns are the user messages in the stored history (the first message and every accepted follow-up).
    /// </summary>
    private void EnsureTurnCapacity(AgentTask task)
    {
        if (_options.MaxTurnsPerTask is not { } max) return;
        var turns = 0;
        if (task.History is { } history)
            foreach (var m in history)
                if (m.Role == Role.User)
                    turns++;
        if (turns < max) return;
        throw new A2AException(A2AErrorCode.UnsupportedOperation,
            $"The task reached its limit of {max} messages. Start a new task and list this one in referenceTaskIds.")
        {
            Metadata = new Dictionary<string, string>
                { ["taskId"] = task.Id, ["maxTurnsPerTask"] = max.ToString(CultureInfo.InvariantCulture) }
        };
    }

    /// <summary>
    ///     Throws UnsupportedOperation when the task, with the follow-up message already added, is larger than
    ///     <see cref="A2ALimits.MaxTaskBytes" />. Nothing is stored then.
    /// </summary>
    private void EnsureTaskSizeCapacity(AgentTask next)
    {
        var max = _options.Limits.MaxTaskBytes;
        if (TaskSize.Measure(next) <= max) return;
        throw new A2AException(A2AErrorCode.UnsupportedOperation,
            $"The task reached its size limit of {max} bytes. Start a new task and list this one in referenceTaskIds.")
        {
            Metadata = new Dictionary<string, string>
                { ["taskId"] = next.Id, ["maxTaskBytes"] = max.ToString(CultureInfo.InvariantCulture) }
        };
    }

    private async Task<string> ResolveContextAsync(string? requested, ServerCallContext call, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(requested))
        {
            var created = await _tasks.GetOrCreateContextAsync(
                    new ContextRecord(call.Tenant, call.Principal, TaskIds.New(), DateTimeOffset.UtcNow), ct)
                .ConfigureAwait(false);
            if (!string.Equals(created.Owner, call.Principal, StringComparison.Ordinal))
                throw ContextDenied(call, created.ContextId); // an id collision
            return created.ContextId;
        }

        var existing = await _tasks.GetContextAsync(call.Tenant, requested, ct).ConfigureAwait(false);
        if (existing is null)
        {
            if (_options.ClientContextIdPolicy != ClientContextIdPolicy.AdoptIfUnknown)
                throw A2AException.InvalidParams(NotFound);
            existing = await _tasks.GetOrCreateContextAsync(
                    new ContextRecord(call.Tenant, call.Principal, requested, DateTimeOffset.UtcNow), ct)
                .ConfigureAwait(false);
        }

        if (!string.Equals(existing.Owner, call.Principal, StringComparison.Ordinal)
            && !(_options.ContextAccess is { } access && await access(existing, call, ct).ConfigureAwait(false)))
            throw ContextDenied(call, existing.ContextId);
        return existing.ContextId;
    }

    /// <summary>
    ///     The refusal of a context that is not the caller's: InvalidParams "contextId not found", so it never confirms
    ///     the context exists. Counts a security event.
    /// </summary>
    private A2AException ContextDenied(ServerCallContext call, string contextId)
    {
        SecurityEventLog.Record(_options, A2ATelemetry.SecurityEventNames.ContextDenied,
            A2AProtocol.Methods.SendMessage, call, contextId);
        // Detached: this builds the exception synchronously. The message's own row ends as Failed with InvalidParams.
        _audit.RecordDetached(_audit.For(A2AAuditAction.AccessDenied, call, resource: contextId)
            with
            {
                Outcome = A2AAuditOutcome.Denied, Operation = A2AOperation.SendMessage.ToString(),
                Reason = "PermissionDenied"
            });
        return A2AException.InvalidParams(NotFound);
    }

    private async ValueTask<AgentTask> GetTaskCoreAsync(GetTaskRequest request, ServerCallContext call,
        CancellationToken cancellationToken)
    {
        await _requestCards.RequireAgentForTaskAsync(call, request.Id, cancellationToken).ConfigureAwait(false);
        A2AValidator.RequireId(request.Id, "id");
        if (request.HistoryLength is < 0) throw A2AException.InvalidParams("historyLength must be >= 0");
        if (request.TimelineLength is < 0) throw A2AException.InvalidParams("timelineLength must be >= 0");
        // A2A 1.1 long-poll (§3.2.7): wait until the generation is above currentGeneration. A 1.0 request has no such field.
        var record = request.CurrentGeneration is { } after && A2AProtocol.IsVersion11OrLater(call.ProtocolVersion)
            ? await _streamer.WaitPastGenerationAsync(Key(call, request.Id), call, after, cancellationToken)
                .ConfigureAwait(false)
            : await _tasks.GetAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false) ??
              throw A2AException.TaskNotFound(request.Id);
        return Shape(record.Task, request.HistoryLength, timelineLength: request.TimelineLength);
    }

    /// <summary>
    ///     Opens a stored artifact object for download. It runs through the pipeline as <see cref="A2AOperation.GetTask" />:
    ///     whoever may
    ///     not read the task may not read its bytes. The task is loaded with the caller's key, and the object key is built
    ///     from the
    ///     call's tenant, <paramref name="taskId" /> and <paramref name="objectId" />, so an id from another task finds
    ///     nothing. Every
    ///     miss (no store, bad object id, no task, no object) is <c>TaskNotFound</c>. The caller disposes the result.
    /// </summary>
    /// <param name="taskId">The task the artifact belongs to.</param>
    /// <param name="objectId">The object id from the artifact link.</param>
    /// <param name="range">
    ///     The bytes to read, or null for all. A range always streams through the server, also in
    ///     <see cref="ArtifactDelivery.Redirect" /> mode.
    /// </param>
    /// <param name="call">The caller, with its tenant bound.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="A2AException">
    ///     The call is refused, or the object cannot be found (
    ///     <see cref="A2AErrorCode.TaskNotFound" />).
    /// </exception>
    public async ValueTask<ArtifactDownload> OpenArtifactAsync(string taskId, string objectId, ArtifactByteRange? range,
        ServerCallContext call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(objectId);
        ArgumentNullException.ThrowIfNull(call);
        var request = new GetTaskRequest { Id = taskId, Tenant = call.Tenant };
        return await _pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.GetTask), call, request,
            (_, c, ct) => _audit.AuditedAsync(
                _audit.For(A2AAuditAction.ArtifactDownloaded, c, taskId,
                    ArtifactObjectKey.IsValidObjectId(objectId) ? objectId : null),
                () => OpenArtifactCoreAsync(taskId, objectId, range, c, ct), null, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ArtifactDownload> OpenArtifactCoreAsync(string taskId, string objectId,
        ArtifactByteRange? range, ServerCallContext call,
        CancellationToken cancellationToken)
    {
        await _requestCards.RequireAgentForTaskAsync(call, taskId, cancellationToken).ConfigureAwait(false);
        // "_" is the link spelling of the default tenant, so it is no tenant of its own and has no keys.
        if (_objects is null || call.Tenant == "_" || !ArtifactObjectKey.IsValidObjectId(objectId))
            throw A2AException.TaskNotFound(taskId);
        _ = await _tasks.GetAsync(Key(call, taskId), cancellationToken).ConfigureAwait(false) ??
            throw A2AException.TaskNotFound(taskId);
        var key = ArtifactObjectKey.Create(call.Tenant, taskId, objectId);
        if (range is null && _options.ArtifactStorage.Delivery == ArtifactDelivery.Redirect)
        {
            // A presigned URL does not prove the object exists, so a miss must be found here to answer 404 instead of a redirect.
            if (!await _objects.ExistsAsync(key, cancellationToken).ConfigureAwait(false))
                throw A2AException.TaskNotFound(taskId);
            if (await _objects
                    .TryCreateDownloadUrlAsync(key, _options.ArtifactStorage.DownloadUrlLifetime, cancellationToken)
                    .ConfigureAwait(false) is { } url)
                return new ArtifactDownload(url, null);
        }

        var content = await _objects.OpenReadAsync(key, range, cancellationToken).ConfigureAwait(false) ??
                      throw A2AException.TaskNotFound(taskId);
        return new ArtifactDownload(null, content);
    }

    /// <summary>
    ///     Stores an uploaded file for the caller and returns the <see cref="UrlPart" /> that stands for it: <c>url</c> (the
    ///     link the
    ///     caller puts in a message), <c>mediaType</c>, <c>filename</c>, and <c>metadata</c> with <c>sha256</c> and
    ///     <c>sizeBytes</c>. It
    ///     runs through the pipeline as the extension operation <see cref="UploadOperationName" />, so a custom
    ///     <see cref="A2AAuthorizationHandler" /> must allow that name. The caller must be authenticated, also when
    ///     <see cref="A2AServerOptions.AllowAnonymous" /> is set. The file belongs to the caller's tenant and principal. The
    ///     content is
    ///     streamed and never buffered. See <see cref="ArtifactUploadOptions" /> for the limits.
    /// </summary>
    /// <param name="content">The file bytes. Not disposed.</param>
    /// <param name="mediaType">The media type, or null for <c>application/octet-stream</c>.</param>
    /// <param name="fileName">
    ///     The file name, or null. Any path is removed; a name over 255 characters or with a control
    ///     character is refused.
    /// </param>
    /// <param name="contentLength">The announced length, or null. A body of another length is refused and not kept.</param>
    /// <param name="call">The caller, with its tenant bound.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <exception cref="InvalidOperationException">Inbound uploads are not enabled, or no object store is registered.</exception>
    /// <exception cref="A2AException">
    ///     Unauthenticated; PermissionDenied; InvalidParams (file name, tenant, length mismatch); a payload over
    ///     <see cref="ArtifactUploadOptions.MaxUploadBytes" /> (<see cref="A2AException.PayloadTooLarge" />, HTTP 413);
    ///     RateLimited (the caller's quota
    ///     or concurrency limit, HTTP 429, with <see cref="A2AException.RetryAfter" />).
    /// </exception>
    public async ValueTask<UrlPart> UploadFileAsync(Stream content, string? mediaType, string? fileName,
        long? contentLength, ServerCallContext call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(call);
        var inbound = _inbound ?? throw new InvalidOperationException(
            "Inbound file uploads are not enabled: set ArtifactStorage.Uploads.Enabled and register an ArtifactObjectStore.");
        UrlPart? part = null;
        await _pipeline.RunExtensionAsync(UploadOperation, call, A2AJson.EmptyObject, async (_, _, ct) =>
        {
            await _requestCards.RequireAgentAsync(call, ct).ConfigureAwait(false);
            part = await inbound.UploadAsync(call, content, mediaType, fileName, contentLength, ct)
                .ConfigureAwait(false);
            return A2AJson.EmptyObject;
        }, cancellationToken).ConfigureAwait(false);
        return part!;
    }

    /// <summary>
    ///     Opens the caller's own uploaded file for download. It runs through the pipeline as the extension operation
    ///     <see cref="ReadUploadOperationName" />. The key is built from the call's tenant and principal and
    ///     <paramref name="objectId" />, so
    ///     another owner's file is not found. Every miss (not enabled, bad id, no file, another owner or tenant) is
    ///     <c>TaskNotFound</c>.
    ///     The caller must be authenticated. The caller disposes the result, which never has a redirect.
    /// </summary>
    /// <param name="objectId">The object id from the upload link.</param>
    /// <param name="range">The bytes to read, or null for all.</param>
    /// <param name="call">The caller, with its tenant bound.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="A2AException">
    ///     Unauthenticated; PermissionDenied; or the file cannot be found (
    ///     <see cref="A2AErrorCode.TaskNotFound" />).
    /// </exception>
    public async ValueTask<ArtifactDownload> OpenUploadAsync(string objectId, ArtifactByteRange? range,
        ServerCallContext call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        ArgumentNullException.ThrowIfNull(call);
        ArtifactObjectContent? content = null;
        await _pipeline.RunExtensionAsync(ReadUploadOperation, call, A2AJson.EmptyObject, async (_, _, ct) =>
        {
            await _requestCards.RequireAgentAsync(call, ct).ConfigureAwait(false);
            if (_inbound is not null)
                content = await _inbound.OpenAsync(call, objectId, range, ct).ConfigureAwait(false);
            return A2AJson.EmptyObject;
        }, cancellationToken).ConfigureAwait(false);
        return new ArtifactDownload(null, content ?? throw A2AException.TaskNotFound(objectId));
    }

    private async ValueTask<ListTasksResponse> ListTasksCoreAsync(ListTasksRequest request, ServerCallContext call,
        CancellationToken cancellationToken)
    {
        await _requestCards.RequireAgentAsync(call, cancellationToken).ConfigureAwait(false);
        if (request.PageSize is < 1) throw A2AException.InvalidParams("pageSize must be between 1 and 100");
        if (request.HistoryLength is < 0) throw A2AException.InvalidParams("historyLength must be >= 0");
        if (request.TimelineLength is < 0) throw A2AException.InvalidParams("timelineLength must be >= 0");
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
            Tasks = result.Records.Select(r => Shape(r.Task, request.HistoryLength, include, request.TimelineLength))
                .ToList(),
            NextPageToken = result.NextPageToken,
            PageSize = pageSize,
            TotalSize = result.TotalSize
        };
    }

    private ValueTask<AgentTask> CancelTaskAuditedAsync(CancelTaskRequest request, ServerCallContext call,
        CancellationToken cancellationToken)
    {
        return _audit.AuditedAsync(_audit.For(A2AAuditAction.TaskCanceled, call, request.Id),
            () => CancelTaskCoreAsync(request, call, cancellationToken),
            static task => (null,
                task.Metadata is not null && task.Metadata.ContainsKey(A2AProtocol.CancelRequestedKey)
                    ? "CancelRequested"
                    : null),
            cancellationToken);
    }

    private async ValueTask<AgentTask> CancelTaskCoreAsync(CancelTaskRequest request, ServerCallContext call,
        CancellationToken cancellationToken)
    {
        var card = await _requestCards.RequireAgentForTaskAsync(call, request.Id, cancellationToken)
            .ConfigureAwait(false);
        // Every authorized CancelTask in a tenant an agent serves counts, whether or not the task exists or can still be
        // canceled. Counted only after the tenant resolved: a made-up tenant per call must not take a tracked entry each,
        // or it fills the detector's table and no other caller is watched.
        _anomalies?.CancelRequested(call, CancelOperationName);
        A2AValidator.RequireId(request.Id, "id");
        var key = Key(call, request.Id);
        var record = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ??
                     throw A2AException.TaskNotFound(request.Id);
        if (_options.IdempotentCancel && record.Task.Status.State == TaskState.Canceled)
            return Shape(record.Task, null);
        if (record.Task.Status.State.IsTerminal()) throw NotCancelable(request.Id);

        // Executions here stop at once; the other instances stop theirs when the request reaches them.
        _engine.CancelRunning(key);
        await _engine.RequestCancelElsewhereAsync(key, cancellationToken).ConfigureAwait(false);

        // Without an agent for the tenant, the default cancel runs (the task is marked canceled), so a task whose agent is
        // gone can still be cancelled.
        await using var agent = await _engine.RentAgentAsync(call.Tenant, CancelOnlyAgent.Instance, cancellationToken)
            .ConfigureAwait(false);
        var exec = await _engine
            .StartCancelAsync(key, record.Task, request.Metadata, agent, card.Capabilities.PushNotifications == true)
            .ConfigureAwait(false);
        exec.Detach();

        await _engine.WaitUntilStoppedAsync(key, _options.CancelGracePeriod, cancellationToken).ConfigureAwait(false);

        record = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ??
                 throw A2AException.TaskNotFound(request.Id);
        var state = record.Task.Status.State;
        if (state == TaskState.Canceled) return Shape(record.Task, null);
        if (!state.IsTerminal() && await _engine.IsRunningAnywhereAsync(key, cancellationToken).ConfigureAwait(false))
        {
            // The execution has not stopped within the grace period: the cancel is requested, not done. Only the response says
            // so (the stored task is not changed); its later CANCELED arrives through the normal stream.
            var pending = Shape(record.Task, null);
            pending.Metadata = pending.Metadata is null ? [] : new Dictionary<string, JsonElement>(pending.Metadata);
            pending.Metadata[A2AProtocol.CancelRequestedKey] = TrueElement;
            return pending;
        }

        throw NotCancelable(request.Id); // the agent refused and nothing runs, or the task ended otherwise
    }

    /// <summary>
    ///     A2A 1.1 (§3.1.12): <c>lastSeenGeneration</c> resumes after that generation, without a task snapshot first. The
    ///     task's
    ///     generation equals its event sequence, so this is the resume after an event id; when the store no longer has the
    ///     events
    ///     after it, the stream starts with a snapshot instead. Ignored for a 1.0 caller, whose request has no such field.
    /// </summary>
    private static long? LastSeenGeneration(SubscribeToTaskRequest request, ServerCallContext call)
    {
        return request.LastSeenGeneration is { } seen && A2AProtocol.IsVersion11OrLater(call.ProtocolVersion)
            ? Math.Max(seen, 0)
            : null;
    }

    private ValueTask<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAuditedAsync(
        TaskPushNotificationConfig config, ServerCallContext call, CancellationToken cancellationToken)
    {
        return _audit.AuditedAsync(_audit.For(A2AAuditAction.PushConfigCreated, call, config.TaskId),
            () => CreateTaskPushNotificationConfigCoreAsync(config, call, cancellationToken),
            static created => (created.Id, null), cancellationToken);
    }

    private async ValueTask<TaskPushNotificationConfig> CreateTaskPushNotificationConfigCoreAsync(
        TaskPushNotificationConfig config, ServerCallContext call, CancellationToken cancellationToken)
    {
        RequestCards.RequirePush(await _requestCards.RequireAgentForTaskAsync(call, config.TaskId, cancellationToken)
            .ConfigureAwait(false));
        A2AValidator.ValidatePushConfig(config, "config", true);
        var key = Key(call, config.TaskId!);
        _ = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ??
            throw A2AException.TaskNotFound(config.TaskId!);
        // Save first, then read the task (inside CatchUpAsync): an event committed after that read lists this config itself or
        // finds it when it lists again (see TaskExecutionEngine), so no final state is missed.
        var versioned = config.DeepCopy(); // the caller's config is never changed
        versioned.ProtocolVersion = PushVersion(config.ProtocolVersion, call, "config");
        var created = await _pushConfigs.CreateAsync(key, versioned, cancellationToken).ConfigureAwait(false);
        // The task may have been purged between the check above and the save: the purge deleted its configs before this one
        // existed. Look again, so a config never outlives its task (the purge also deletes configs again after the task).
        if (await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) is null)
        {
            await _pushConfigs.TryDeleteAsync(key, created.Id!).ConfigureAwait(false);
            throw A2AException.TaskNotFound(config.TaskId!);
        }

        await _engine.CatchUpAsync(key, created.Id!, cancellationToken).ConfigureAwait(false);
        return created;
    }

    private async ValueTask<TaskPushNotificationConfig> GetTaskPushNotificationConfigCoreAsync(
        GetTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        RequestCards.RequirePush(await _requestCards.RequireAgentForTaskAsync(call, request.TaskId, cancellationToken)
            .ConfigureAwait(false));
        A2AValidator.RequireId(request.TaskId, "taskId");
        A2AValidator.RequireId(request.Id, "id");
        var key = Key(call, request.TaskId);
        _ = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ??
            throw A2AException.TaskNotFound(request.TaskId);
        return await _pushConfigs.GetAsync(key, request.Id, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsCoreAsync(
        ListTaskPushNotificationConfigsRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        RequestCards.RequirePush(await _requestCards.RequireAgentForTaskAsync(call, request.TaskId, cancellationToken)
            .ConfigureAwait(false));
        A2AValidator.RequireId(request.TaskId, "taskId");
        var key = Key(call, request.TaskId);
        _ = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ??
            throw A2AException.TaskNotFound(request.TaskId);
        return await _pushConfigs.ListAsync(key, request.PageSize ?? 0, request.PageToken, cancellationToken)
            .ConfigureAwait(false);
    }

    private ValueTask<object?> DeleteTaskPushNotificationConfigAuditedAsync(
        DeleteTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        return _audit.AuditedAsync(_audit.For(A2AAuditAction.PushConfigDeleted, call, request.TaskId, request.Id),
            () => DeleteTaskPushNotificationConfigCoreAsync(request, call, cancellationToken),
            null, cancellationToken);
    }

    private async ValueTask<object?> DeleteTaskPushNotificationConfigCoreAsync(
        DeleteTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        RequestCards.RequirePush(await _requestCards.RequireAgentForTaskAsync(call, request.TaskId, cancellationToken)
            .ConfigureAwait(false));
        A2AValidator.RequireId(request.TaskId, "taskId");
        A2AValidator.RequireId(request.Id, "id");
        var key = Key(call, request.TaskId);
        _ = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ??
            throw A2AException.TaskNotFound(request.TaskId);
        await _pushConfigs.DeleteAsync(key, request.Id, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async ValueTask<AgentCard> GetExtendedAgentCardCoreAsync(GetExtendedAgentCardRequest request,
        ServerCallContext call, CancellationToken cancellationToken)
    {
        var publicCard = await _requestCards.RequireAgentAsync(call, cancellationToken).ConfigureAwait(false);
        if (publicCard.Capabilities.ExtendedAgentCard != true)
            throw new A2AException(A2AErrorCode.UnsupportedOperation,
                "This agent does not provide an extended agent card.");
        if (!call.IsAuthenticated)
            throw new A2AException(A2AErrorCode.Unauthenticated, "The extended agent card requires authentication.");
        var card = await _cards.GetExtendedAsync(call, publicCard, cancellationToken).ConfigureAwait(false);
        return card is null
            ? throw new A2AException(A2AErrorCode.ExtendedAgentCardNotConfigured)
            : await _skills.FilterExtendedCardAsync(call, card, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Wraps the handler of <paramref name="extension" />. Inside the pipeline, so after authorization, it resolves the
    ///     tenant's card
    ///     (a tenant no agent serves is UnsupportedOperation; with per-tenant cards this also checks required extensions) and
    ///     then
    ///     requires the call to have activated the operation's extension.
    /// </summary>
    private Func<A2AOperationContext, JsonElement, CancellationToken, ValueTask<JsonElement>> RequireActivation(
        A2AExtensionOperation extension)
    {
        return async (context, parameters, ct) =>
        {
            await _requestCards.RequireAgentAsync(context.Call, ct).ConfigureAwait(false);
            if (!context.Call.ActivatedExtensions.Contains(extension.ExtensionUri))
                throw new A2AException(A2AErrorCode.ExtensionSupportRequired,
                    $"Extension '{extension.ExtensionUri}' is required.")
                {
                    Metadata = new Dictionary<string, string> { ["extension"] = extension.ExtensionUri }
                };

            return await extension.Handler(context, parameters, ct).ConfigureAwait(false);
        };
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static TaskKey Key(ServerCallContext call, string taskId)
    {
        return new TaskKey(call.Tenant, call.Principal, taskId);
    }

    private AgentTask Shape(AgentTask task, int? historyLength, bool includeArtifacts = true,
        int? timelineLength = null)
    {
        return TaskShaper.Shape(task, historyLength, _options.Limits, includeArtifacts, timelineLength);
    }

    /// <summary>
    ///     The protocol version a new push config is stored with (A2A 1.1 §4.3.1): what a 1.1 caller asked for (<c>1.0</c> or
    ///     <c>1.1</c>), else the version of the request that creates it. A 1.0 caller has no such field: null, which means
    ///     1.0.
    /// </summary>
    private string? PushVersion(string? requested, ServerCallContext call, string field)
    {
        // A config made by a v0.3 caller (the compat surface translated the call) gets v0.3 bodies, when the compat package
        // is there to write them.
        if (call.WireProtocolVersion == A2AProtocol.ImplicitVersion &&
            _options.PushPayloadEncoder?.ProtocolVersion == A2AProtocol.ImplicitVersion)
            return A2AProtocol.ImplicitVersion;
        if (!A2AProtocol.IsVersion11OrLater(call.ProtocolVersion)) return null;
        if (string.IsNullOrWhiteSpace(requested)) return A2AProtocol.Version11;
        var parts = requested.Trim().Split('.');
        var majorMinor = parts.Length >= 2 ? parts[0] + "." + parts[1] : requested.Trim();
        return majorMinor is A2AProtocol.Version or A2AProtocol.Version11
            ? majorMinor
            : throw A2AException.InvalidParams(
                $"{field}.protocolVersion must be {A2AProtocol.Version} or {A2AProtocol.Version11}");
    }

    /// <summary>
    ///     A2A 1.1 (§3.2.1): checks the client's artifact updates of a SendMessage request for <paramref name="taskId" /> and
    ///     returns
    ///     server-owned copies (role user, no generation, the task's ids), or null when there are none. A 1.0 caller has no
    ///     such
    ///     field: its updates are ignored. Refused without the card's <c>userArtifacts</c> capability (UnsupportedOperation),
    ///     over
    ///     <see cref="A2ALimits.MaxUserArtifactsPerRequest" />, or when an update is malformed or names another task
    ///     (InvalidParams).
    ///     Each artifact's parts are held to the message limits and the card's input media types.
    /// </summary>
    private async Task<IReadOnlyList<TaskArtifactUpdateEvent>?> CheckArtifactUpdatesAsync(SendMessageRequest request,
        ServerCallContext call, string taskId,
        CancellationToken ct)
    {
        if (request.ArtifactUpdates is not { Count: > 0 } updates ||
            !A2AProtocol.IsVersion11OrLater(call.ProtocolVersion)) return null;
        var card = await _requestCards.RequireAgentAsync(call, ct).ConfigureAwait(false);
        if (card.Capabilities.UserArtifacts != true)
            throw new A2AException(A2AErrorCode.UnsupportedOperation,
                "The agent does not accept artifacts written by the client (capabilities.userArtifacts).");
        if (updates.Count > _options.Limits.MaxUserArtifactsPerRequest)
            throw A2AException.InvalidParams(
                $"artifactUpdates has more than {_options.Limits.MaxUserArtifactsPerRequest} entries");
        var checkedUpdates = new List<TaskArtifactUpdateEvent>(updates.Count);
        for (var i = 0; i < updates.Count; i++)
        {
            var path = $"artifactUpdates[{i}]";
            var u = updates[i] ?? throw A2AException.InvalidParams($"{path} is null");
            if (!string.IsNullOrEmpty(u.TaskId) && !string.Equals(u.TaskId, taskId, StringComparison.Ordinal))
                throw A2AException.InvalidParams($"{path}.taskId must be the task of the request");
            var artifact = u.Artifact ?? throw A2AException.InvalidParams($"{path}.artifact is required");
            if (string.IsNullOrWhiteSpace(artifact.ArtifactId))
                throw A2AException.InvalidParams($"{path}.artifact.artifactId is required");
            if (artifact.Parts is not { Count: > 0 } && !u.Append)
                throw A2AException.InvalidParams($"{path}.artifact.parts must contain at least one part");
            if (artifact.Parts is { Count: > 0 })
            {
                // The same limits and media types as a message's parts.
                var asMessage = new Message
                    { MessageId = artifact.ArtifactId, Role = Role.User, Parts = artifact.Parts };
                A2AValidator.ValidateMessage(asMessage, $"{path}.artifact");
                RequestLimitValidator.Validate(asMessage, _options.Limits);
                await RequireSupportedMediaTypesAsync(asMessage, call, ct).ConfigureAwait(false);
            }

            var copy = new Artifact
            {
                ArtifactId = artifact.ArtifactId,
                Name = artifact.Name,
                Description = artifact.Description,
                Parts = (artifact.Parts ?? []).ConvertAll(p => p),
                Metadata = artifact.Metadata is null ? null : new Dictionary<string, JsonElement>(artifact.Metadata),
                Extensions = artifact.Extensions is null ? null : [.. artifact.Extensions],
                Role = Role.User // the server says who wrote it
            };
            // The generation is the server's (§3.2.1): a value the client sent is ignored.
            checkedUpdates.Add(new TaskArtifactUpdateEvent
            {
                TaskId = taskId, ContextId = u.ContextId, Artifact = copy, Append = u.Append, LastChunk = u.LastChunk,
                Metadata = u.Metadata
            });
        }

        return checkedUpdates;
    }

    /// <summary>Refuses client updates of an artifact the agent wrote (one artifact id belongs to one side).</summary>
    private static void RequireClientOwned(AgentTask task, IReadOnlyList<TaskArtifactUpdateEvent> updates)
    {
        foreach (var u in updates)
            if (task.Artifacts?.Find(a => a.ArtifactId == u.Artifact.ArtifactId) is { } existing &&
                existing.Role != Role.User)
                throw A2AException.InvalidParams(
                    $"Artifact '{u.Artifact.ArtifactId}' was written by the agent; the client cannot change it.");
    }

    /// <summary>
    ///     A2A 1.1: a SendMessage without a message stores the client's artifact updates on an existing task (each update
    ///     names it),
    ///     under the task lock, and returns the stored task. No agent runs. Refused for a 1.0 caller (message is required),
    ///     for a
    ///     terminal task, and as <see cref="CheckArtifactUpdatesAsync" /> refuses.
    /// </summary>
    private async Task<TaskRecord> StoreArtifactsOnlyAsync(SendMessageRequest request, ServerCallContext call,
        CancellationToken ct)
    {
        call.BindTenant(request.Tenant);
        if (!A2AProtocol.IsVersion11OrLater(call.ProtocolVersion) || request.ArtifactUpdates is not { Count: > 0 } raw)
            throw A2AException.InvalidParams("message is required");
        var taskId = raw[0]?.TaskId;
        if (string.IsNullOrEmpty(taskId))
            throw A2AException.InvalidParams("artifactUpdates[0].taskId is required when the request has no message");
        var updates = (await CheckArtifactUpdatesAsync(request, call, taskId, ct).ConfigureAwait(false))!;
        var key = Key(call, taskId);
        // Never ask the card provider under the task lock: the locks are shared by many tasks.
        var pushEnabled = await _engine.PushEnabledForTenantAsync(key, ct).ConfigureAwait(false);
        using var gate = await _engine.LockAsync(key, ct).ConfigureAwait(false);
        var record = await _tasks.GetAsync(key, ct).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(taskId);
        if (record.Task.Status.State.IsTerminal())
            throw new A2AException(A2AErrorCode.UnsupportedOperation, "The task is in a terminal state.")
                { Metadata = new Dictionary<string, string> { ["taskId"] = taskId } };
        RequireClientOwned(record.Task, updates);
        foreach (var update in updates)
        {
            update.ContextId = record.Task.ContextId;
            if (await _engine.StoreClientArtifactAsync(key, record, update, pushEnabled, ct).ConfigureAwait(false) is
                { } applied) record = applied.Record;
        }

        return record;
    }

    /// <summary>A2A 1.1 <c>TaskGenerationMismatchError</c> with the metadata of spec 11.6.</summary>
    private static A2AException GenerationMismatch(string taskId, long expected, long current)
    {
        return new A2AException(A2AErrorCode.TaskGenerationMismatch,
            string.Create(CultureInfo.InvariantCulture,
                $"Task generation mismatch: expected {expected} but current generation is {current}"))
        {
            Metadata = new Dictionary<string, string>
            {
                ["taskId"] = taskId,
                ["expectedGeneration"] = expected.ToString(CultureInfo.InvariantCulture),
                ["currentGeneration"] = current.ToString(CultureInfo.InvariantCulture)
            }
        };
    }

    private static A2AException NotCancelable(string taskId)
    {
        return new A2AException(A2AErrorCode.TaskNotCancelable)
            { Metadata = new Dictionary<string, string> { ["taskId"] = taskId } };
    }

    private static string ScopeKey(string tenant, string taskId)
    {
        return TaskExecutionEngine.ScopeKey(tenant, taskId);
    }

    /// <summary>Waits for all running executions to finish. For tests and graceful shutdown.</summary>
    public Task WhenIdleAsync()
    {
        return _engine.WhenIdleAsync();
    }

    /// <param name="Context">The request context for the agent.</param>
    /// <param name="PushConfig">The push config to save when a new task is created.</param>
    /// <param name="LastSequence">
    ///     For a follow-up, the task's last event sequence right after the message was appended; 0 for
    ///     a new task.
    /// </param>
    /// <param name="Duplicate">
    ///     The follow-up's message id was already in the task's history: nothing was appended and no agent
    ///     must start.
    /// </param>
    /// <param name="AcceptedStatus">
    ///     For a follow-up, the task's status when its message was committed (a follow-up does not
    ///     change it).
    /// </param>
    /// <param name="StartPending">
    ///     The engine counts this request's start as pending (see
    ///     <see cref="TaskExecutionEngine.BeginStart" />): it ends it once the execution is registered or the start failed.
    /// </param>
    private sealed record Prepared(
        AgentRequestContext Context,
        TaskPushNotificationConfig? PushConfig,
        long LastSequence,
        bool Duplicate = false,
        AgentTaskStatus? AcceptedStatus = null,
        bool StartPending = false);

    /// <summary>
    ///     The audit state of one accepted message (see <see cref="AcceptAsync" />). The events are set only after their
    ///     <see cref="A2AAuditor.BeginAsync" /> returned, so a request refused by a failing sink (fail closed) writes no final
    ///     row. The other
    ///     fields are what the message turned out to be, set once it is committed.
    /// </summary>
    private sealed class AuditTicket
    {
        /// <summary>The message event, once begun.</summary>
        public A2AAuditEvent? Message { get; set; }

        /// <summary>The inline push config event, once begun.</summary>
        public A2AAuditEvent? Push { get; set; }

        /// <summary>
        ///     The final action of <see cref="Message" />, when the message turned out to be another kind than first
        ///     classified.
        /// </summary>
        public A2AAuditAction? Action { get; set; }

        /// <summary>The final resource of <see cref="Message" /> (the status message an approval used).</summary>
        public string? Resource { get; set; }

        /// <summary>The reason of a success row: <c>duplicate</c>, <c>recovered</c>, or the answer of a form.</summary>
        public string? Reason { get; set; }

        /// <summary>The message was a duplicate: no push config was saved either.</summary>
        public string? PushReason { get; set; }
    }

    /// <summary>
    ///     What a follow-up or new task was accepted as. <see cref="Message" /> is the one the agent gets: a recovery
    ///     replaces the retry's.
    /// </summary>
    private sealed record Accepted(
        Message Message,
        string ContextId,
        AgentTask? Existing,
        long LastSequence,
        bool Duplicate,
        bool StartPending,
        ApprovalState Approval,
        ElicitationAnswer? Elicitation,
        string? ApprovalResource);

    /// <summary>Runs CancelTask for a tenant without an agent: the default cancel (the task is marked canceled).</summary>
    private sealed class CancelOnlyAgent : IAgentExecutor
    {
        public static readonly CancelOnlyAgent Instance = new();

        public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(NoAgentText);
        }
    }
}