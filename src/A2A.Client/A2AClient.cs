using System.Runtime.CompilerServices;
using A2A.Client.Streaming;
using A2A.Validation;

namespace A2A.Client;

/// <summary>
/// High-level A2A client. Wraps a transport, fills in the tenant, checks declared capabilities,
/// and adds convenience overloads.
/// </summary>
/// <remarks>
/// Every operation is a span of the <c>A2A.Client</c> <see cref="System.Diagnostics.ActivitySource"/> (see
/// <see cref="A2A.Diagnostics.A2ATelemetry"/>), covering retries and stream reconnects; stream events are span events. The
/// transports send the current span as a W3C <c>traceparent</c> header (gRPC metadata), so server spans join the trace.
/// </remarks>
public sealed class A2AClient : IA2AClient
{
    private readonly IA2AClient _transport;
    private readonly A2AClientOptions _options;
    private readonly string? _binding;

    /// <summary>Creates a client over <paramref name="transport"/>. <paramref name="options"/> defaults to a new <see cref="A2AClientOptions"/>. <paramref name="agentCard"/> enables capability checks when given.</summary>
    public A2AClient(IA2AClient transport, A2AClientOptions? options = null, AgentCard? agentCard = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new A2AClientOptions();
        AgentCard = agentCard;
        _binding = (transport as ITransportBinding)?.Binding;
    }

    /// <summary>The card this client was created from, if any.</summary>
    public AgentCard? AgentCard { get; private set; }

    /// <summary>The binding name for the client's spans (null when the transport names none).</summary>
    internal string? TelemetryBinding => _binding;

    /// <summary>The protocol version the client speaks, for the client's spans.</summary>
    internal string TelemetryProtocolVersion => _options.ProtocolVersion;

    // ---- Convenience ----

    /// <summary>Sends a text message. Pass <paramref name="taskId"/>/<paramref name="contextId"/> to continue a conversation.</summary>
    public Task<SendMessageResponse> SendMessageAsync(string text, string? contextId = null, string? taskId = null, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendMessageAsync(new SendMessageRequest { Message = Message.FromUser(text, contextId, taskId) }, options, cancellationToken);

    /// <summary>Sends <paramref name="message"/> and waits for the result (spec §3.1.1).</summary>
    public Task<SendMessageResponse> SendMessageAsync(Message message, SendMessageConfiguration? configuration = null, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendMessageAsync(new SendMessageRequest { Message = message, Configuration = configuration }, options, cancellationToken);

    /// <summary>Sends <paramref name="message"/> and streams the updates (spec §3.1.2).</summary>
    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(Message message, SendMessageConfiguration? configuration = null, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendStreamingMessageAsync(new SendMessageRequest { Message = message, Configuration = configuration }, options, cancellationToken);

    /// <summary>Fetches a task (spec §3.1.3). <paramref name="historyLength"/> limits the history; null means the server default.</summary>
    public Task<AgentTask> GetTaskAsync(string taskId, int? historyLength = null, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        GetTaskAsync(new GetTaskRequest { Id = taskId, HistoryLength = historyLength }, options, cancellationToken);

    /// <summary>Asks the agent to cancel a task (spec §3.1.5).</summary>
    public Task<AgentTask> CancelTaskAsync(string taskId, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        CancelTaskAsync(new CancelTaskRequest { Id = taskId }, options, cancellationToken);

    /// <summary>Streams updates of an existing task (spec §3.1.6).</summary>
    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(string taskId, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId }, options, cancellationToken);

    /// <summary>
    /// Polls a task until it reaches a terminal or interrupted state. Use when streaming and push are not available,
    /// or to recover after a dropped stream.
    /// </summary>
    public async Task<AgentTask> WaitForTaskAsync(string taskId, TimeSpan? pollInterval = null, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var delay = pollInterval ?? TimeSpan.FromSeconds(1);
        while (true)
        {
            var task = await GetTaskAsync(taskId, 0, options, cancellationToken).ConfigureAwait(false);
            if (task.Status.State.IsTerminal() || task.Status.State.IsInterrupted()) return task;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Pages through all tasks matching <paramref name="request"/>.</summary>
    public async IAsyncEnumerable<AgentTask> ListAllTasksAsync(ListTasksRequest? request = null, A2ARequestOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        request ??= new ListTasksRequest();
        while (true)
        {
            var page = await ListTasksAsync(request, options, cancellationToken).ConfigureAwait(false);
            foreach (var t in page.Tasks) yield return t;
            if (string.IsNullOrEmpty(page.NextPageToken)) yield break;
            request = new ListTasksRequest
            {
                Tenant = request.Tenant, ContextId = request.ContextId, Status = request.Status, PageSize = request.PageSize,
                PageToken = page.NextPageToken, HistoryLength = request.HistoryLength,
                StatusTimestampAfter = request.StatusTimestampAfter, IncludeArtifacts = request.IncludeArtifacts,
            };
        }
    }

    /// <summary>Fetches the extended card and makes it the current card for capability checks (spec §3.1.11).</summary>
    public async Task<AgentCard> RefreshWithExtendedAgentCardAsync(A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var card = await GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest(), options, cancellationToken).ConfigureAwait(false);
        AgentCard = card;
        return card;
    }

    // ---- IA2AClient ----

    /// <inheritdoc />
    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureMessageId(request);
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        if (request.Configuration?.TaskPushNotificationConfig is not null) RequireCapability(AgentCard?.Capabilities.PushNotifications, A2AErrorCode.PushNotificationNotSupported);
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.SendMessage, this, request.Tenant, request.Message?.TaskId, () =>
        {
            var call = _transport.SendMessageAsync(request, options, cancellationToken);
            return _options.OnMessageReceived is null ? call : InspectAsync(call, cancellationToken);
        }, cancellationToken, request.Message);
    }

    private async Task<SendMessageResponse> InspectAsync(Task<SendMessageResponse> call, CancellationToken ct)
    {
        var response = await call.ConfigureAwait(false);
        switch (response)
        {
            case MessageResponse { Message: var m }:
                await NotifyAsync(new ReceivedMessage(m, ReceivedMessageSource.DirectReply, m.TaskId, m.ContextId), ct).ConfigureAwait(false);
                break;
            case TaskResponse { Task: var t }:
                await InspectAsync(t, ct).ConfigureAwait(false);
                break;
        }
        return response;
    }

    private async Task<AgentTask> InspectAsync(Task<AgentTask> call, CancellationToken ct)
    {
        var task = await call.ConfigureAwait(false);
        await InspectAsync(task, ct).ConfigureAwait(false);
        return task;
    }

    private ValueTask InspectAsync(AgentTask task, CancellationToken ct) =>
        task.Status?.Message is { } m
            ? NotifyAsync(new ReceivedMessage(m, ReceivedMessageSource.TaskSnapshot, task.Id, task.ContextId), ct)
            : ValueTask.CompletedTask;

    /// <summary>Passes every agent message in <paramref name="source"/> to <see cref="A2AClientOptions.OnMessageReceived"/> before yielding its event.</summary>
    private IAsyncEnumerable<StreamResponse> Inspect(IAsyncEnumerable<StreamResponse> source) =>
        _options.OnMessageReceived is null ? source : InspectStreamAsync(source);

    private async IAsyncEnumerable<StreamResponse> InspectStreamAsync(IAsyncEnumerable<StreamResponse> source, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in source.WithCancellation(ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case MessageEvent { Message: var m }:
                    await NotifyAsync(new ReceivedMessage(m, ReceivedMessageSource.DirectReply, m.TaskId, m.ContextId), ct).ConfigureAwait(false);
                    break;
                case StatusUpdateEvent { StatusUpdate: { Status.Message: { } m } su }:
                    await NotifyAsync(new ReceivedMessage(m, ReceivedMessageSource.StatusUpdate, su.TaskId, su.ContextId), ct).ConfigureAwait(false);
                    break;
                case TaskEvent { Task: var t }:
                    await InspectAsync(t, ct).ConfigureAwait(false);
                    break;
            }
            yield return evt;
        }
    }

    private ValueTask NotifyAsync(ReceivedMessage received, CancellationToken ct) =>
        _options.OnMessageReceived is { } hook ? hook(received, ct) : ValueTask.CompletedTask;

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureMessageId(request);
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.Streaming, A2AErrorCode.UnsupportedOperation, "Agent does not declare streaming support.");
        return ClientTelemetry.TraceStream(A2AProtocol.Methods.SendStreamingMessage, this, request.Tenant, request.Message?.TaskId, Inspect(ResumableStream.RunAsync(
            knownTaskId: null,
            ct => _transport is IResumableTransport rt
                ? rt.SendStreamingFramesAsync(request, options, ct)
                : Frames(_transport.SendStreamingMessageAsync(request, options, ct), ct),
            (taskId, cursor, ct) => Resubscribe(request.Tenant, taskId, cursor, options, ct),
            (taskId, ct) => GetTaskForResume(request.Tenant, taskId, options, ct),
            _options.TimeProvider, cancellationToken)), request.Message);
    }

    /// <summary>
    /// Follows a task: subscribes, and when the connection drops reconnects with backoff (0.5 s, 1 s, 2 s, 4 s, 8 s; then
    /// the error is thrown) and <c>Last-Event-ID</c>, so no event is lost or repeated. A task that has already finished
    /// is answered with its final snapshot as a <see cref="TaskEvent"/>. The same <paramref name="options"/> (headers,
    /// credentials) are used on every reconnect.
    /// </summary>
    /// <remarks>
    /// "Not repeated" needs SSE event ids, which this SDK's servers send. A peer without event ids gives no cursor to resume
    /// from: after a reconnect it may send events again (starting with a task snapshot), and they are yielded again.
    /// The same holds for <see cref="SendStreamingMessageAsync(SendMessageRequest, A2ARequestOptions?, CancellationToken)"/>.
    /// </remarks>
    public IAsyncEnumerable<StreamResponse> ObserveTaskAsync(string taskId, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskId);
        RequireCapability(AgentCard?.Capabilities.Streaming, A2AErrorCode.UnsupportedOperation, "Agent does not declare streaming support.");
        var tenant = _options.Tenant;
        return ClientTelemetry.TraceStream(A2AProtocol.Methods.SubscribeToTask, this, tenant, taskId, Inspect(ResumableStream.RunAsync(
            knownTaskId: taskId,
            ct => Resubscribe(tenant, taskId, null, options, ct),
            (id, cursor, ct) => Resubscribe(tenant, id, cursor, options, ct),
            (id, ct) => GetTaskForResume(tenant, id, options, ct),
            _options.TimeProvider, cancellationToken)));
    }

    private IAsyncEnumerable<StreamFrame> Resubscribe(string? tenant, string taskId, string? lastEventId, A2ARequestOptions? options, CancellationToken ct)
    {
        var request = new SubscribeToTaskRequest { Id = taskId, Tenant = tenant ?? _options.Tenant };
        return _transport is IResumableTransport rt
            ? rt.SubscribeToTaskAsync(request, options, lastEventId, ct)
            : Frames(_transport.SubscribeToTaskAsync(request, options, ct), ct);
    }

    private Task<AgentTask> GetTaskForResume(string? tenant, string taskId, A2ARequestOptions? options, CancellationToken ct) =>
        _transport.GetTaskAsync(new GetTaskRequest { Id = taskId, Tenant = tenant ?? _options.Tenant }, options, ct);

    private static async IAsyncEnumerable<StreamFrame> Frames(IAsyncEnumerable<StreamResponse> source, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false)) yield return new StreamFrame(item, null);
    }

    /// <inheritdoc />
    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.GetTask, this, request.Tenant, request.Id, () =>
        {
            var call = _transport.GetTaskAsync(request, options, cancellationToken);
            return _options.OnMessageReceived is null ? call : InspectAsync(call, cancellationToken);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.ListTasks, this, request.Tenant, null,
            () => _transport.ListTasksAsync(request, options, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.CancelTask, this, request.Tenant, request.Id, () =>
        {
            var call = _transport.CancelTaskAsync(request, options, cancellationToken);
            return _options.OnMessageReceived is null ? call : InspectAsync(call, cancellationToken);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.Streaming, A2AErrorCode.UnsupportedOperation, "Agent does not declare streaming support.");
        return ClientTelemetry.TraceStream(A2AProtocol.Methods.SubscribeToTask, this, request.Tenant, request.Id,
            Inspect(_transport.SubscribeToTaskAsync(request, options, cancellationToken)));
    }

    /// <inheritdoc />
    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        config.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.PushNotifications, A2AErrorCode.PushNotificationNotSupported);
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.CreateTaskPushNotificationConfig, this, config.Tenant, config.TaskId,
            () => _transport.CreateTaskPushNotificationConfigAsync(config, options, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.GetTaskPushNotificationConfig, this, request.Tenant, request.TaskId,
            () => _transport.GetTaskPushNotificationConfigAsync(request, options, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.ListTaskPushNotificationConfigs, this, request.Tenant, request.TaskId,
            () => _transport.ListTaskPushNotificationConfigsAsync(request, options, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.DeleteTaskPushNotificationConfig, this, request.Tenant, request.TaskId,
            () => _transport.DeleteTaskPushNotificationConfigAsync(request, options, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.ExtendedAgentCard, A2AErrorCode.UnsupportedOperation, "Agent does not declare an extended agent card.");
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.GetExtendedAgentCard, this, request.Tenant, null,
            () => _transport.GetExtendedAgentCardAsync(request, options, cancellationToken), cancellationToken);
    }

    private static void EnsureMessageId(SendMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Message is { } m && string.IsNullOrWhiteSpace(m.MessageId)) m.MessageId = Message.NewId();
    }

    private void RequireCapability(bool? declared, A2AErrorCode code, string? message = null)
    {
        if (!_options.ValidateCapabilities || AgentCard is null) return;
        if (declared != true) throw new A2AException(code, message);
    }
}
