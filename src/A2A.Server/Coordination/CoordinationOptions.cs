namespace A2A.Server.Coordination;

/// <summary>
///     Lease and polling settings of the <see cref="ExecutionCoordinator" /> (<see cref="A2AServerOptions.Coordination" />
///     ).
///     Every execution of the agent holds a lease while it runs and renews it every <see cref="Heartbeat" />; a lease not
///     renewed
///     for <see cref="LeaseTtl" /> belongs to an execution whose instance stopped.
/// </summary>
public sealed class CoordinationOptions
{
    /// <summary>
    ///     How long an execution's lease lasts without a heartbeat. An instance that dies mid-execution is noticed this long
    ///     after
    ///     its last heartbeat. Must be longer than 2 × <see cref="Heartbeat" />, so one missed heartbeat does not expire a
    ///     live
    ///     execution. An execution whose heartbeats lapse past it (for example a long pause, or a coordinator outage) is
    ///     treated as
    ///     dead: the retention sweep marks its task FAILED, and its later events are dropped. Default 30 seconds.
    /// </summary>
    public TimeSpan LeaseTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     How often a running execution renews its lease (and the idempotency claim of the request that started it, if it
    ///     still
    ///     holds one). Default 10 seconds.
    /// </summary>
    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     How often a request that waits for a task to settle (a blocking SendMessage, a retried SendMessage, CancelTask)
    ///     checks
    ///     whether an execution on another instance still runs it. Executions on the same instance are noticed at once.
    ///     Default 1 second.
    /// </summary>
    public TimeSpan RunningPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     How often an idle SubscribeToTask stream (fresh or resumed) looks at the event store.
    ///     Normally every event reaches a stream over the bus. A lost bus message, or an instance that died right after it
    ///     committed
    ///     an event, would leave the stream silent until <see cref="A2ALimits.MaxStreamDuration" />. At each check the stream
    ///     sends
    ///     the events it missed. It also ends cleanly, so the client resumes with <c>Last-Event-ID</c>, when nothing runs the
    ///     task for
    ///     two checks in a row. A stream that received an event since the last check does not look. The cost is one small set
    ///     of
    ///     store reads (the last sequence, the task, and the running check) per idle stream per interval. Default 30 seconds.
    /// </summary>
    public TimeSpan StreamCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Throws <see cref="InvalidOperationException" /> unless <see cref="Heartbeat" />, <see cref="RunningPollInterval" />
    ///     and
    ///     <see cref="StreamCheckInterval" /> are positive and finite and <see cref="LeaseTtl" /> is longer than 2 ×
    ///     <see cref="Heartbeat" />.
    /// </summary>
    public void Validate()
    {
        if (Heartbeat <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"Coordination.Heartbeat ({Heartbeat}) must be a positive, finite time span.");
        if (RunningPollInterval <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"Coordination.RunningPollInterval ({RunningPollInterval}) must be a positive, finite time span.");
        if (StreamCheckInterval <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"Coordination.StreamCheckInterval ({StreamCheckInterval}) must be a positive, finite time span.");
        // In 128-bit ticks, so a huge heartbeat cannot overflow.
        if (LeaseTtl.Ticks <= 2 * (Int128)Heartbeat.Ticks)
            throw new InvalidOperationException(
                $"Coordination.LeaseTtl ({LeaseTtl}) must be longer than 2 x Coordination.Heartbeat ({Heartbeat}), " +
                "so one missed heartbeat does not make a running execution look dead.");
    }
}