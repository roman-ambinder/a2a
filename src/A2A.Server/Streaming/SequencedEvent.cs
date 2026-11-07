namespace A2A.Server.Streaming;

/// <summary>
/// A stream event with its per-task sequence number from the <see cref="Storage.EventStore"/>.
/// <see cref="Sequence"/> 0 means the event was not stored (a direct message reply that belongs to no task).
/// </summary>
public readonly record struct SequencedEvent(long Sequence, StreamResponse Event);
