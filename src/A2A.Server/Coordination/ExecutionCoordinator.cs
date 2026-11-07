using A2A.Server.Storage;

namespace A2A.Server.Coordination;

/// <summary>
/// Tracks which executions of the agent run, across every server instance that shares the coordinator, and carries cancel
/// requests to the instance that runs an execution.
/// </summary>
/// <remarks>
/// <para>Each execution (one incoming message, or one CancelTask) registers a lease under a new execution id, renews it with
/// <see cref="HeartbeatAsync"/> while it runs, and unregisters it when it ends. Every lease operation is fenced by the task
/// and the execution id: an operation with another execution's id never touches that execution's lease.</para>
/// <para>A lease that is not renewed within <see cref="CoordinationOptions.LeaseTtl"/> expires: its instance stopped. An
/// expired lease no longer counts as running, is never brought back by a late heartbeat, and is reported by
/// <see cref="ListExpiredAsync"/> until it is unregistered. Expiry is measured on the store's clock, never on the instance's,
/// so instances with skewed clocks agree.</para>
/// <para>The default <see cref="InMemoryExecutionCoordinator"/> is process-local. A2A.Server.Redis provides one that several
/// instances share.</para>
/// </remarks>
public abstract class ExecutionCoordinator
{
    /// <summary>Starts the lease of a new execution of <paramref name="task"/>, valid for <see cref="CoordinationOptions.LeaseTtl"/>.</summary>
    public abstract ValueTask RegisterAsync(TaskKey task, string executionId, CancellationToken ct = default);

    /// <summary>
    /// Renews the lease of the execution for another <see cref="CoordinationOptions.LeaseTtl"/> from now. Does nothing when
    /// the lease is missing or already expired.
    /// </summary>
    public abstract ValueTask HeartbeatAsync(TaskKey task, string executionId, CancellationToken ct = default);

    /// <summary>Ends the lease of the execution (expired or not). Does nothing when it is missing.</summary>
    public abstract ValueTask UnregisterAsync(TaskKey task, string executionId, CancellationToken ct = default);

    /// <summary>True when any execution of <paramref name="task"/> holds a lease that has not expired.</summary>
    public abstract ValueTask<bool> IsRunningAsync(TaskKey task, CancellationToken ct = default);

    /// <summary>
    /// Asks every other instance to cancel its executions of <paramref name="task"/>; the sender cancels its own directly.
    /// Instances read the request from <see cref="CancelRequestsAsync"/>. Fire and forget: delivery is at most once, to the
    /// readers listening now.
    /// </summary>
    public abstract ValueTask RequestCancelAsync(TaskKey task, CancellationToken ct = default);

    /// <summary>
    /// The cancel requests of the other instances (never this instance's own), from the moment the enumeration starts until
    /// <paramref name="ct"/> fires. Every reader gets every request. The reader is registered on the first
    /// <c>MoveNextAsync</c> call, before it first waits.
    /// </summary>
    public abstract IAsyncEnumerable<TaskKey> CancelRequestsAsync(CancellationToken ct);

    /// <summary>
    /// Up to <paramref name="max"/> leases that expired (by the store's clock) and were not unregistered, the longest expired
    /// first. Their executions stopped without ending: their instance died.
    /// </summary>
    public abstract ValueTask<IReadOnlyList<(TaskKey Task, string ExecutionId)>> ListExpiredAsync(int max, CancellationToken ct = default);

    /// <summary>
    /// Takes the named cluster-wide lease for <paramref name="duration"/>, so only one instance runs a periodic job (such as
    /// the retention sweep) per period. True when the lease was free or had expired; false while it is held, also by the
    /// caller. Not released early: a job takes it once per period, for a little less than the period.
    /// </summary>
    public abstract ValueTask<bool> TryAcquireSweepLeaseAsync(string name, TimeSpan duration, CancellationToken ct = default);
}
