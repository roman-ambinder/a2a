namespace A2A.Client.Push;

/// <summary>What <see cref="IPushDeliveryStore.TryBeginAsync" /> found for a delivery id.</summary>
public enum PushDeliveryClaim
{
    /// <summary>
    ///     The id is new. The caller now owns it and must call <see cref="IPushDeliveryStore.CompleteAsync" /> or
    ///     <see cref="IPushDeliveryStore.ReleaseAsync" />.
    /// </summary>
    Claimed,

    /// <summary>Another call holds the id and has not finished (and its lease has not run out).</summary>
    InProgress,

    /// <summary>The id was handled before and is still remembered.</summary>
    AlreadyHandled
}

/// <summary>
///     Remembers which webhook deliveries the application has handled, for
///     <see cref="PushNotificationReceiver.HandleAsync" />.
///     Implement it over a database or cache so the list survives a restart and is shared by several receiver instances.
///     An id goes <c>Claimed</c> (in progress, with a lease), then <c>Completed</c> (remembered until a time) or
///     <c>Released</c> (forgotten).
///     Implementations must be thread-safe, and <see cref="TryBeginAsync" /> must be atomic.
/// </summary>
/// <remarks>
///     Losing the list is safe: the sender may repeat a delivery, and a receiver that is idempotent handles it again.
/// </remarks>
public interface IPushDeliveryStore
{
    /// <summary>
    ///     Atomically claims <paramref name="id" />. Returns <see cref="PushDeliveryClaim.AlreadyHandled" /> when it was
    ///     completed and
    ///     not yet forgotten, <see cref="PushDeliveryClaim.InProgress" /> when it is claimed and the lease of that claim has
    ///     not
    ///     passed at <paramref name="now" />, and otherwise claims it until <paramref name="leaseUntil" />.
    /// </summary>
    Task<PushDeliveryClaim> TryBeginAsync(string id, DateTimeOffset now, DateTimeOffset leaseUntil,
        CancellationToken cancellationToken);

    /// <summary>Marks <paramref name="id" /> as handled, remembered until <paramref name="keepUntil" />.</summary>
    Task CompleteAsync(string id, DateTimeOffset keepUntil, CancellationToken cancellationToken);

    /// <summary>Forgets <paramref name="id" /> (the handler failed), so a retry of the delivery reaches the handler again.</summary>
    Task ReleaseAsync(string id, CancellationToken cancellationToken);
}

/// <summary>
///     The default <see cref="IPushDeliveryStore" />: ids are kept in memory. After a restart the list is empty, so a
///     delivery
///     that was already handled can arrive again. That is safe when the handler is idempotent. Thread-safe.
/// </summary>
public sealed class InMemoryPushDeliveryStore : IPushDeliveryStore
{
    private readonly Dictionary<string, (bool Done, DateTimeOffset Until)> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <inheritdoc />
    public Task<PushDeliveryClaim> TryBeginAsync(string id, DateTimeOffset now, DateTimeOffset leaseUntil,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            List<string>? expired = null;
            foreach (var (key, entry) in _entries)
                if (now >= entry.Until)
                    (expired ??= []).Add(key);
            if (expired is not null)
                foreach (var key in expired)
                    _entries.Remove(key);

            if (_entries.TryGetValue(id, out var current))
                return Task.FromResult(current.Done ? PushDeliveryClaim.AlreadyHandled : PushDeliveryClaim.InProgress);
            _entries[id] = (false, leaseUntil);
            return Task.FromResult(PushDeliveryClaim.Claimed);
        }
    }

    /// <inheritdoc />
    public Task CompleteAsync(string id, DateTimeOffset keepUntil, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            _entries[id] = (true, keepUntil);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReleaseAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            _entries.Remove(id);
        }

        return Task.CompletedTask;
    }
}