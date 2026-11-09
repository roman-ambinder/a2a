using A2A.Client.Resilience;

namespace A2A.Client;

/// <summary>
/// All A2A operations (spec §3.1). Implemented by <see cref="A2AClient"/> and by each transport. Every method takes
/// optional per-call <see cref="A2ARequestOptions"/>.
/// </summary>
public interface IA2AClient
{
    /// <summary>SendMessage (spec §3.1.1): sends a message and waits for the result, a task or a message.</summary>
    Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>SendStreamingMessage (spec §3.1.2): sends a message and streams the task updates.</summary>
    IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>GetTask (spec §3.1.3): fetches one task.</summary>
    Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>ListTasks (spec §3.1.4): lists tasks with filters and paging.</summary>
    Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>CancelTask (spec §3.1.5): asks the agent to cancel a task.</summary>
    Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>SubscribeToTask (spec §3.1.6): streams updates of an existing task.</summary>
    IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>CreateTaskPushNotificationConfig (spec §3.1.7): registers a webhook for a task.</summary>
    Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>GetTaskPushNotificationConfig (spec §3.1.8): fetches one webhook registration.</summary>
    Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>ListTaskPushNotificationConfigs (spec §3.1.9): lists the webhooks of a task.</summary>
    Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>DeleteTaskPushNotificationConfig (spec §3.1.10): removes a webhook registration.</summary>
    Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>GetExtendedAgentCard (spec §3.1.11): fetches the authenticated, extended Agent Card.</summary>
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
    /// Tenant applied to every request that does not set one: a per-request <c>Tenant</c> always wins over this value.
    /// <see cref="A2AClientFactory"/> replaces this value with the selected <see cref="AgentInterface.Tenant"/> when the
    /// card gives one for that interface.
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

    /// <summary>Clock used for retry delays and the circuit breaker. Default <see cref="System.TimeProvider.System"/>. Replace it in tests.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Circuit breaker for the peer of each <see cref="A2AClient"/>. Default null: no breaker. When set, a client stops sending
    /// requests after <see cref="CircuitBreakerOptions.FailureThreshold"/> failed calls in a row, and a call it refuses throws
    /// <see cref="CircuitOpenException"/>. The state belongs to one <see cref="A2AClient"/>, so share one client per peer.
    /// It applies to every binding, gRPC included. Time comes from <see cref="TimeProvider"/>.
    /// </summary>
    public CircuitBreakerOptions? CircuitBreaker { get; set; }

    /// <summary>
    /// Called by <see cref="A2AClient"/> for every agent message it receives, before the caller sees it: a direct reply, the
    /// message of a status update, and the latest status message of a task snapshot (SendMessage, SendStreamingMessage,
    /// SubscribeToTask, ObserveTaskAsync, GetTask and CancelTask; unary and streamed; every binding, gRPC included).
    /// Throw to abort the call: the exception reaches the caller, and a stream stops. Default null.
    /// </summary>
    /// <remarks>
    /// This is the place to check each turn of a multi-turn exchange against the user's original intent, and to require
    /// human approval before the agent's request leads to a sensitive action. A peer agent can steer a long conversation one
    /// small step at a time ("agent session smuggling"); a check on every message catches drift that a check on the first
    /// one misses. The same snapshot message can be seen more than once (for example when polling with GetTask, or after a
    /// stream reconnects), so make the check idempotent.
    /// </remarks>
    public Func<ReceivedMessage, CancellationToken, ValueTask>? OnMessageReceived { get; set; }

    /// <summary>
    /// Decides which endpoints an Agent Card may name. <see cref="A2AClientFactory"/> checks every interface of the card that this
    /// client could select, before it builds a client; a refusal throws <see cref="A2AErrorCode.InvalidAgentResponse"/> and counts
    /// <c>a2a.client.endpoint.refused</c>. Null or <see cref="AgentEndpointPolicies.AllowAll"/> turns the check off (no DNS lookup).
    /// Default on .NET 8 and later: <see cref="AgentEndpointPolicies.MatchCardNetwork"/>. On netstandard2.0 the default is null, and
    /// the presets do not exist (they need A2A.Security.Http), the same as the <c>jku</c> limit of <see cref="A2ACardResolver"/>.
    /// </summary>
    /// <remarks>
    /// The check is pre-flight only. A host can resolve to another address when the client connects (DNS rebinding); to close that,
    /// give the client an <see cref="HttpClient"/> built on <c>SafeHttp.CreateHandler</c>.
    /// </remarks>
#if NETSTANDARD2_0
    public Func<AgentEndpointCheck, bool>? EndpointPolicy { get; set; }
#else
    public Func<AgentEndpointCheck, bool>? EndpointPolicy { get; set; } = AgentEndpointPolicies.MatchCardNetwork;
#endif

    /// <summary>
    /// A copy of these options. <see cref="Extensions"/>, <see cref="SupportedBindings"/> and <see cref="TransportFactories"/> are
    /// new lists; other values are shared. Every factory copies options through this method, so a new option must be added here.
    /// </summary>
    public A2AClientOptions Clone() => new()
    {
        ProtocolVersion = ProtocolVersion,
        Extensions = new List<string>(Extensions),
        Tenant = Tenant,
        SupportedBindings = new List<string>(SupportedBindings),
        TransportFactories = new List<IA2ATransportFactory>(TransportFactories),
        RestSubscribeMethod = RestSubscribeMethod,
        ValidateCapabilities = ValidateCapabilities,
        MaxRetries = MaxRetries,
        TimeProvider = TimeProvider,
        CircuitBreaker = CircuitBreaker,
        OnMessageReceived = OnMessageReceived,
        EndpointPolicy = EndpointPolicy,
    };
}
