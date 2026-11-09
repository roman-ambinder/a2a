namespace A2A.Server.Audit;

/// <summary>
/// Receives the audit events. An abstract class with one method, like the stores, so members can be added later without
/// breaking sinks (see ADR-001). Register yours with <see cref="A2AServerBuilder.AddAuditSink{T}"/>. The default sink is
/// <see cref="LoggerAuditSink"/>.
/// </summary>
/// <remarks>
/// The server waits for the write, at most <see cref="A2AAuditOptions.WriteTimeout"/>. Honor the token. A sink that throws or
/// times out is logged, and the request goes on unless <see cref="A2AAuditOptions.FailClosed"/> is set and the write was the
/// <see cref="A2AAuditOutcome.Attempted"/> row. One instance serves every call: it must be safe to call from many threads.
/// Writing the same <see cref="A2AAuditEvent.Id"/> and outcome twice must do no harm.
/// </remarks>
public abstract class A2AAuditSink
{
    /// <summary>Stores <paramref name="auditEvent"/>. Append only: never change or delete an earlier row.</summary>
    /// <param name="auditEvent">The event. Its strings are already cleaned.</param>
    /// <param name="cancellationToken">Cancelled when the write timed out.</param>
    public abstract ValueTask WriteAsync(A2AAuditEvent auditEvent, CancellationToken cancellationToken);
}
