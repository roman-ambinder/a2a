using A2A.Server.Streaming;

namespace A2A.Server.Storage;

/// <summary>
/// Per-task event log. Every event applied to a task gets a monotonic sequence number that starts at 1. The sequence is
/// the ordering and deduplication key for streams (resume) and push notifications. Like the task store, the log is
/// owner-scoped: it is addressed by <see cref="TaskKey"/>.
/// </summary>
public abstract class EventStore
{
    /// <summary>
    /// Appends an event and returns its sequence. The first event of a task gets 1. The request handler does not call
    /// this: it appends through <see cref="TaskStore.CommitAsync"/>, together with the task change.
    /// </summary>
    public abstract ValueTask<long> AppendAsync(TaskKey task, StreamResponse evt, CancellationToken ct = default);

    /// <summary>Reads the retained events with a sequence greater than <paramref name="afterSequence"/>, in order.</summary>
    public abstract IAsyncEnumerable<SequencedEvent> ReadAfterAsync(TaskKey task, long afterSequence, CancellationToken ct = default);

    /// <summary>The lowest retained sequence, or null when no events are retained.</summary>
    public abstract ValueTask<long?> OldestRetainedAsync(TaskKey task, CancellationToken ct = default);

    /// <summary>The highest sequence ever appended for the task, or 0 when there is none.</summary>
    public abstract ValueTask<long> LastSequenceAsync(TaskKey task, CancellationToken ct = default);

    /// <summary>Tells the store the task reached a terminal state, so it may drop the retained events after a retention time.</summary>
    public abstract ValueTask MarkTerminalAsync(TaskKey task, CancellationToken ct = default);

    /// <summary>
    /// Deletes the task's whole log, retained events and the sequence counter included: a later append starts at 1 again.
    /// Owner-scoped through <paramref name="task"/>; another owner's key, or a task without a log, deletes nothing.
    /// </summary>
    public abstract ValueTask DeleteAsync(TaskKey task, CancellationToken ct = default);
}
