using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using A2A.Server.Streaming;

namespace A2A.Server.Storage;

/// <summary>
///     Process-local event log. Keeps the last <see cref="StreamingOptions.ReplayBufferSize" /> events per task and drops
///     the
///     buffer of a terminal task <see cref="StreamingOptions.TerminalRetention" /> after <see cref="MarkTerminalAsync" />.
///     It stores the event instances it is given, with their encoded bytes, and returns them without copying or encoding
///     again:
///     an appended event belongs to the store and nobody changes it. Each retained event holds both the event model and
///     its UTF-8
///     bytes, so memory per event is roughly double the model alone: size <see cref="StreamingOptions.ReplayBufferSize" />
///     for that.
/// </summary>
public sealed class InMemoryEventStore(StreamingOptions options, TimeProvider? time = null) : EventStore
{
    private readonly ConcurrentDictionary<TaskKey, TaskLog> _logs = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public override ValueTask<long> AppendAsync(TaskKey task, SequencedEvent evt,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(AppendAfter(task, evt, static () => { }));
    }

    /// <summary>
    ///     Runs <paramref name="write" /> and then appends <paramref name="evt" />, both under the task's log lock: if
    ///     <paramref name="write" /> throws, nothing is appended. <see cref="InMemoryTaskStore.CommitAsync" /> does its
    ///     compare-and-set here, so commits to one task are serialized and the log follows their order. Then, still under the
    ///     lock, <paramref name="appended" /> (which must not fail) gets the new sequence.
    /// </summary>
    internal long AppendAfter(TaskKey task, SequencedEvent evt, Action write, Action<long>? appended = null)
    {
        return AppendManyAfter(task, [evt], write, appended);
    }

    /// <summary>
    ///     Like <see cref="AppendAfter" /> for several events: runs <paramref name="write" />, then appends every event with
    ///     consecutive sequences, all under the task's log lock. <paramref name="appended" /> gets the first sequence.
    ///     Returns the first sequence.
    /// </summary>
    internal long AppendManyAfter(TaskKey task, IReadOnlyList<SequencedEvent> events, Action write,
        Action<long>? appended = null)
    {
        var capacity = Math.Max(1, options.ReplayBufferSize);
        while (true)
        {
            var log = _logs.GetOrAdd(task, _ => new TaskLog());
            lock (log)
            {
                if (log.Evicted) continue; // lost a race with eviction; start a fresh log
                try
                {
                    write();
                }
                catch when (log.Last == 0)
                {
                    // Do not keep an empty log created only for a write that failed.
                    log.Evicted = true;
                    _logs.TryRemove(new KeyValuePair<TaskKey, TaskLog>(task, log));
                    throw;
                }

                var first = log.Last + 1;
                foreach (var evt in events) log.Add(evt with { Sequence = ++log.Last }, capacity);
                appended?.Invoke(first);
                return first;
            }
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<SequencedEvent> ReadAfterAsync(TaskKey task, long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        SequencedEvent[] snapshot = [];
        if (_logs.TryGetValue(task, out var log))
            lock (log)
            {
                snapshot = log.CopyAfter(afterSequence);
            }

        foreach (var e in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return e;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<SequencedEvent> ReadRangeAsync(TaskKey task, long afterSequence,
        long beforeSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        SequencedEvent[] snapshot = [];
        if (beforeSequence > afterSequence + 1 && _logs.TryGetValue(task, out var log))
            lock (log)
            {
                snapshot = log.CopyAfter(afterSequence, beforeSequence);
            }

        foreach (var e in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return e;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override ValueTask<long?> OldestRetainedAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        if (!_logs.TryGetValue(task, out var log)) return ValueTask.FromResult<long?>(null);
        lock (log)
        {
            return ValueTask.FromResult<long?>(log.Count == 0 ? null : log.Last - log.Count + 1);
        }
    }

    /// <inheritdoc />
    public override ValueTask<long> LastSequenceAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        if (!_logs.TryGetValue(task, out var log)) return ValueTask.FromResult(0L);
        lock (log)
        {
            return ValueTask.FromResult(log.Last);
        }
    }

    /// <inheritdoc />
    public override ValueTask MarkTerminalAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        if (!_logs.TryGetValue(task, out var log)) return ValueTask.CompletedTask;
        lock (log)
        {
            if (log.Timer is not null) return ValueTask.CompletedTask;
            log.Timer = _time.CreateTimer(_ => Evict(task, log), null, options.TerminalRetention,
                Timeout.InfiniteTimeSpan);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask DeleteAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        if (_logs.TryGetValue(task, out var log)) Evict(task, log);
        return ValueTask.CompletedTask;
    }

    private void Evict(TaskKey task, TaskLog log)
    {
        lock (log)
        {
            log.Evicted = true;
            log.Timer?.Dispose();
        }

        _logs.TryRemove(new KeyValuePair<TaskKey, TaskLog>(task, log));
    }

    /// <summary>
    ///     The retained events of one task: a ring buffer that grows (doubling) up to the replay buffer size, then overwrites
    ///     the
    ///     oldest event. Sequences in it are consecutive and end at <see cref="Last" />, so an event is found by arithmetic.
    ///     Use under the log's lock.
    /// </summary>
    private sealed class TaskLog
    {
        public int Count;
        public bool Evicted;
        public long Last;
        public ITimer? Timer;
        private SequencedEvent[] _ring = new SequencedEvent[8];
        private int _start; // index of the oldest retained event

        public void Add(SequencedEvent evt, int capacity)
        {
            if (Count >= capacity)
            {
                // Full: the oldest is dropped.
                _ring[_start] = default;
                _start = (_start + 1) % _ring.Length;
                Count--;
            }
            else if (Count == _ring.Length)
            {
                Grow(capacity);
            }

            _ring[(_start + Count) % _ring.Length] = evt;
            Count++;
        }

        /// <summary>
        ///     The retained events with a sequence above <paramref name="afterSequence" /> and below
        ///     <paramref name="beforeSequence" />,
        ///     oldest first.
        /// </summary>
        public SequencedEvent[] CopyAfter(long afterSequence, long beforeSequence = long.MaxValue)
        {
            var oldest = Last - Count + 1;
            var skip = afterSequence < oldest ? 0 : afterSequence - oldest + 1;
            if (skip >= Count) return [];
            var end = beforeSequence > Last ? Count : beforeSequence - oldest; // exclusive index
            if (end <= skip) return [];
            var result = new SequencedEvent[(int)(end - skip)];
            for (var i = 0; i < result.Length; i++) result[i] = _ring[(_start + (int)skip + i) % _ring.Length];
            return result;
        }

        private void Grow(int capacity)
        {
            var bigger = new SequencedEvent[(int)Math.Min(capacity, (long)_ring.Length * 2)];
            for (var i = 0; i < Count; i++) bigger[i] = _ring[(_start + i) % _ring.Length];
            _ring = bigger;
            _start = 0;
        }
    }
}