using A2A.Server.Storage;

namespace A2A.Server.Push;

/// <summary>A push delivery waiting in the outbox, or a dead letter.</summary>
/// <param name="Delivery">The delivery: task, push config, event sequence and delivery id.</param>
/// <param name="Attempt">
/// Attempts made so far. 0 for a new or re-driven delivery. For a dead letter, every attempt including the last one.
/// </param>
/// <param name="DueAt">When the next attempt may start. For a dead letter, when it was dead-lettered.</param>
/// <param name="LastError">Why the last attempt failed, or null. Never holds secrets.</param>
public sealed record OutboxEntry(PushDelivery Delivery, int Attempt, DateTimeOffset DueAt, string? LastError);

/// <summary>
/// Durable queue of push deliveries (spec §4.3.3). Entries are written by <see cref="TaskStore.CommitAsync"/> in the same
/// atomic unit as the event they announce (<see cref="TaskCommit.Deliveries"/>, due at once, attempt 0), and processed by
/// <see cref="PushDeliveryService"/>. An entry is identified by its task, config id and sequence.
/// </summary>
/// <remarks>
/// Order: per task and push config, only the oldest pending entry (lowest sequence) is ever leased, so a delivery is not sent
/// until the one before it for the same task and config succeeded or was dead-lettered. Leases make delivery safe across
/// instances: a leased entry is invisible to <see cref="LeaseDueAsync"/> until its lease ends, so an instance that dies
/// mid-send only delays the delivery (at least once). Operations on an entry that is not there do nothing.
/// </remarks>
public abstract class PushOutbox
{
    private readonly SemaphoreSlim _pending = new(0, 1);

    /// <summary>
    /// Leases up to <paramref name="max"/> due entries for <paramref name="lease"/>: those whose due time has come, that are not
    /// leased, and that are the oldest pending entry of their task and config. Two callers never get the same entry while its
    /// lease runs.
    /// </summary>
    public abstract ValueTask<IReadOnlyList<OutboxEntry>> LeaseDueAsync(int max, TimeSpan lease, CancellationToken ct = default);

    /// <summary>Removes a delivered (or dropped) entry, which releases the next one of its task and config.</summary>
    public abstract ValueTask CompleteAsync(PushDelivery d, CancellationToken ct = default);

    /// <summary>Records a failed attempt: sets the attempt count, the next due time and the error, and ends the lease.</summary>
    public abstract ValueTask RescheduleAsync(PushDelivery d, int attempt, DateTimeOffset dueAt, string error, CancellationToken ct = default);

    /// <summary>
    /// Moves an entry to the dead letters after its last attempt (counted in <see cref="OutboxEntry.Attempt"/>) failed with
    /// <paramref name="error"/>. This releases the next entry of its task and config.
    /// </summary>
    public abstract ValueTask DeadLetterAsync(PushDelivery d, string error, CancellationToken ct = default);

    /// <summary>The dead letters of <paramref name="tenant"/>, oldest first, at most <paramref name="max"/>.</summary>
    public abstract ValueTask<IReadOnlyList<OutboxEntry>> ListDeadLettersAsync(string tenant, int max, CancellationToken ct = default);

    /// <summary>
    /// Moves a dead letter back to the outbox, due at once with attempt 0 and the same delivery id. Returns false when it is
    /// not a dead letter.
    /// </summary>
    public abstract ValueTask<bool> RedriveAsync(PushDelivery d, CancellationToken ct = default);

    /// <summary>Removes every entry and dead letter of <paramref name="task"/> (for example when the task is deleted).</summary>
    public abstract ValueTask DeleteAllAsync(TaskKey task, CancellationToken ct = default);

    /// <summary>Wakes this process's <see cref="PushDeliveryService"/>, so new deliveries do not wait for its next poll.</summary>
    internal void NotifyPending()
    {
        if (_pending.CurrentCount > 0) return;
        try { _pending.Release(); }
        catch (SemaphoreFullException) { } // lost a race with another notifier: already signalled
    }

    /// <summary>Completes after the next <see cref="NotifyPending"/> (or at once if one is outstanding).</summary>
    internal Task WaitForPendingAsync(CancellationToken ct) => _pending.WaitAsync(ct);
}
