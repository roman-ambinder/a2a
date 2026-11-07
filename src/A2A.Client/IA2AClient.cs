namespace A2A.Client;

/// <summary>
/// All A2A operations (spec §3.1). Implemented by <see cref="A2AClient"/> and by each transport. Every method takes
/// optional per-call <see cref="A2ARequestOptions"/>.
/// </summary>
public interface IA2AClient
{
    Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);
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
    /// Transports for bindings beyond JSON-RPC and HTTP+JSON, used by <see cref="A2AClientFactory"/>. A factory's binding
    /// must also be in <see cref="SupportedBindings"/> to be selected. A factory for JSONRPC or HTTP+JSON replaces the built-in one.
    /// </summary>
    public IList<IA2ATransportFactory> TransportFactories { get; set; } = new List<IA2ATransportFactory>();

    /// <summary>
    /// HTTP method for REST SubscribeToTask. The spec text says POST, the normative proto says GET. Default POST;
    /// servers built with this SDK accept both.
    /// </summary>
    public HttpMethod RestSubscribeMethod { get; set; } = HttpMethod.Post;

    /// <summary>When a card is known, fail fast if a capability (streaming, push, extended card) is not declared. Default true.</summary>
    public bool ValidateCapabilities { get; set; } = true;

    /// <summary>
    /// Automatic retries after the first attempt of a unary call. Default 2 (3 attempts in all); 0 disables retries.
    /// <see cref="A2ARequestOptions.MaxRetries"/> overrides it per call. See <see cref="A2ARequestOptions.MaxRetries"/> for what is retried.
    /// </summary>
    public int MaxRetries
    {
        get => _maxRetries;
        set => _maxRetries = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "MaxRetries cannot be negative.");
    }

    private int _maxRetries = 2;

    /// <summary>Clock used for retry delays. Default <see cref="System.TimeProvider.System"/>. Replace it in tests.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
