using A2A.Server.Streaming;

namespace A2A.Server.Storage;

/// <summary>
/// One atomic change to a task: the task compare-and-set, the event that caused it, and the push deliveries for that
/// event. <see cref="TaskStore.CommitAsync"/> writes all of it or nothing.
/// </summary>
/// <param name="Record">The task as it must be stored. For a create its <see cref="TaskRecord.Version"/> must be 0.</param>
/// <param name="ExpectedVersion">The stored version this change is based on; 0 creates the task.</param>
/// <param name="Event">
/// The event appended to the task's log, encoded once (<see cref="EventEncoder"/>): <see cref="SequencedEvent.Json"/> holds its
/// bytes, which the store keeps for the log (as <see cref="EventStore.AppendAsync"/> does) and uses as the payload of
/// <paramref name="Deliveries"/>. Its <see cref="SequencedEvent.Sequence"/> is ignored: the store allocates the sequence.
/// </param>
/// <param name="Deliveries">Push deliveries for the event. The store sets their <see cref="PushDelivery.Sequence"/>.</param>
public sealed record TaskCommit(TaskRecord Record, long ExpectedVersion, SequencedEvent Event, IReadOnlyList<PushDelivery> Deliveries)
{
    /// <summary>
    /// The idempotency outcome this commit makes true (the request created the task), or null. A store that shares a transaction
    /// with the <see cref="Idempotency.IdempotencyStore"/> writes the outcome in the same transaction, so a crash cannot leave
    /// the task without it. Any other store ignores it, and the engine records the outcome after the commit. A claim that
    /// no longer owns the entry is not an error: nothing is written.
    /// </summary>
    public Idempotency.IdempotencyCommit? Idempotency { get; init; }

    /// <summary>A commit of an event that is not encoded yet: <paramref name="evt"/> is encoded here, once (<see cref="EventEncoder.Encode"/>).</summary>
    /// <param name="record">The task as it must be stored.</param>
    /// <param name="expectedVersion">The stored version this change is based on; 0 creates the task.</param>
    /// <param name="evt">The event appended to the task's log.</param>
    /// <param name="deliveries">Push deliveries for the event.</param>
    public TaskCommit(TaskRecord record, long expectedVersion, StreamResponse evt, IReadOnlyList<PushDelivery> deliveries)
        : this(record, expectedVersion, EventEncoder.Encode(0, evt), deliveries)
    {
    }

    /// <summary>True when the commit creates the task (<see cref="ExpectedVersion"/> is 0).</summary>
    public bool IsCreate => ExpectedVersion == 0;
}

/// <summary>The outcome of a <see cref="TaskCommit"/>.</summary>
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
    /// W3C <c>traceparent</c> of the agent run that produced the event, or null. Stored with the delivery so its
    /// <c>a2a.push.deliver</c> span joins that trace, whichever instance sends it and however late.
    /// </summary>
    public string? TraceParent { get; init; }
}
