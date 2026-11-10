using A2A.Server.Storage;

namespace A2A.Server.Push;

/// <summary>
///     Process-local <see cref="PushOutbox" />. For development and tests; pending deliveries are lost on restart.
///     <see cref="InMemoryTaskStore" /> writes its entries, so give that store this outbox.
/// </summary>
public sealed class InMemoryPushOutbox(TimeProvider? time = null) : PushOutbox, IPushOutboxStatistics
{
    private readonly Dictionary<(TaskKey Task, string ConfigId, long Sequence), OutboxEntry> _dead = new();
    private readonly object _gate = new();
    private readonly Dictionary<(TaskKey Task, string ConfigId), SortedDictionary<long, Entry>> _pending = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Entries not yet delivered or dead-lettered, leased ones included.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _pending.Values.Sum(g => g.Count);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<PushOutboxStatistics> GetStatisticsAsync(int maxDeadLetters,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var age = TimeSpan.Zero;
            if (_pending.Count > 0)
            {
                var oldest = _pending.Values.Min(g => g.Values.Min(e => e.DueAt));
                var waited = _time.GetUtcNow() - oldest;
                if (waited > TimeSpan.Zero) age = waited;
            }

            return ValueTask.FromResult(new PushOutboxStatistics(age,
                Math.Min(_dead.Count, Math.Max(0, maxDeadLetters))));
        }
    }

    /// <summary>
    ///     Adds new deliveries of one event, due now, with its JSON <paramref name="payload" /> (shared, never changed).
    ///     Called by <see cref="InMemoryTaskStore" /> inside its commit; does not fail.
    /// </summary>
    internal void Add(IEnumerable<PushDelivery> deliveries, ReadOnlyMemory<byte> payload)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var d in deliveries)
                Group(d, true)!.TryAdd(d.Sequence, new Entry(d, payload, now)); // never replaces
        }

        NotifyPending();
    }

    /// <inheritdoc />
    public override ValueTask<bool> EnqueueAsync(PushDelivery delivery, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        lock (_gate)
        {
            if (_dead.ContainsKey((delivery.Task, delivery.ConfigId, delivery.Sequence)))
                return ValueTask.FromResult(false);
            if (!Group(delivery, true)!.TryAdd(delivery.Sequence, new Entry(delivery, payload, _time.GetUtcNow())))
                return ValueTask.FromResult(false);
        }

        NotifyPending();
        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<OutboxEntry>> LeaseDueAsync(int max, TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        return Lease(max, lease, null);
    }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<OutboxEntry>> LeaseDueCappedAsync(int max, TimeSpan lease,
        int maxInFlightPerCaller, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInFlightPerCaller, 1);
        return Lease(max, lease, maxInFlightPerCaller);
    }

    private ValueTask<IReadOnlyList<OutboxEntry>> Lease(int max, TimeSpan lease, int? maxInFlightPerCaller)
    {
        if (max <= 0) return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>([]);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var candidates = _pending.Values
                .Select(g => g.Values.First()) // only the oldest entry of a task and config may be in flight
                .Where(e => e.DueAt <= now && (e.LeasedUntil is not { } until || until <= now))
                .OrderBy(e => e.DueAt).ThenBy(e => e.Delivery.Sequence);
            var leased = new List<Entry>();
            if (maxInFlightPerCaller is not { } cap)
            {
                leased.AddRange(candidates.Take(max));
            }
            else
            {
                // The tenant is not part of the count: the cap is per caller.
                var inFlight = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var g in _pending.Values)
                {
                    var e = g.Values.First();
                    if (e.LeasedUntil is { } until && until > now)
                        inFlight[e.Delivery.Task.Owner] = inFlight.GetValueOrDefault(e.Delivery.Task.Owner) + 1;
                }

                foreach (var e in candidates)
                {
                    if (leased.Count >= max) break;
                    var owner = e.Delivery.Task.Owner;
                    var n = inFlight.GetValueOrDefault(owner);
                    if (n >= cap) continue;
                    inFlight[owner] = n + 1;
                    leased.Add(e);
                }
            }

            var token = Guid.NewGuid();
            foreach (var e in leased)
            {
                e.LeasedUntil = now + lease;
                e.LeaseToken = token;
            }

            return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(leased.Select(e => e.ToEntry(now)).ToList());
        }
    }

    /// <inheritdoc />
    public override ValueTask<bool> CompleteAsync(OutboxEntry leased, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(leased);
        lock (_gate)
        {
            return ValueTask.FromResult(Remove(leased) is not null);
        }
    }

    /// <inheritdoc />
    public override ValueTask<bool> RescheduleAsync(OutboxEntry leased, int attempt, DateTimeOffset dueAt, string error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(leased);
        lock (_gate)
        {
            if (Held(leased) is not { } e) return ValueTask.FromResult(false);
            e.Attempt = attempt;
            e.DueAt = dueAt;
            e.LastError = error;
            e.LeasedUntil = null;
            e.LeaseToken = null;
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public override ValueTask<bool> DeadLetterAsync(OutboxEntry leased, string error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(leased);
        lock (_gate)
        {
            if (Remove(leased) is not { } e) return ValueTask.FromResult(false);
            var d = e.Delivery;
            _dead[(d.Task, d.ConfigId, d.Sequence)] = new OutboxEntry(d, e.Attempt + 1, _time.GetUtcNow(), error)
                { Payload = e.Payload };
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<OutboxEntry>> ListDeadLettersAsync(string tenant, int max,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<OutboxEntry> list = _dead.Values
                .Where(e => string.Equals(e.Delivery.Task.Tenant, tenant, StringComparison.Ordinal))
                .OrderBy(e => e.DueAt)
                .ThenBy(e => e.Delivery.Task.Owner, StringComparer.Ordinal)
                .ThenBy(e => e.Delivery.Task.TaskId, StringComparer.Ordinal)
                .ThenBy(e => e.Delivery.ConfigId, StringComparer.Ordinal)
                .ThenBy(e => e.Delivery.Sequence)
                .Take(Math.Max(0, max))
                .ToList();
            return ValueTask.FromResult(list);
        }
    }

    /// <inheritdoc />
    public override ValueTask<bool> RedriveAsync(PushDelivery d, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_dead.TryGetValue((d.Task, d.ConfigId, d.Sequence), out var dead)) return ValueTask.FromResult(false);
            // An entry with this key is pending already: leave it (and its lease) alone, and keep the dead letter.
            if (!Group(d, true)!.TryAdd(d.Sequence, new Entry(dead.Delivery, dead.Payload, _time.GetUtcNow())))
                return ValueTask.FromResult(false);
            _dead.Remove((d.Task, d.ConfigId, d.Sequence));
        }

        NotifyPending();
        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public override ValueTask DeleteAllAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var key in _pending.Keys.Where(k => k.Task == task).ToList()) _pending.Remove(key);
            foreach (var key in _dead.Keys.Where(k => k.Task == task).ToList()) _dead.Remove(key);
        }

        return ValueTask.CompletedTask;
    }

    private SortedDictionary<long, Entry>? Group(PushDelivery d, bool create)
    {
        var key = (d.Task, d.ConfigId);
        if (_pending.TryGetValue(key, out var g)) return g;
        if (!create) return null;
        return _pending[key] = new SortedDictionary<long, Entry>();
    }

    /// <summary>The pending entry of <paramref name="leased" /> if it still holds that lease.</summary>
    private Entry? Held(OutboxEntry leased)
    {
        var d = leased.Delivery;
        return leased.LeaseToken is { } token && Group(d, false) is { } g && g.TryGetValue(d.Sequence, out var e) &&
               e.LeaseToken == token
            ? e
            : null;
    }

    /// <summary>Removes the entry of <paramref name="leased" /> if it still holds that lease.</summary>
    private Entry? Remove(OutboxEntry leased)
    {
        if (Held(leased) is not { } e) return null;
        var d = e.Delivery;
        var g = Group(d, false)!;
        g.Remove(d.Sequence);
        if (g.Count == 0) _pending.Remove((d.Task, d.ConfigId));
        return e;
    }

    private sealed class Entry(PushDelivery delivery, ReadOnlyMemory<byte> payload, DateTimeOffset dueAt)
    {
        public DateTimeOffset QueuedAt { get; } = dueAt;
        public PushDelivery Delivery { get; } = delivery;
        public ReadOnlyMemory<byte> Payload { get; } = payload;
        public int Attempt { get; set; }
        public DateTimeOffset DueAt { get; set; } = dueAt;
        public string? LastError { get; set; }
        public DateTimeOffset? LeasedUntil { get; set; }
        public Guid? LeaseToken { get; set; }

        public OutboxEntry ToEntry(DateTimeOffset now)
        {
            return new OutboxEntry(Delivery, Attempt, DueAt, LastError)
                { Payload = Payload, LeaseToken = LeaseToken, Age = now > QueuedAt ? now - QueuedAt : TimeSpan.Zero };
        }
    }
}