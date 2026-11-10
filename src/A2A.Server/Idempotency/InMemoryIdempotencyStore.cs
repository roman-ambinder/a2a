using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace A2A.Server.Idempotency;

/// <summary>
///     Process-local idempotency store. A finished entry is kept for <see cref="IdempotencyOptions.Retention" /> after it
///     finished; a running one never expires. Beyond <see cref="IdempotencyOptions.MaxEntries" /> the least recently used
///     finished entry is dropped and the <c>a2a.idempotency.evictions</c> counter on meter <c>A2A</c> goes up.
/// </summary>
public sealed class InMemoryIdempotencyStore(IdempotencyOptions options, TimeProvider? time = null) : IdempotencyStore
{
    /// <summary>Name of the meter that carries the eviction counter.</summary>
    public const string MeterName = "A2A";

    /// <summary>Counter of entries dropped before their retention ended because the store was full.</summary>
    public const string EvictionsCounterName = "a2a.idempotency.evictions";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Evictions = Meter.CreateCounter<long>(EvictionsCounterName,
        description: "Idempotency entries dropped before their retention ended because the store was full.");

    private static readonly IdempotencyResult Mismatch = new IdempotencyResult.Mismatch();
    private readonly ConcurrentDictionary<IdempotencyScope, Entry> _entries = new();

    /// <summary>Entries in use order, most recent first. Locked for every access; its count is the store size.</summary>
    private readonly LinkedList<Entry> _lru = new();

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Entries held, running or finished. For tests.</summary>
    internal int Count
    {
        get
        {
            lock (_lru)
            {
                return _lru.Count;
            }
        }
    }

    /// <inheritdoc />
    public override ValueTask<IdempotencyResult> TryBeginAsync(IdempotencyScope scope, byte[] requestHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestHash);
        while (true)
        {
            var mine = new Entry(scope, (byte[])requestHash.Clone());
            var entry = _entries.GetOrAdd(scope, mine);
            if (ReferenceEquals(entry, mine))
            {
                lock (_lru)
                {
                    _lru.AddFirst(entry.Node);
                    EvictOverCapacity();
                }

                return ValueTask.FromResult<IdempotencyResult>(new IdempotencyResult.New(entry.ClaimId));
            }

            if (entry.IsExpired(_time.GetUtcNow()))
            {
                Remove(entry);
                continue;
            }

            if (!entry.Hash.AsSpan().SequenceEqual(requestHash)) return ValueTask.FromResult(Mismatch);

            lock (_lru)
            {
                if (entry.Node.List is not null)
                {
                    _lru.Remove(entry.Node);
                    _lru.AddFirst(entry.Node);
                }
            }

            var completion = entry.Completion.Task;
            return ValueTask.FromResult<IdempotencyResult>(completion.IsCompletedSuccessfully
                ? new IdempotencyResult.Completed(completion.Result)
                : new IdempotencyResult.InProgress(completion));
        }
    }

    /// <inheritdoc />
    public override ValueTask CompleteAsync(IdempotencyScope scope, Guid claimId, IdempotencyOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (_entries.TryGetValue(scope, out var entry) && entry.ClaimId == claimId)
        {
            var now = _time.GetUtcNow();
            entry.Complete(outcome,
                options.Retention >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + options.Retention);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask AbandonAsync(IdempotencyScope scope, Guid claimId,
        CancellationToken cancellationToken = default)
    {
        if (_entries.TryGetValue(scope, out var entry) && entry.ClaimId == claimId &&
            !entry.Completion.Task.IsCompleted)
        {
            Remove(entry);
            entry.Completion.TrySetCanceled(CancellationToken.None);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>A running entry never expires in this store, so there is nothing to extend.</summary>
    public override ValueTask RenewAsync(IdempotencyScope scope, Guid claimId,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    private void Remove(Entry entry)
    {
        if (!_entries.TryRemove(new KeyValuePair<IdempotencyScope, Entry>(entry.Scope, entry))) return;
        lock (_lru)
        {
            if (entry.Node.List is not null) _lru.Remove(entry.Node);
        }
    }

    /// <summary>
    ///     Drops finished entries from the least recently used end until the store fits. Caller holds the
    ///     <see cref="_lru" /> lock.
    /// </summary>
    private void EvictOverCapacity()
    {
        var max = Math.Max(0, options.MaxEntries);
        var now = _time.GetUtcNow();
        for (var node = _lru.Last; node is not null && _lru.Count > max;)
        {
            var previous = node.Previous;
            var entry = node.Value;
            if (entry.Completion.Task.IsCompletedSuccessfully) // a running request keeps its entry
            {
                _lru.Remove(node);
                _entries.TryRemove(new KeyValuePair<IdempotencyScope, Entry>(entry.Scope, entry));
                if (!entry.IsExpired(now)) Evictions.Add(1);
            }

            node = previous;
        }
    }

    private sealed class Entry
    {
        private DateTimeOffset _expiresAt = DateTimeOffset.MaxValue;

        public Entry(IdempotencyScope scope, byte[] hash)
        {
            Scope = scope;
            Hash = hash;
            Node = new LinkedListNode<Entry>(this);
        }

        public IdempotencyScope Scope { get; }
        public Guid ClaimId { get; } = Guid.NewGuid();
        public byte[] Hash { get; }
        public LinkedListNode<Entry> Node { get; }

        public TaskCompletionSource<IdempotencyOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Sets the expiry, then publishes the outcome (readers check completion before reading the expiry).</summary>
        public void Complete(IdempotencyOutcome outcome, DateTimeOffset expiresAt)
        {
            if (Completion.Task.IsCompleted) return;
            _expiresAt = expiresAt;
            Completion.TrySetResult(outcome);
        }

        public bool IsExpired(DateTimeOffset now)
        {
            return Completion.Task.IsCompletedSuccessfully && now >= _expiresAt;
        }
    }
}