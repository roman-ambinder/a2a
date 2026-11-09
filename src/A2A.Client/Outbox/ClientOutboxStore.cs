namespace A2A.Client.Outbox;

/// <summary>
/// Durable list of the messages an agent sends to peers: queued, sent and tracked, final result stored, result collected.
/// A <see cref="ClientOutboxDispatcher"/> moves entries along; the app enqueues messages and collects results.
/// Implementations must be thread-safe, and must work when several dispatchers share one store.
/// </summary>
/// <remarks>
/// Methods take spans of time, not absolute times, so each store uses its own clock (the database clock for PostgreSQL).
/// Every write that follows a lease (<see cref="RescheduleAsync"/>, <see cref="MarkTrackingAsync"/>, <see cref="FinishAsync"/>,
/// <see cref="AcknowledgeAsync"/>) is fenced by <see cref="ClientOutboxEntry.LeaseToken"/>: it changes nothing and returns false
/// when the entry is gone or was leased again by someone else. A holder that crashes only delays its entry: the lease runs out.
/// This is an abstract class so members can be added later without breaking stores.
/// </remarks>
public abstract class ClientOutboxStore
{
    /// <summary>Adds the message as a queued entry, due now. Returns false, and keeps the old entry, when the id exists (also when that entry is already collected and not yet purged).</summary>
    public abstract ValueTask<bool> EnqueueAsync(ClientOutboxMessage message, CancellationToken cancellationToken = default);

    /// <summary>Returns the entry, or null when there is none.</summary>
    public abstract ValueTask<ClientOutboxEntry?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Leases up to <paramref name="max"/> entries that are <see cref="ClientOutboxStatus.Queued"/> or <see cref="ClientOutboxStatus.Tracking"/>,
    /// due, and not leased. Two callers never get the same entry while its lease runs. Each entry's <see cref="ClientOutboxEntry.Attempts"/>
    /// goes up by one. The returned <see cref="ClientOutboxEntry.InDoubt"/> is the value from before this lease; the store then sets it
    /// to true for a queued entry, because a send is about to start.
    /// </summary>
    public abstract ValueTask<IReadOnlyList<ClientOutboxEntry>> LeaseDueAsync(int max, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>Tries again after <paramref name="delay"/>. Stores <paramref name="error"/> (may be null) and <paramref name="inDoubt"/>. Clears the lease.</summary>
    public abstract ValueTask<bool> RescheduleAsync(ClientOutboxEntry leased, TimeSpan delay, string? error, bool inDoubt, CancellationToken cancellationToken = default);

    /// <summary>The peer took the message. The entry becomes <see cref="ClientOutboxStatus.Tracking"/> with the peer's ids, <see cref="ClientOutboxEntry.Attempts"/> 0 and not in doubt, due after <paramref name="delay"/>. Clears the lease.</summary>
    public abstract ValueTask<bool> MarkTrackingAsync(ClientOutboxEntry leased, string taskId, string? contextId, TaskState state, TimeSpan delay, CancellationToken cancellationToken = default);

    /// <summary>Ends the entry as <see cref="ClientOutboxOutcome.Status"/> says and stores the outcome. Remote ids the outcome does not carry are kept. Clears the lease.</summary>
    public abstract ValueTask<bool> FinishAsync(ClientOutboxEntry leased, ClientOutboxOutcome outcome, CancellationToken cancellationToken = default);

    /// <summary>
    /// Leases up to <paramref name="max"/> results to read: entries that are <see cref="ClientOutboxStatus.Finished"/> or
    /// <see cref="ClientOutboxStatus.Failed"/>, not collected, and not leased, oldest first. With <paramref name="correlation"/>, only entries with that label.
    /// </summary>
    public abstract ValueTask<IReadOnlyList<ClientOutboxEntry>> LeaseResultsAsync(string? correlation, int max, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>Marks a leased result as collected (<see cref="ClientOutboxEntry.CollectedAt"/>) and clears the lease. A result that is not acknowledged is returned again after its lease.</summary>
    public abstract ValueTask<bool> AcknowledgeAsync(ClientOutboxEntry leased, CancellationToken cancellationToken = default);

    /// <summary>Makes the <see cref="ClientOutboxStatus.Tracking"/> entry of the peer's task due now. Returns false when there is none. The task id is only unique per peer, so both are needed.</summary>
    public abstract ValueTask<bool> HastenAsync(string peer, string taskId, CancellationToken cancellationToken = default);

    /// <summary>Deletes up to <paramref name="max"/> entries that were collected longer than <paramref name="olderThan"/> ago, oldest first. Returns how many. After that their ids can be enqueued again.</summary>
    public abstract ValueTask<int> PurgeCollectedAsync(TimeSpan olderThan, int max, CancellationToken cancellationToken = default);
}
