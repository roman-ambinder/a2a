using A2A.Client.Resilience;

namespace A2A.Client;

/// <summary>
///     The A2A client: what application code calls. <see cref="A2AClient" /> implements it over a transport, adding
///     capability checks, response validation, the circuit breaker and stream resume; the shortcuts (<c>AskAsync</c>,
///     <c>StartConversation</c>, string overloads) extend it. Mock it in tests of code that calls an agent.
/// </summary>
public interface IA2AClient : IA2ATransport
{
    /// <summary>
    ///     The agent's card when the client has one (made from a card or by URL): capability checks and adapters read it.
    ///     Null for a client made for an endpoint without a card.
    /// </summary>
    AgentCard? AgentCard { get; }
}

/// <summary>
///     All A2A operations (spec §3.1) on one binding: what a transport implements (JSON-RPC, HTTP+JSON, gRPC, or your own
///     through an <see cref="IA2ATransportFactory" />). Application code calls an <see cref="IA2AClient" /> instead; wrap a
///     transport in an <see cref="A2AClient" /> to get one. Every method takes optional per-call
///     <see cref="A2ARequestOptions" />.
/// </summary>
public interface IA2ATransport
{
    /// <summary>SendMessage (spec §3.1.1): sends a message and waits for the result, a task or a message.</summary>
    Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>SendStreamingMessage (spec §3.1.2): sends a message and streams the task updates.</summary>
    IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>GetTask (spec §3.1.3): fetches one task.</summary>
    Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>ListTasks (spec §3.1.4): lists tasks with filters and paging.</summary>
    Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>CancelTask (spec §3.1.5): asks the agent to cancel a task.</summary>
    Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>SubscribeToTask (spec §3.1.6): streams updates of an existing task.</summary>
    IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>CreateTaskPushNotificationConfig (spec §3.1.7): registers a webhook for a task.</summary>
    Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>GetTaskPushNotificationConfig (spec §3.1.8): fetches one webhook registration.</summary>
    Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>ListTaskPushNotificationConfigs (spec §3.1.9): lists the webhooks of a task.</summary>
    Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(
        ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>DeleteTaskPushNotificationConfig (spec §3.1.10): removes a webhook registration.</summary>
    Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>GetExtendedAgentCard (spec §3.1.11): fetches the authenticated, extended Agent Card.</summary>
    Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Settings shared by all transports.</summary>
public sealed class A2AClientOptions
{
    /// <summary>Sent as the A2A-Version header on every request.</summary>
    public string ProtocolVersion { get; set; } = A2AProtocol.Version;

    /// <summary>
    ///     When true, the client refuses to register a webhook URL that is not <c>https</c> (spec §13.2: clients SHOULD use
    ///     HTTPS endpoints for webhook URLs), in <c>CreateTaskPushNotificationConfig</c> and in the push config of a
    ///     <c>SendMessage</c>. A loopback URL (<c>localhost</c>, <c>127.0.0.1</c>, <c>[::1]</c>) is allowed for local
    ///     development. Default false, so existing plain-HTTP webhooks (and test receivers) keep working; set it in
    ///     production. The SDK's server refuses plain-HTTP webhooks by default anyway
    ///     (<c>PushNotifications.AllowInsecureUrls</c>).
    /// </summary>
    public bool RequireHttpsWebhooks { get; set; }

    /// <summary>Extension URIs sent in the A2A-Extensions header.</summary>
    public IList<string> Extensions { get; set; } = new List<string>();

    /// <summary>
    ///     Tenant applied to every request that does not set one: a per-request <c>Tenant</c> always wins over this value.
    ///     <see cref="A2AClientFactory" /> replaces this value with the selected <see cref="AgentInterface.Tenant" /> when the
    ///     card gives one for that interface.
    /// </summary>
    public string? Tenant { get; set; }

    /// <summary>
    ///     Bindings this client may use, for interface selection. Order does not matter: the card's order wins (spec
    ///     §8.3.2).
    /// </summary>
    public IList<string> SupportedBindings { get; set; } =
        new List<string> { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    /// <summary>
    ///     Transports for bindings beyond JSON-RPC and HTTP+JSON, used by <see cref="A2AClientFactory" />. A factory's binding
    ///     must also be in <see cref="SupportedBindings" /> to be selected. A factory for JSONRPC or HTTP+JSON replaces the
    ///     built-in one.
    /// </summary>
    public IList<IA2ATransportFactory> TransportFactories { get; set; } = new List<IA2ATransportFactory>();

    /// <summary>
    ///     HTTP method for REST SubscribeToTask. The spec text says POST, the normative proto says GET. Default POST;
    ///     servers built with this SDK accept both.
    /// </summary>
    public HttpMethod RestSubscribeMethod { get; set; } = HttpMethod.Post;

    /// <summary>
    ///     When a card is known, fail fast if a capability (streaming, push, extended card) is not declared. Default
    ///     true.
    /// </summary>
    public bool ValidateCapabilities { get; set; } = true;

    /// <summary>
    ///     Reject an agent response that lacks a REQUIRED field (spec 5.7) with InvalidAgentResponse: a task without an id
    ///     or a status state, a stream event without its task or context id, a message without an id, a role or parts, and
    ///     an artifact without an id or parts. Applies to SendMessage, GetTask, ListTasks, CancelTask and every stream
    ///     event. Default true; set false to accept such responses from a peer that leaves fields out.
    /// </summary>
    public bool ValidateResponses { get; set; } = true;

    /// <summary>
    ///     Automatic retries after the first attempt of a unary call. Default 2 (3 attempts in all); 0 disables retries.
    ///     <see cref="A2ARequestOptions.MaxRetries" /> overrides it per call. See <see cref="A2ARequestOptions.MaxRetries" />
    ///     for what is retried.
    /// </summary>
    public int MaxRetries
    {
        get => _maxRetries;
        set => _maxRetries = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "MaxRetries cannot be negative.");
    }

    private int _maxRetries = 2;

    /// <summary>
    ///     Clock used for retry delays and the circuit breaker. Default <see cref="System.TimeProvider.System" />.
    ///     Replace it in tests.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    ///     Circuit breaker for the peer of each <see cref="A2AClient" />. Default null: no breaker. When set, a client stops
    ///     sending
    ///     requests after <see cref="CircuitBreakerOptions.FailureThreshold" /> failed calls in a row, and a call it refuses
    ///     throws
    ///     <see cref="CircuitOpenException" />. The state belongs to one <see cref="A2AClient" />, so share one client per
    ///     peer.
    ///     It applies to every binding, gRPC included. Time comes from <see cref="TimeProvider" />.
    /// </summary>
    public CircuitBreakerOptions? CircuitBreaker { get; set; }

    /// <summary>
    ///     When true, <c>SendMessageAsync</c> does not return a task that is still submitted or working: a server answers a
    ///     blocking send with the running task once its long-poll timeout passes, and the client then polls the task (as
    ///     <see cref="A2AClientExtensions.WaitForTaskAsync" /> does) until it is terminal or interrupted, and returns that task, with
    ///     the history the request asked for. A request that sets <c>ReturnImmediately</c> is returned as it is. Default
    ///     false.
    /// </summary>
    public bool WaitForTaskCompletion { get; set; }

    /// <summary>
    ///     Also writes the W3C trace context of the send into the message metadata (<c>traceparent</c> and
    ///     <c>tracestate</c>), not only into the request headers, so the agent can join the caller's trace when a proxy or
    ///     gateway on the way drops the headers. A message that already carries a <c>traceparent</c> is left as it is.
    ///     Applies to SendMessage and SendStreamingMessage. Default false.
    /// </summary>
    /// <remarks>
    ///     The metadata is part of the message, so the agent stores it in the task history, where everyone who can read the
    ///     task sees the trace and span ids. A server of this SDK uses it only when the request carries no
    ///     <c>traceparent</c> header, and leaves it out of the idempotency fingerprint.
    /// </remarks>
    public bool PropagateTraceInMetadata { get; set; }

    /// <summary>
    ///     Called by <see cref="A2AClient" /> for every agent message it receives, before the caller sees it: a direct reply,
    ///     the
    ///     message of a status update, and the latest status message of a task snapshot (SendMessage, SendStreamingMessage,
    ///     SubscribeToTask, ObserveTaskAsync, GetTask and CancelTask; unary and streamed; every binding, gRPC included).
    ///     Throw to abort the call: the exception reaches the caller, and a stream stops. Default null.
    /// </summary>
    /// <remarks>
    ///     This is the place to check each turn of a multi-turn exchange against the user's original intent, and to require
    ///     human approval before the agent's request leads to a sensitive action. A peer agent can steer a long conversation
    ///     one
    ///     small step at a time ("agent session smuggling"); a check on every message catches drift that a check on the first
    ///     one misses. The same snapshot message can be seen more than once (for example when polling with GetTask, or after a
    ///     stream reconnects), so make the check idempotent.
    /// </remarks>
    public Func<ReceivedMessage, CancellationToken, ValueTask>? OnMessageReceived { get; set; }

    /// <summary>
    ///     When true, a client made by <see cref="A2AClientFactory" /> from an Agent Card falls back to the next interface of
    ///     the card (spec §5.2) when a call fails before any request reached the agent: the connection, name lookup, proxy
    ///     tunnel or TLS handshake failed, after the transport's own retries (<see cref="MaxRetries" />). The candidates are
    ///     the interfaces <see cref="A2AClientFactory.SelectInterface" /> would accept, in the card's order, with the tenant
    ///     of the selected one. A call that works stays on its interface for later calls. An HTTP status, an A2A error, a
    ///     timeout or a failure during the request never falls back, nor does a stream that already delivered an event.
    ///     Default false: the client uses only the selected interface.
    /// </summary>
    /// <remarks>
    ///     A client with credential providers falls back only to interfaces on the card's origin: credentials are never
    ///     sent elsewhere. Every candidate passes the same <see cref="EndpointPolicy" /> check as the selected interface.
    ///     Each fallback counts <c>a2a.client.interface.fallback</c> on the <c>A2A</c> meter. On netstandard2.0 the client
    ///     cannot tell that a request never left, so it never falls back.
    /// </remarks>
    public bool InterfaceFallback { get; set; }

    /// <summary>
    ///     Decides which endpoints an Agent Card may name. <see cref="A2AClientFactory" /> checks every interface of the card
    ///     that this
    ///     client could select, before it builds a client; a refusal throws <see cref="A2AErrorCode.InvalidAgentResponse" />
    ///     and counts
    ///     <c>a2a.client.endpoint.refused</c>. Null or <see cref="AgentEndpointPolicies.AllowAll" /> turns the check off (no
    ///     DNS lookup).
    ///     Default on .NET 10: <see cref="AgentEndpointPolicies.MatchCardNetwork" />. On netstandard2.0 the default is null,
    ///     and
    ///     the presets do not exist (they need A2A.Security.Http), the same as the <c>jku</c> limit of
    ///     <see cref="A2ACardResolver" />.
    /// </summary>
    /// <remarks>
    ///     The check is pre-flight only. A host can resolve to another address when the client connects (DNS rebinding); to
    ///     close that,
    ///     give the client an <see cref="HttpClient" /> built on <c>SafeHttp.CreateHandler</c>.
    /// </remarks>
#if NETSTANDARD2_0
    public Func<AgentEndpointCheck, bool>? EndpointPolicy { get; set; }
#else
    public Func<AgentEndpointCheck, bool>? EndpointPolicy { get; set; } = AgentEndpointPolicies.MatchCardNetwork;
#endif

    /// <summary>
    ///     A copy of these options. <see cref="Extensions" />, <see cref="SupportedBindings" /> and
    ///     <see cref="TransportFactories" /> are
    ///     new lists; other values are shared. Every factory copies options through this method, so a new option must be added
    ///     here.
    /// </summary>
    public A2AClientOptions Clone()
    {
        return new A2AClientOptions
        {
            ProtocolVersion = ProtocolVersion,
            RequireHttpsWebhooks = RequireHttpsWebhooks,
            Extensions = new List<string>(Extensions),
            Tenant = Tenant,
            SupportedBindings = new List<string>(SupportedBindings),
            TransportFactories = new List<IA2ATransportFactory>(TransportFactories),
            RestSubscribeMethod = RestSubscribeMethod,
            ValidateCapabilities = ValidateCapabilities,
            ValidateResponses = ValidateResponses,
            MaxRetries = MaxRetries,
            TimeProvider = TimeProvider,
            CircuitBreaker = CircuitBreaker,
            WaitForTaskCompletion = WaitForTaskCompletion,
            PropagateTraceInMetadata = PropagateTraceInMetadata,
            OnMessageReceived = OnMessageReceived,
            EndpointPolicy = EndpointPolicy,
            InterfaceFallback = InterfaceFallback
        };
    }
}