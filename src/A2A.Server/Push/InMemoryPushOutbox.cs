using A2A.Server.Storage;

namespace A2A.Server.Push;

/// <summary>
/// Process-local <see cref="PushOutbox"/>. For development and tests; pending deliveries are lost on restart.
/// <see cref="InMemoryTaskStore"/> writes its entries, so give that store this outbox.
/// </summary>
public sealed class InMemoryPushOutbox(TimeProvider? time = null) : PushOutbox
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<(TaskKey Task, string ConfigId), SortedDictionary<long, Entry>> _pending = new();
    private readonly Dictionary<(TaskKey Task, string ConfigId, long Sequence), OutboxEntry> _dead = new();

    /// <summary>Entries not yet delivered or dead-lettered, leased ones included.</summary>
    internal int Count
    {
        get { lock (_gate) return _pending.Values.Sum(g => g.Count); }
    }

    /// <summary>
    /// Adds new deliveries of one event, due now, with its JSON <paramref name="payload"/> (shared, never changed).
    /// Called by <see cref="InMemoryTaskStore"/> inside its commit; does not fail.
    /// </summary>
    internal void Add(IEnumerable<PushDelivery> deliveries, ReadOnlyMemory<byte> payload)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var d in deliveries) Group(d, create: true)![d.Sequence] = new Entry(d, payload, now);
        }
        NotifyPending();
    }

    public override ValueTask<IReadOnlyList<OutboxEntry>> LeaseDueAsync(int max, TimeSpan lease, CancellationToken ct = default)
    {
        if (max <= 0) return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>([]);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var leased = _pending.Values
                .Select(g => g.Values.First()) // only the oldest entry of a task and config may be in flight
                .Where(e => e.DueAt <= now && (e.LeasedUntil is not { } until || until <= now))
                .OrderBy(e => e.DueAt).ThenBy(e => e.Delivery.Sequence)
                .Take(max)
                .ToList();
            foreach (var e in leased) e.LeasedUntil = now + lease;
            return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(leased.Select(e => e.ToEntry()).ToList());
        }
    }

    public override ValueTask CompleteAsync(PushDelivery d, CancellationToken ct = default)
    {
        lock (_gate) Remove(d);
        return ValueTask.CompletedTask;
    }

    public override ValueTask RescheduleAsync(PushDelivery d, int attempt, DateTimeOffset dueAt, string error, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Group(d, create: false) is { } g && g.TryGetValue(d.Sequence, out var e))
            {
                e.Attempt = attempt;
                e.DueAt = dueAt;
                e.LastError = error;
                e.LeasedUntil = null;
            }
        }
        return ValueTask.CompletedTask;
    }

    public override ValueTask DeadLetterAsync(PushDelivery d, string error, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Remove(d) is { } e)
                _dead[(d.Task, d.ConfigId, d.Sequence)] = new OutboxEntry(e.Delivery, e.Attempt + 1, _time.GetUtcNow(), error) { Payload = e.Payload };
        }
        return ValueTask.CompletedTask;
    }

    public override ValueTask<IReadOnlyList<OutboxEntry>> ListDeadLettersAsync(string tenant, int max, CancellationToken ct = default)
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

    public override ValueTask<bool> RedriveAsync(PushDelivery d, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_dead.Remove((d.Task, d.ConfigId, d.Sequence), out var dead)) return ValueTask.FromResult(false);
            Group(d, create: true)![d.Sequence] = new Entry(dead.Delivery, dead.Payload, _time.GetUtcNow());
        }
        NotifyPending();
        return ValueTask.FromResult(true);
    }

    public override ValueTask DeleteAllAsync(TaskKey task, CancellationToken ct = default)
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

    private Entry? Remove(PushDelivery d)
    {
        if (Group(d, create: false) is not { } g || !g.Remove(d.Sequence, out var e)) return null;
        if (g.Count == 0) _pending.Remove((d.Task, d.ConfigId));
        return e;
    }

    private sealed class Entry(PushDelivery delivery, ReadOnlyMemory<byte> payload, DateTimeOffset dueAt)
    {
        public PushDelivery Delivery { get; } = delivery;
        public ReadOnlyMemory<byte> Payload { get; } = payload;
        public int Attempt { get; set; }
        public DateTimeOffset DueAt { get; set; } = dueAt;
        public string? LastError { get; set; }
        public DateTimeOffset? LeasedUntil { get; set; }

        public OutboxEntry ToEntry() => new(Delivery, Attempt, DueAt, LastError) { Payload = Payload };
    }
}
