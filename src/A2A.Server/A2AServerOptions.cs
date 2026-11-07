using A2A.Server.Idempotency;
using A2A.Server.Push;

namespace A2A.Server;

/// <summary>Settings of the A2A server. Configure them with <c>AddA2AServer(options =&gt; ...)</c>.</summary>
public sealed class A2AServerOptions
{
    /// <summary>The public Agent Card. Required.</summary>
    public AgentCard AgentCard { get; set; } = new()
    {
        Name = string.Empty, Description = string.Empty, SupportedInterfaces = [], Version = string.Empty,
        Capabilities = new(), DefaultInputModes = [], DefaultOutputModes = [], Skills = [],
    };

    /// <summary>Accepted protocol versions (Major.Minor). Default {"1.0"}.</summary>
    public ISet<string> SupportedVersions { get; } = new HashSet<string>(StringComparer.Ordinal) { A2AProtocol.Version };

    /// <summary>
    /// The spec says a missing A2A-Version header means 0.3 (§3.6.2), which this server rejects unless listed in
    /// <see cref="SupportedVersions"/>. Set true to treat a missing header as <see cref="A2AProtocol.Version"/> instead
    /// (handy for curl and older tooling, but not strictly compliant). Default false.
    /// </summary>
    public bool TreatMissingVersionAsCurrent { get; set; }

    /// <summary>
    /// Allow callers that are not authenticated. Default false: at startup the ASP.NET Core layer fails unless every A2A
    /// endpoint has an authorization policy (<c>MapA2A(...).RequireAuthorization()</c>). Set true to opt in to anonymous
    /// access. All anonymous callers then share one owner (<see cref="ServerCallContext.AnonymousPrincipal"/>) and can
    /// read each other's tasks. A warning is logged once at startup.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>
    /// Returns the extended card for an authenticated caller (spec §3.1.11). Requires
    /// <c>AgentCard.Capabilities.ExtendedAgentCard = true</c>. Return null when no card is configured.
    /// </summary>
    public Func<ServerCallContext, CancellationToken, ValueTask<AgentCard?>>? ExtendedAgentCardProvider { get; set; }

    /// <summary>Webhook delivery and URL policy for push notifications.</summary>
    public PushNotificationOptions PushNotifications { get; } = new();

    /// <summary>How long CancelTask waits for a running execution to stop. Default 5 seconds.</summary>
    public TimeSpan CancelGracePeriod { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Append the exception type and message to the text of unexpected (non-A2A) errors sent to callers.
    /// Null (default) means true only when the host environment is Development. The ASP.NET Core layer resolves the null.
    /// </summary>
    public bool? IncludeExceptionDetails { get; set; }

    /// <summary>Which task states accept follow-up messages. Default <see cref="Server.FollowUpPolicy.InterruptedOnly"/>.</summary>
    public FollowUpPolicy FollowUpPolicy { get; set; } = FollowUpPolicy.InterruptedOnly;

    /// <summary>How to treat an unknown client-supplied contextId on a new task. Default <see cref="Server.ClientContextIdPolicy.Reject"/>.</summary>
    public ClientContextIdPolicy ClientContextIdPolicy { get; set; } = ClientContextIdPolicy.Reject;

    /// <summary>Size, count and time limits (request body, parts, JSON depth, history, page size, streams, push configs).</summary>
    public A2ALimits Limits { get; } = new();

    /// <summary>Event log retention and SSE keep-alive settings.</summary>
    public StreamingOptions Streaming { get; } = new();

    /// <summary>
    /// Idempotent SendMessage (retention and size of the in-memory store). A retried SendMessage with the same
    /// <c>Idempotency-Key</c> header, or without one the same message id, from the same tenant and principal is answered with
    /// the first request's task or reply instead of running again.
    /// </summary>
    public IdempotencyOptions Idempotency { get; } = new();

    /// <summary>
    /// Rate limit callers per tenant and principal (ASP.NET Core layer, policy <c>"a2a"</c>). Default true. When true the host
    /// must call <c>app.UseA2A()</c> (it adds the rate limiter middleware) or startup fails. Set false to turn rate limiting off.
    /// </summary>
    public bool EnableRateLimiting { get; set; } = true;
}

/// <summary>Settings for the per-task event log and for SSE streams.</summary>
public sealed class StreamingOptions
{
    /// <summary>Events kept per task for replay. Default 1000.</summary>
    public int ReplayBufferSize { get; set; } = 1000;

    /// <summary>How long a terminal task's events are kept after it finishes. Default 10 minutes.</summary>
    public TimeSpan TerminalRetention { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Interval for SSE keep-alive comments while a stream is idle. Default 15 seconds. Zero disables.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Capacity of each per-task bounded event channel, such as the push delivery queue of a task. Default 256. When a channel
    /// is full, the producer is not blocked and no event is dropped: later events wait, in order, until the consumer catches up.
    /// </summary>
    public int ChannelCapacity { get; set; } = 256;
}
