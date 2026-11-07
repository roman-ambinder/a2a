using A2A.Server.Push;

namespace A2A.Server;

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

    /// <summary>
    /// Rate limit callers per tenant and principal (ASP.NET Core layer, policy <c>"a2a"</c>). Default true. When true the host
    /// must call <c>app.UseA2A()</c> (it adds the rate limiter middleware) or startup fails. Set false to turn rate limiting off.
    /// </summary>
    public bool EnableRateLimiting { get; set; } = true;
}
