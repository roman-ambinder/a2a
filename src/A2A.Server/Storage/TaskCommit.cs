using A2A.Server.Idempotency;
using A2A.Server.Streaming;

namespace A2A.Server.Storage;

/// <summary>
///     One atomic change to a task: the task compare-and-set, the event that caused it, and the push deliveries for that
///     event. <see cref="TaskStore.CommitAsync" /> writes all of it or nothing.
/// </summary>
/// <param name="Record">The task as it must be stored. For a create its <see cref="TaskRecord.Version" /> must be 0.</param>
/// <param name="ExpectedVersion">The stored version this change is based on; 0 creates the task.</param>
/// <param name="Event">
///     The event appended to the task's log, encoded once (<see cref="EventEncoder" />):
///     <see cref="SequencedEvent.Json" /> holds its
///     bytes, which the store keeps for the log (as <see cref="EventStore.AppendAsync" /> does) and uses as the payload of
///     <paramref name="Deliveries" />. Its <see cref="SequencedEvent.Sequence" /> is ignored: the store allocates the
///     sequence.
/// </param>
/// <param name="Deliveries">Push deliveries for the event. The store sets their <see cref="PushDelivery.Sequence" />.</param>
public sealed record TaskCommit(
    TaskRecord Record,
    long ExpectedVersion,
    SequencedEvent Event,
    IReadOnlyList<PushDelivery> Deliveries)
{
    /// <summary>
    ///     A commit of an event that is not encoded yet: <paramref name="evt" /> is encoded here, once (
    ///     <see cref="EventEncoder.Encode" />).
    /// </summary>
    /// <param name="record">The task as it must be stored.</param>
    /// <param name="expectedVersion">The stored version this change is based on; 0 creates the task.</param>
    /// <param name="evt">The event appended to the task's log.</param>
    /// <param name="deliveries">Push deliveries for the event.</param>
    public TaskCommit(TaskRecord record, long expectedVersion, StreamResponse evt,
        IReadOnlyList<PushDelivery> deliveries)
        : this(record, expectedVersion, EventEncoder.Encode(0, evt), deliveries)
    {
    }

    /// <summary>
    ///     The idempotency outcome this commit makes true (the request created the task), or null. A store that shares a
    ///     transaction
    ///     with the <see cref="Idempotency.IdempotencyStore" /> writes the outcome in the same transaction, so a crash cannot
    ///     leave
    ///     the task without it. Any other store ignores it, and the engine records the outcome after the commit. A claim that
    ///     no longer owns the entry is not an error: nothing is written.
    /// </summary>
    public IdempotencyCommit? Idempotency { get; init; }

    /// <summary>True when the commit creates the task (<see cref="ExpectedVersion" /> is 0).</summary>
    public bool IsCreate => ExpectedVersion == 0;

    /// <summary>
    ///     A unique id of this commit, or null. A store that implements <see cref="ITaskCommitLookup" /> keeps it with the
    ///     event, so the engine can find out whether a commit whose call failed was stored after all (for example when the
    ///     database committed and the answer was then lost). Any other store ignores it.
    /// </summary>
    public string? MutationId { get; init; }
}

/// <summary>The outcome of a <see cref="TaskCommit" />.</summary>
/// <param name="Record">The stored task, with its new version.</param>
/// <param name="Sequence">The sequence the event got in the task's log.</param>
public sealed record CommitResult(TaskRecord Record, long Sequence);

/// <summary>A push notification to send for one event to one push config of a task.</summary>
/// <param name="Task">The task the event belongs to.</param>
/// <param name="ConfigId">The push config to deliver to.</param>
/// <param name="Sequence">The event's sequence. Set by the store when the commit allocates it.</param>
/// <param name="DeliveryId">Unique id of this delivery, sent to the receiver so it can drop duplicates.</param>
public sealed record PushDelivery(TaskKey Task, string ConfigId, long Sequence, string DeliveryId)
{
    /// <summary>
    ///     W3C <c>traceparent</c> of the agent run that produced the event, or null. Stored with the delivery so its
    ///     <c>a2a.push.deliver</c> span joins that trace, whichever instance sends it and however late.
    /// </summary>
    public string? TraceParent { get; init; }
}

/// <summary>
///     Optional capability of a <see cref="TaskStore" />: commits several events of one task, and the task as it is after
///     all of them,
///     as one atomic unit. The engine uses it when an agent produces events faster than they are stored (for example many
///     artifact
///     chunks), so the task is copied and written once per batch instead of once per event. A store without it gets one
///     <see cref="TaskStore.CommitAsync" /> per event, as before.
/// </summary>
public interface IBatchTaskCommit
{
    /// <summary>
    ///     Writes <see cref="TaskBatchCommit.Record" /> (compare-and-set on <see cref="TaskBatchCommit.ExpectedVersion" />),
    ///     appends
    ///     every event of <see cref="TaskBatchCommit.Events" /> to the task's log with consecutive sequences, in order, and
    ///     stores the
    ///     deliveries of each event with that event's sequence: all of it or nothing. The same rules as
    ///     <see cref="TaskStore.CommitAsync" />
    ///     apply to each event. The stored record gets version expected + 1, one version for the whole batch.
    /// </summary>
    /// <returns>The stored record and the sequence of the first event; event i has sequence <c>FirstSequence + i</c>.</returns>
    /// <exception cref="ConcurrencyException">
    ///     The stored version differs, or the task does not exist for this owner. Nothing
    ///     was written.
    /// </exception>
    /// <exception cref="ArgumentException">An empty batch, a create (expected version 0), or a delivery of another task.</exception>
    ValueTask<BatchCommitResult>
        CommitBatchAsync(TaskBatchCommit commit, CancellationToken cancellationToken = default);
}

/// <summary>
///     A change to an existing task caused by several events, committed as one unit (<see cref="IBatchTaskCommit" />
///     ).
/// </summary>
/// <param name="Record">The task as it must be stored, after every event.</param>
/// <param name="ExpectedVersion">
///     The stored version this change is based on. Must be greater than 0: a batch never creates
///     a task.
/// </param>
/// <param name="Events">The events, in order, each with its push deliveries. At least one.</param>
public sealed record TaskBatchCommit(TaskRecord Record, long ExpectedVersion, IReadOnlyList<BatchedEvent> Events)
{
    /// <summary>
    ///     A unique id of this batch, or null. A store that implements <see cref="ITaskCommitLookup" /> keeps it with the
    ///     first event of the batch (see <see cref="TaskCommit.MutationId" />).
    /// </summary>
    public string? MutationId { get; init; }
}

/// <summary>
///     Optional capability of a <see cref="TaskStore" />: tells whether a commit was stored, by its
///     <see cref="TaskCommit.MutationId" /> (or <see cref="TaskBatchCommit.MutationId" />). The engine asks after a commit
///     call failed, or reported a version conflict, so a commit that was stored is not applied a second time and a task
///     whose write landed is not failed. A store without it gets the old behaviour: a failed call fails the run, and a
///     conflict reloads the task and applies the event again.
/// </summary>
public interface ITaskCommitLookup
{
    /// <summary>
    ///     The sequence of the event stored by the commit with <paramref name="mutationId" /> (the first event, for a batch),
    ///     or null when no such commit is stored for <paramref name="task" />. A store may forget an id once its event left
    ///     the replay buffer; the engine asks right after the commit, so the event is still there.
    /// </summary>
    ValueTask<long?> FindCommitAsync(TaskKey task, string mutationId, CancellationToken cancellationToken = default);
}

/// <summary>One event of a <see cref="TaskBatchCommit" />.</summary>
/// <param name="Event">
///     The event, encoded once (see <see cref="TaskCommit.Event" />). Its sequence is ignored: the store
///     allocates it.
/// </param>
/// <param name="Deliveries">Push deliveries for this event. The store sets their <see cref="PushDelivery.Sequence" />.</param>
public sealed record BatchedEvent(SequencedEvent Event, IReadOnlyList<PushDelivery> Deliveries);

/// <summary>The outcome of a <see cref="TaskBatchCommit" />.</summary>
/// <param name="Record">The stored task, with its new version.</param>
/// <param name="FirstSequence">The sequence of the first event; the others follow it, one apart.</param>
public sealed record BatchCommitResult(TaskRecord Record, long FirstSequence);