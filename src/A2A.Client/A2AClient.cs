using System.Runtime.CompilerServices;
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
    public Task<SendMessageResponse> SendMessageAsync(string text, string? contextId = null, string? taskId = null, CancellationToken cancellationToken = default) =>
        SendMessageAsync(new SendMessageRequest { Message = Message.FromUser(text, contextId, taskId) }, cancellationToken);

    public Task<SendMessageResponse> SendMessageAsync(Message message, SendMessageConfiguration? configuration = null, CancellationToken cancellationToken = default) =>
        SendMessageAsync(new SendMessageRequest { Message = message, Configuration = configuration }, cancellationToken);

    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(Message message, SendMessageConfiguration? configuration = null, CancellationToken cancellationToken = default) =>
        SendStreamingMessageAsync(new SendMessageRequest { Message = message, Configuration = configuration }, cancellationToken);

    public Task<AgentTask> GetTaskAsync(string taskId, int? historyLength = null, CancellationToken cancellationToken = default) =>
        GetTaskAsync(new GetTaskRequest { Id = taskId, HistoryLength = historyLength }, cancellationToken);

    public Task<AgentTask> CancelTaskAsync(string taskId, CancellationToken cancellationToken = default) =>
        CancelTaskAsync(new CancelTaskRequest { Id = taskId }, cancellationToken);

    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(string taskId, CancellationToken cancellationToken = default) =>
        SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId }, cancellationToken);

    /// <summary>
    /// Polls a task until it reaches a terminal or interrupted state. Use when streaming and push are not available,
    /// or to recover after a dropped stream.
    /// </summary>
    public async Task<AgentTask> WaitForTaskAsync(string taskId, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        var delay = pollInterval ?? TimeSpan.FromSeconds(1);
        while (true)
        {
            var task = await GetTaskAsync(taskId, 0, cancellationToken).ConfigureAwait(false);
            if (task.Status.State.IsTerminal() || task.Status.State.IsInterrupted()) return task;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Pages through all tasks matching <paramref name="request"/>.</summary>
    public async IAsyncEnumerable<AgentTask> ListAllTasksAsync(ListTasksRequest? request = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        request ??= new ListTasksRequest();
        while (true)
        {
            var page = await ListTasksAsync(request, cancellationToken).ConfigureAwait(false);
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
    public async Task<AgentCard> RefreshWithExtendedAgentCardAsync(CancellationToken cancellationToken = default)
    {
        var card = await GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest(), cancellationToken).ConfigureAwait(false);
        AgentCard = card;
        return card;
    }

    // ---- IA2AClient ----

    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default)
    {
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        if (request.Configuration?.TaskPushNotificationConfig is not null) RequireCapability(AgentCard?.Capabilities.PushNotifications, A2AErrorCode.PushNotificationNotSupported);
        return _transport.SendMessageAsync(request, cancellationToken);
    }

    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default)
    {
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.Streaming, A2AErrorCode.UnsupportedOperation, "Agent does not declare streaming support.");
        return _transport.SendStreamingMessageAsync(request, cancellationToken);
    }

    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        return _transport.GetTaskAsync(request, cancellationToken);
    }

    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        return _transport.ListTasksAsync(request, cancellationToken);
    }

    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        return _transport.CancelTaskAsync(request, cancellationToken);
    }

    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.Streaming, A2AErrorCode.UnsupportedOperation, "Agent does not declare streaming support.");
        return _transport.SubscribeToTaskAsync(request, cancellationToken);
    }

    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, CancellationToken cancellationToken = default)
    {
        config.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.PushNotifications, A2AErrorCode.PushNotificationNotSupported);
        return _transport.CreateTaskPushNotificationConfigAsync(config, cancellationToken);
    }

    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        return _transport.GetTaskPushNotificationConfigAsync(request, cancellationToken);
    }

    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        return _transport.ListTaskPushNotificationConfigsAsync(request, cancellationToken);
    }

    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        return _transport.DeleteTaskPushNotificationConfigAsync(request, cancellationToken);
    }

    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, CancellationToken cancellationToken = default)
    {
        request.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.ExtendedAgentCard, A2AErrorCode.UnsupportedOperation, "Agent does not declare an extended agent card.");
        return _transport.GetExtendedAgentCardAsync(request, cancellationToken);
    }

    private void RequireCapability(bool? declared, A2AErrorCode code, string? message = null)
    {
        if (!_options.ValidateCapabilities || AgentCard is null) return;
        if (declared != true) throw new A2AException(code, message);
    }
}
