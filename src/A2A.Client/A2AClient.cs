using System.Runtime.CompilerServices;
using A2A.Client.Streaming;
using A2A.Validation;

namespace A2A.Client;

/// <summary>
/// High-level A2A client. Wraps a transport, fills in the tenant, checks declared capabilities,
/// and adds convenience overloads.
/// </summary>
public sealed class A2AClient : IA2AClient
{
    private readonly IA2AClient _transport;
    private readonly A2AClientOptions _options;

    public A2AClient(IA2AClient transport, A2AClientOptions? options = null, AgentCard? agentCard = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new A2AClientOptions();
        AgentCard = agentCard;
    }

    /// <summary>The card this client was created from, if any.</summary>
    public AgentCard? AgentCard { get; private set; }

    // ---- Convenience ----

    /// <summary>Sends a text message. Pass <paramref name="taskId"/>/<paramref name="contextId"/> to continue a conversation.</summary>
    public Task<SendMessageResponse> SendMessageAsync(string text, string? contextId = null, string? taskId = null, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendMessageAsync(new SendMessageRequest { Message = Message.FromUser(text, contextId, taskId) }, options, cancellationToken);

    public Task<SendMessageResponse> SendMessageAsync(Message message, SendMessageConfiguration? configuration = null, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendMessageAsync(new SendMessageRequest { Message = message, Configuration = configuration }, options, cancellationToken);

    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(Message message, SendMessageConfiguration? configuration = null, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendStreamingMessageAsync(new SendMessageRequest { Message = message, Configuration = configuration }, options, cancellationToken);

    public Task<AgentTask> GetTaskAsync(string taskId, int? historyLength = null, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        GetTaskAsync(new GetTaskRequest { Id = taskId, HistoryLength = historyLength }, options, cancellationToken);

    public Task<AgentTask> CancelTaskAsync(string taskId, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        CancelTaskAsync(new CancelTaskRequest { Id = taskId }, options, cancellationToken);

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

    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureMessageId(request);
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        if (request.Configuration?.TaskPushNotificationConfig is not null) RequireCapability(AgentCard?.Capabilities.PushNotifications, A2AErrorCode.PushNotificationNotSupported);
        return _transport.SendMessageAsync(request, options, cancellationToken);
    }

    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureMessageId(request);
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.Streaming, A2AErrorCode.UnsupportedOperation, "Agent does not declare streaming support.");
        return ResumableStream.RunAsync(
            knownTaskId: null,
            ct => _transport is IResumableTransport rt
                ? rt.SendStreamingFramesAsync(request, options, ct)
                : Frames(_transport.SendStreamingMessageAsync(request, options, ct)),
            (taskId, cursor, ct) => Resubscribe(request.Tenant, taskId, cursor, options, ct),
            (taskId, ct) => GetTaskForResume(request.Tenant, taskId, options, ct),
            _options.TimeProvider, cancellationToken);
    }

    /// <summary>
    /// Follows a task: subscribes, and when the connection drops reconnects with backoff (0.5 s, 1 s, 2 s, 4 s, 8 s; then
    /// the error is thrown) and <c>Last-Event-ID</c>, so no event is lost or repeated. A task that has already finished
    /// is answered with its final snapshot as a <see cref="TaskEvent"/>. The same <paramref name="options"/> (headers,
    /// credentials) are used on every reconnect.
    /// </summary>
    public IAsyncEnumerable<StreamResponse> ObserveTaskAsync(string taskId, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskId);
        RequireCapability(AgentCard?.Capabilities.Streaming, A2AErrorCode.UnsupportedOperation, "Agent does not declare streaming support.");
        var tenant = _options.Tenant;
        return ResumableStream.RunAsync(
            knownTaskId: taskId,
            ct => Resubscribe(tenant, taskId, null, options, ct),
            (id, cursor, ct) => Resubscribe(tenant, id, cursor, options, ct),
            (id, ct) => GetTaskForResume(tenant, id, options, ct),
            _options.TimeProvider, cancellationToken);
    }

    private IAsyncEnumerable<StreamFrame> Resubscribe(string? tenant, string taskId, string? lastEventId, A2ARequestOptions? options, CancellationToken ct)
    {
        var request = new SubscribeToTaskRequest { Id = taskId, Tenant = tenant ?? _options.Tenant };
        return _transport is IResumableTransport rt
            ? rt.SubscribeToTaskAsync(request, options, lastEventId, ct)
            : Frames(_transport.SubscribeToTaskAsync(request, options, ct));
    }

    private Task<AgentTask> GetTaskForResume(string? tenant, string taskId, A2ARequestOptions? options, CancellationToken ct) =>
        _transport.GetTaskAsync(new GetTaskRequest { Id = taskId, Tenant = tenant ?? _options.Tenant }, options, ct);

    private static async IAsyncEnumerable<StreamFrame> Frames(IAsyncEnumerable<StreamResponse> source, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false)) yield return new StreamFrame(item, null);
    }

    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        return _transport.GetTaskAsync(request, options, cancellationToken);
    }

    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        return _transport.ListTasksAsync(request, options, cancellationToken);
    }

    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        return _transport.CancelTaskAsync(request, options, cancellationToken);
    }

    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.Streaming, A2AErrorCode.UnsupportedOperation, "Agent does not declare streaming support.");
        return _transport.SubscribeToTaskAsync(request, options, cancellationToken);
    }

    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        config.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.PushNotifications, A2AErrorCode.PushNotificationNotSupported);
        return _transport.CreateTaskPushNotificationConfigAsync(config, options, cancellationToken);
    }

    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        return _transport.GetTaskPushNotificationConfigAsync(request, options, cancellationToken);
    }

    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        return _transport.ListTaskPushNotificationConfigsAsync(request, options, cancellationToken);
    }

    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        return _transport.DeleteTaskPushNotificationConfigAsync(request, options, cancellationToken);
    }

    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.ExtendedAgentCard, A2AErrorCode.UnsupportedOperation, "Agent does not declare an extended agent card.");
        return _transport.GetExtendedAgentCardAsync(request, options, cancellationToken);
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
