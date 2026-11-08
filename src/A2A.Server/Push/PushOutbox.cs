using A2A.Server.Storage;

namespace A2A.Server.Push;

/// <summary>A push delivery waiting in the outbox, or a dead letter.</summary>
/// <param name="Delivery">The delivery: task, push config, event sequence and delivery id.</param>
/// <param name="Attempt">
/// Attempts made so far. 0 for a new or re-driven delivery. For a dead letter, every attempt including the last one.
/// </param>
/// <param name="DueAt">When the next attempt may start. For a dead letter, when it was dead-lettered.</param>
/// <param name="LastError">Why the last attempt failed, or null. Never holds secrets.</param>
public sealed record OutboxEntry(PushDelivery Delivery, int Attempt, DateTimeOffset DueAt, string? LastError)
{
    /// <summary>
    /// The webhook body: the event's <see cref="StreamResponse"/> JSON (UTF-8), captured by the commit. Kept with the entry and
    /// its dead letter, so a delivery does not depend on the event log's retention and can be re-driven at any time.
    /// </summary>
    public ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>
    /// The lease this entry was returned under by <see cref="PushOutbox.LeaseDueAsync"/>, or null. Result writes
    /// (<see cref="PushOutbox.CompleteAsync"/>, <see cref="PushOutbox.RescheduleAsync"/>, <see cref="PushOutbox.DeadLetterAsync"/>)
    /// apply only while the entry still holds this lease, so a holder whose lease ran out and was taken over cannot change it.
    /// </summary>
    public Guid? LeaseToken { get; init; }

    /// <summary>
    /// How long ago the entry was queued, by the outbox's own clock (the database clock for PostgreSQL), as of the call that
    /// returned it. Zero when not known. Only <see cref="PushOutbox.LeaseDueAsync"/> sets it; <see cref="PushDeliveryOptions.MaxDeliveryAge"/>
    /// is checked against it.
    /// </summary>
    public TimeSpan Age { get; init; }
}

/// <summary>
/// Durable queue of push deliveries (spec §4.3.3). Entries are written by <see cref="TaskStore.CommitAsync"/> in the same
/// atomic unit as the event they announce (<see cref="TaskCommit.Deliveries"/>, due at once, attempt 0, with the event's
/// JSON as <see cref="OutboxEntry.Payload"/>), and processed by <see cref="PushDeliveryService"/>. An entry is identified
/// by its task, config id and sequence.
/// </summary>
/// <remarks>
/// Order: per task and push config, only the oldest pending entry (lowest sequence) is ever leased, so a delivery is not sent
/// until the one before it for the same task and config succeeded or was dead-lettered. Leases make delivery safe across
/// instances: a leased entry is invisible to <see cref="LeaseDueAsync"/> until its lease ends, so an instance that dies
/// mid-send only delays the delivery (at least once). Result writes are fenced by <see cref="OutboxEntry.LeaseToken"/>: they
/// take the leased entry and return false (writing nothing) when it is gone or was leased again by someone else.
/// New entries never replace an existing entry with the same task, config and sequence.
/// </remarks>
public abstract class PushOutbox
{
    // Set by NotifyPending, swapped for a new one by the waiter that consumes it. Not a SemaphoreSlim: a cancelled
    // SemaphoreSlim wait leaves its waiter queued for a moment, and a Release in that window is lost.
    private TaskCompletionSource _pending = NewSignal();

    /// <summary>
    /// Leases up to <paramref name="max"/> due entries for <paramref name="lease"/>: those whose due time has come, that are not
    /// leased, and that are the oldest pending entry of their task and config. Two callers never get the same entry while its
    /// lease runs. Each returned entry carries a new <see cref="OutboxEntry.LeaseToken"/>.
    /// </summary>
    public abstract ValueTask<IReadOnlyList<OutboxEntry>> LeaseDueAsync(int max, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a delivered (or dropped) leased entry, which releases the next one of its task and config. Returns false, and
    /// changes nothing, when the entry no longer holds <paramref name="leased"/>'s lease.
    /// </summary>
    public abstract ValueTask<bool> CompleteAsync(OutboxEntry leased, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed attempt of a leased entry: sets the attempt count, the next due time and the error, and ends the lease.
    /// Returns false, and changes nothing, when the entry no longer holds <paramref name="leased"/>'s lease.
    /// </summary>
    public abstract ValueTask<bool> RescheduleAsync(OutboxEntry leased, int attempt, DateTimeOffset dueAt, string error, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a leased entry to the dead letters after its last attempt failed with <paramref name="error"/> (the dead letter's
    /// <see cref="OutboxEntry.Attempt"/> counts that attempt). This releases the next entry of its task and config. Returns
    /// false, and changes nothing, when the entry no longer holds <paramref name="leased"/>'s lease.
    /// </summary>
    public abstract ValueTask<bool> DeadLetterAsync(OutboxEntry leased, string error, CancellationToken cancellationToken = default);

    /// <summary>The dead letters of <paramref name="tenant"/>, oldest first, at most <paramref name="max"/>.</summary>
    public abstract ValueTask<IReadOnlyList<OutboxEntry>> ListDeadLettersAsync(string tenant, int max, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a dead letter back to the outbox, due at once with attempt 0, the same delivery id and the same payload. Returns
    /// false when it is not a dead letter.
    /// </summary>
    public abstract ValueTask<bool> RedriveAsync(PushDelivery d, CancellationToken cancellationToken = default);

    /// <summary>Removes every entry and dead letter of <paramref name="task"/> (for example when the task is deleted).</summary>
    public abstract ValueTask DeleteAllAsync(TaskKey task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues one delivery outside a commit (a catch-up: a webhook registered after the event it should see), due at once,
    /// attempt 0, with <paramref name="payload"/> as its webhook body. Idempotent on task, config id and sequence: returns
    /// false, and changes nothing, when a pending entry or a dead letter has that key already.
    /// </summary>
    public abstract ValueTask<bool> EnqueueAsync(PushDelivery delivery, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>Wakes this process's <see cref="PushDeliveryService"/>, so new deliveries do not wait for its next poll.</summary>
    internal void NotifyPending() => Volatile.Read(ref _pending).TrySetResult();

    /// <summary>
    /// Completes after the next <see cref="NotifyPending"/> (or at once if one is outstanding), and consumes that signal.
    /// A wait cancelled by <paramref name="cancellationToken"/> consumes nothing, so a signal sent meanwhile wakes the next wait.
    /// </summary>
    internal async Task WaitForPendingAsync(CancellationToken cancellationToken)
    {
        var signal = Volatile.Read(ref _pending);
        await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        // Consume it. A NotifyPending that still completes the old signal comes before the caller's next pass, which sees its work.
        Interlocked.CompareExchange(ref _pending, NewSignal(), signal);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
