namespace A2A.Server.Streaming;

/// <summary>
/// A stream event with its per-task sequence number from the <see cref="Storage.EventStore"/>, and its encoded JSON.
/// <see cref="Sequence"/> 0 means the event was not stored (a direct message reply that belongs to no task).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Json"/> is the ProtoJSON (UTF-8) of <see cref="Event"/>, encoded once by the server when the event is accepted
/// (<see cref="EventEncoder.Encode"/>); the event store keeps it, and SSE streams and push notifications write it as it is.
/// It is empty when the event was not encoded: snapshots built from a task, and events made with the two-argument
/// constructor. A consumer that needs the bytes calls <see cref="EventEncoder.EnsureEncoded"/>, which encodes only then.
/// </para>
/// <para>
/// The bytes are immutable by contract, like the event: nobody changes either once the event is stored. They never contain
/// the sequence, so an event can be encoded before its sequence is known. <see cref="Event"/> and <see cref="Json"/> are
/// get-only, so <c>evt with { Event = ... }</c> does not compile (it would keep the old bytes); build a new
/// <see cref="SequencedEvent(long, StreamResponse, ReadOnlyMemory{byte})"/> instead. <c>with { Sequence = ... }</c> still works.
/// </para>
/// </remarks>
/// <param name="Sequence">The per-task sequence number; 0 when the event was not stored.</param>
/// <param name="Event">The event.</param>
/// <param name="Json">The ProtoJSON of <paramref name="Event"/>, or empty when it was not encoded.</param>
public readonly record struct SequencedEvent(long Sequence, StreamResponse Event, ReadOnlyMemory<byte> Json)
{
    /// <summary>The event.</summary>
    public StreamResponse Event { get; } = Event;

    /// <summary>The ProtoJSON of <see cref="Event"/>, or empty when it was not encoded.</summary>
    public ReadOnlyMemory<byte> Json { get; } = Json;

    /// <summary>An event that is not encoded yet (<see cref="Json"/> is empty). Consumers encode it on demand with <see cref="EventEncoder.EnsureEncoded"/>.</summary>
    /// <param name="sequence">The per-task sequence number; 0 when the event was not stored.</param>
    /// <param name="evt">The event.</param>
    public SequencedEvent(long sequence, StreamResponse evt) : this(sequence, evt, ReadOnlyMemory<byte>.Empty)
    {
    }
}
