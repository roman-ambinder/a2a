using A2A.Server.Streaming;

namespace A2A.Server.Storage;

/// <summary>
/// Per-task event log. Every event applied to a task gets a monotonic sequence number that starts at 1. The sequence is
/// the ordering and deduplication key for streams (resume) and push notifications. Like the task store, the log is
/// owner-scoped: it is addressed by <see cref="TaskKey"/>.
/// </summary>
/// <remarks>
/// <para>
/// Ownership: an appended event belongs to the store, and the events it returns are never mutated by callers. The server
/// stores an event before it publishes the same instance to streams and webhooks, and nobody changes it after that.
/// </para>
/// <para>
/// Encoding: the server encodes each event once (<see cref="EventEncoder"/>) and appends it with its bytes in
/// <see cref="SequencedEvent.Json"/>. Keep those bytes and return them from <see cref="ReadAfterAsync"/>: replays and gap
/// fills write them as they are. A store that persists only the bytes may rebuild the event from them; a store that does not
/// keep the bytes may return events with an empty <see cref="SequencedEvent.Json"/>, and readers then encode them again.
/// </para>
/// </remarks>
public abstract class EventStore
{
    /// <summary>
    /// Appends an event and returns the sequence the store assigned to it. The first event of a task gets 1.
    /// <see cref="SequencedEvent.Sequence"/> of <paramref name="evt"/> is ignored (the server passes 0): store
    /// <c>evt with { Sequence = assigned }</c>. <see cref="SequencedEvent.Json"/> carries the encoded event (it never
    /// contains the sequence, so it was encoded before the sequence was known).
    /// </summary>
    public abstract ValueTask<long> AppendAsync(TaskKey task, SequencedEvent evt, CancellationToken cancellationToken = default);

    /// <summary>Reads the retained events with a sequence greater than <paramref name="afterSequence"/>, in order.</summary>
    public abstract IAsyncEnumerable<SequencedEvent> ReadAfterAsync(TaskKey task, long afterSequence, CancellationToken cancellationToken = default);

    /// <summary>The lowest retained sequence, or null when no events are retained.</summary>
    public abstract ValueTask<long?> OldestRetainedAsync(TaskKey task, CancellationToken cancellationToken = default);

    /// <summary>The highest sequence ever appended for the task, or 0 when there is none.</summary>
    public abstract ValueTask<long> LastSequenceAsync(TaskKey task, CancellationToken cancellationToken = default);

    /// <summary>Tells the store the task reached a terminal state, so it may drop the retained events after a retention time.</summary>
    public abstract ValueTask MarkTerminalAsync(TaskKey task, CancellationToken cancellationToken = default);
}
