namespace A2A.Server.Audit;

/// <summary>Settings of the audit trail. Set them in <see cref="A2AServerOptions.Audit"/>.</summary>
public sealed class A2AAuditOptions
{
    /// <summary>
    /// When true, no state-changing action happens without a record: the <see cref="A2AAuditOutcome.Attempted"/> row is
    /// written first, and if that write fails or times out the request is refused with <see cref="A2AErrorCode.Internal"/> and
    /// nothing changes. A failure of the final row is only logged. Default false (fail open): a sink error is logged and the
    /// request goes on.
    /// </summary>
    public bool FailClosed { get; set; }

    /// <summary>How long one write may take. Default 2 seconds. Must be positive.</summary>
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// When true, <see cref="LoggerAuditSink"/> writes the actor and the caller's address in clear. Default false: the log line has
    /// the actor's hash (the same hash as the security event log) and no address, because logs are widely read. Other sinks,
    /// such as the Postgres one, always get the real values.
    /// </summary>
    public bool LogIdentity { get; set; }

    /// <summary>Throws <see cref="InvalidOperationException"/> unless <see cref="WriteTimeout"/> is positive.</summary>
    internal void Validate()
    {
        if (WriteTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException($"Audit.WriteTimeout ({WriteTimeout}) must be greater than zero.");
    }
}
