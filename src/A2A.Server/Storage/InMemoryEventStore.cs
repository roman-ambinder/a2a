using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using A2A.Json;
using A2A.Server.Streaming;

namespace A2A.Server.Storage;

/// <summary>
/// Process-local event log. Keeps the last <see cref="StreamingOptions.ReplayBufferSize"/> events per task and drops the
/// buffer of a terminal task <see cref="StreamingOptions.TerminalRetention"/> after <see cref="MarkTerminalAsync"/>.
/// </summary>
public sealed class InMemoryEventStore(StreamingOptions options, TimeProvider? time = null) : EventStore
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<TaskKey, TaskLog> _logs = new();

    public override ValueTask<long> AppendAsync(TaskKey task, StreamResponse evt, CancellationToken ct = default) =>
        ValueTask.FromResult(AppendAfter(task, evt, static () => { }));

    /// <summary>
    /// Runs <paramref name="write"/> and then appends <paramref name="evt"/>, both under the task's log lock: if
    /// <paramref name="write"/> throws, nothing is appended. <see cref="InMemoryTaskStore.CommitAsync"/> does its
    /// compare-and-set here, so commits to one task are serialized and the log follows their order. Then, still under the
    /// lock, <paramref name="appended"/> (which must not fail) gets the new sequence: readers of the log wait for it.
    /// </summary>
    internal long AppendAfter(TaskKey task, StreamResponse evt, Action write, Action<long>? appended = null)
    {
        var copy = A2AJson.Clone(evt); // before the write: nothing that can fail runs after it
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
                var seq = ++log.Last;
                log.Events.Add(new SequencedEvent(seq, copy));
                if (log.Events.Count > capacity) log.Events.RemoveRange(0, log.Events.Count - capacity);
                appended?.Invoke(seq);
                return seq;
            }
        }
    }

    public override async IAsyncEnumerable<SequencedEvent> ReadAfterAsync(TaskKey task, long afterSequence,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        SequencedEvent[] snapshot = [];
        if (_logs.TryGetValue(task, out var log))
        {
            lock (log) snapshot = log.Events.Where(e => e.Sequence > afterSequence).ToArray();
        }
        foreach (var e in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return e with { Event = A2AJson.Clone(e.Event) };
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public override ValueTask<long?> OldestRetainedAsync(TaskKey task, CancellationToken ct = default)
    {
        if (!_logs.TryGetValue(task, out var log)) return ValueTask.FromResult<long?>(null);
        lock (log) return ValueTask.FromResult<long?>(log.Events.Count == 0 ? null : log.Events[0].Sequence);
    }

    public override ValueTask<long> LastSequenceAsync(TaskKey task, CancellationToken ct = default)
    {
        if (!_logs.TryGetValue(task, out var log)) return ValueTask.FromResult(0L);
        lock (log) return ValueTask.FromResult(log.Last);
    }

    public override ValueTask MarkTerminalAsync(TaskKey task, CancellationToken ct = default)
    {
        if (!_logs.TryGetValue(task, out var log)) return ValueTask.CompletedTask;
        lock (log)
        {
            if (log.Timer is not null) return ValueTask.CompletedTask;
            log.Timer = _time.CreateTimer(_ => Evict(task, log), null, options.TerminalRetention, Timeout.InfiniteTimeSpan);
        }
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

    private sealed class TaskLog
    {
        public readonly List<SequencedEvent> Events = [];
        public long Last;
        public bool Evicted;
        public ITimer? Timer;
    }
}
