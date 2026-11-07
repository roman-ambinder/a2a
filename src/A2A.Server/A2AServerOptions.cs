using A2A.Server.Push;

namespace A2A.Server;

public sealed class A2AServerOptions
{
    /// <summary>The public Agent Card. Required.</summary>
    public AgentCard AgentCard { get; set; } = new();

    /// <summary>Accepted protocol versions (Major.Minor). Default {"1.0"}.</summary>
    public ISet<string> SupportedVersions { get; } = new HashSet<string>(StringComparer.Ordinal) { A2AProtocol.Version };

    /// <summary>
    /// The spec says a missing A2A-Version header means 0.3 (§3.6.2), which this server rejects unless listed in
    /// <see cref="SupportedVersions"/>. Set true to treat a missing header as <see cref="A2AProtocol.Version"/> instead
    /// (handy for curl and older tooling, but not strictly compliant). Default false.
    /// </summary>
    public bool TreatMissingVersionAsCurrent { get; set; }

    /// <summary>Reject unauthenticated callers on every operation. Default false (use endpoint authorization instead).</summary>
    public bool RequireAuthenticatedCaller { get; set; }

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

    /// <summary>Upper bound on ListTasks page size. Default 100 (spec).</summary>
    public int MaxPageSize { get; set; } = 100;
}
