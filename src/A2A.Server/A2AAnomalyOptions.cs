namespace A2A.Server;

/// <summary>
///     Monitoring for unusual patterns (spec §13.4): rapid task creation and excessive cancellations by one caller. Set them in
///     <see cref="A2AServerOptions.Anomalies" />. The detector only reports: it writes an <c>anomaly</c> security event and
///     counts <c>a2a.server.anomalies</c>, and never refuses, delays or changes a request (use rate limiting for that). Off by
///     default.
/// </summary>
/// <remarks>
///     A caller is a principal (<see cref="ServerCallContext.Principal" />) in a tenant. Each caller has a one-minute window
///     per kind, which starts at its first counted call; a caller that goes over a threshold is reported once in that window.
///     The window is measured on the <see cref="TimeProvider" /> registered in DI, else <see cref="TimeProvider.System" />.
///     Counts are kept in memory on each instance, so with several instances each one sees only the calls it serves.
/// </remarks>
public sealed class A2AAnomalyOptions
{
    /// <summary>Turns the detector on. Default false: nothing is tracked and nothing is reported.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     New tasks (a SendMessage or SendStreamingMessage that creates a task, not a follow-up or a retry) one caller may
    ///     create in one minute before it is reported. Default 60. Must be at least 1.
    /// </summary>
    public int TaskCreationsPerMinute { get; set; } = 60;

    /// <summary>
    ///     CancelTask calls one caller may make in one minute before it is reported, whether or not the task could be
    ///     canceled. Default 20. Must be at least 1.
    /// </summary>
    public int CancellationsPerMinute { get; set; } = 20;

    /// <summary>
    ///     Most callers tracked at once, which bounds the memory used. Callers whose windows have ended are dropped first;
    ///     when every tracked caller is still active, a new caller is not tracked until one of them ends its window. Default
    ///     10 000. Must be at least 1.
    /// </summary>
    public int MaxTrackedCallers { get; set; } = 10_000;

    /// <summary>Throws <see cref="InvalidOperationException" /> when a setting is out of range.</summary>
    internal void Validate()
    {
        if (TaskCreationsPerMinute < 1)
            throw new InvalidOperationException(
                $"Anomalies.TaskCreationsPerMinute ({TaskCreationsPerMinute}) must be at least 1.");
        if (CancellationsPerMinute < 1)
            throw new InvalidOperationException(
                $"Anomalies.CancellationsPerMinute ({CancellationsPerMinute}) must be at least 1.");
        if (MaxTrackedCallers < 1)
            throw new InvalidOperationException(
                $"Anomalies.MaxTrackedCallers ({MaxTrackedCallers}) must be at least 1.");
    }
}
