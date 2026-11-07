namespace A2A.Client;

/// <summary>
/// All A2A operations (spec §3.1). Implemented by <see cref="A2AClient"/> and by each transport.
/// </summary>
public interface IA2AClient
{
    Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default);

    Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken cancellationToken = default);

    Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken cancellationToken = default);

    Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken cancellationToken = default);

    Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, CancellationToken cancellationToken = default);

    Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default);

    Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, CancellationToken cancellationToken = default);

    Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default);

    Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Settings shared by all transports.</summary>
public sealed class A2AClientOptions
{
    /// <summary>Sent as the A2A-Version header on every request.</summary>
    public string ProtocolVersion { get; set; } = A2AProtocol.Version;

    /// <summary>Extension URIs sent in the A2A-Extensions header.</summary>
    public IList<string> Extensions { get; set; } = new List<string>();

    /// <summary>
    /// Tenant applied to every request that does not set one. Filled automatically from the selected
    /// <see cref="AgentInterface.Tenant"/> by <see cref="A2AClientFactory"/>.
    /// </summary>
    public string? Tenant { get; set; }

    /// <summary>Bindings this client may use, for interface selection. Order does not matter: the card's order wins (spec §8.3.2).</summary>
    public IList<string> SupportedBindings { get; set; } = new List<string> { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    /// <summary>
    /// HTTP method for REST SubscribeToTask. The spec text says POST, the normative proto says GET. Default POST;
    /// servers built with this SDK accept both.
    /// </summary>
    public HttpMethod RestSubscribeMethod { get; set; } = HttpMethod.Post;

    /// <summary>When a card is known, fail fast if a capability (streaming, push, extended card) is not declared. Default true.</summary>
    public bool ValidateCapabilities { get; set; } = true;
}
