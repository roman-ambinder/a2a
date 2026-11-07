namespace A2A.Server.Storage;

/// <summary>
/// One atomic change to a task: the task compare-and-set, the event that caused it, and the push deliveries for that
/// event. <see cref="TaskStore.CommitAsync"/> writes all of it or nothing.
/// </summary>
/// <param name="Record">The task as it must be stored. For a create its <see cref="TaskRecord.Version"/> must be 0.</param>
/// <param name="ExpectedVersion">The stored version this change is based on; 0 creates the task.</param>
/// <param name="Event">The event appended to the task's log. The store allocates its sequence.</param>
/// <param name="Deliveries">Push deliveries for the event. The store sets their <see cref="PushDelivery.Sequence"/>.</param>
public sealed record TaskCommit(TaskRecord Record, long ExpectedVersion, StreamResponse Event, IReadOnlyList<PushDelivery> Deliveries)
{
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
public sealed record PushDelivery(TaskKey Task, string ConfigId, long Sequence, string DeliveryId);
