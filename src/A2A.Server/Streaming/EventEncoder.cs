using System.Text.Json;
using A2A.Json;

namespace A2A.Server.Streaming;

/// <summary>
/// Encodes stream events to their ProtoJSON (UTF-8) once, so the event store, SSE streams and push notifications share the
/// same bytes (see <see cref="SequencedEvent.Json"/>).
/// </summary>
public static class EventEncoder
{
    /// <summary>
    /// Encodes <paramref name="evt"/> into a new byte array owned by the returned event. The bytes are the 1.0 shape
    /// (<see cref="A2AJson.V10Options"/>): the members A2A 1.1 added are left out, so the stored bytes can go to every caller as
    /// they are. A 1.1 caller gets <see cref="JsonFor"/> instead.
    /// </summary>
    /// <param name="sequence">The sequence to stamp; 0 when it is not known yet (the bytes never contain it).</param>
    /// <param name="evt">The event.</param>
    public static SequencedEvent Encode(long sequence, StreamResponse evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return new SequencedEvent(sequence, evt, JsonSerializer.SerializeToUtf8Bytes(evt, A2AJson.GetTypeInfo<StreamResponse>(A2AProtocol.Version)));
    }

    /// <summary>
    /// True when a caller of <paramref name="protocolVersion"/> may receive <paramref name="evt"/>. The A2A 1.1 event kinds
    /// (a message added to the task, a refused request on a live stream) go only to 1.1 callers (1.1 §3.2.8); a 1.0 caller
    /// learns about an added message from the task.
    /// </summary>
    /// <param name="evt">The event.</param>
    /// <param name="protocolVersion">The caller's protocol version (<c>Major.Minor</c>).</param>
    public static bool IsVisibleTo(StreamResponse evt, string? protocolVersion) =>
        evt is not (MessageUpdateEvent or StreamErrorEvent) || A2AProtocol.IsVersion11OrLater(protocolVersion);

    /// <summary>
    /// The bytes of <paramref name="evt"/> for a caller of <paramref name="protocolVersion"/>. A 1.0 caller gets the stored
    /// bytes (<see cref="EnsureEncoded"/>). A 1.1 caller gets the event encoded with the 1.1 members, and a stored task event
    /// (sequence above 0) always carries its <c>generation</c>: the task's generation equals the event's sequence, so an
    /// event rebuilt from stored bytes gets it from <see cref="SequencedEvent.Sequence"/>.
    /// </summary>
    /// <param name="evt">The event.</param>
    /// <param name="protocolVersion">The caller's protocol version (<c>Major.Minor</c>).</param>
    public static ReadOnlyMemory<byte> JsonFor(SequencedEvent evt, string? protocolVersion)
    {
        if (!A2AProtocol.IsVersion11OrLater(protocolVersion)) return EnsureEncoded(evt).Json;
        var bytes = A2AJson.SerializeToUtf8Bytes(evt.Event);
        return evt.Sequence > 0 && CarriesGeneration(evt.Event) && GenerationOf(evt.Event) is null
            ? WithGeneration(bytes, evt.Sequence)
            : bytes;
    }

    /// <summary>The event kinds whose payload has a <c>generation</c> member.</summary>
    private static bool CarriesGeneration(StreamResponse evt) =>
        evt is TaskEvent or StatusUpdateEvent or ArtifactUpdateEvent or MessageUpdateEvent;

    private static long? GenerationOf(StreamResponse evt) => evt switch
    {
        TaskEvent t => t.Task.Generation,
        StatusUpdateEvent s => s.StatusUpdate.Generation,
        ArtifactUpdateEvent a => a.ArtifactUpdate.Generation,
        MessageUpdateEvent u => u.MessageUpdate.Generation,
        _ => null,
    };

    /// <summary>
    /// Adds <c>"generation":"n"</c> as the last member of the payload of a stream event, <c>{"kind":{...}}</c>, without
    /// parsing it: the payload object closes at the second-to-last byte, and it is never empty.
    /// </summary>
    internal static byte[] WithGeneration(byte[] json, long generation)
    {
        var member = System.Text.Encoding.UTF8.GetBytes(
            ",\"generation\":\"" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"");
        if (json.Length < 4 || json[^1] != (byte)'}' || json[^2] != (byte)'}')
            throw new InvalidOperationException("Not an encoded stream event.");
        var result = new byte[json.Length + member.Length];
        json.AsSpan(0, json.Length - 2).CopyTo(result);
        member.CopyTo(result.AsSpan(json.Length - 2));
        result[^2] = (byte)'}';
        result[^1] = (byte)'}';
        return result;
    }

    /// <summary>Returns <paramref name="evt"/> as it is when it carries its bytes, or encoded (<see cref="Encode"/>) when <see cref="SequencedEvent.Json"/> is empty.</summary>
    /// <param name="evt">The event.</param>
    public static SequencedEvent EnsureEncoded(SequencedEvent evt) =>
        evt.Json.IsEmpty ? Encode(evt.Sequence, evt.Event) : evt;

    /// <summary>
    /// The length in bytes of the ProtoJSON of <paramref name="evt"/>, measured without keeping the bytes. To encode as well,
    /// call <see cref="Encode"/> and use the length of its <see cref="SequencedEvent.Json"/> instead of measuring first.
    /// </summary>
    /// <param name="evt">The event.</param>
    public static int MeasureUtf8(StreamResponse evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        using var counter = new CountingStream();
        JsonSerializer.Serialize(counter, evt, A2AJson.GetTypeInfo<StreamResponse>(A2AProtocol.Version));
        return checked((int)counter.Length);
    }

    /// <summary>A write-only stream that counts the bytes written to it and discards them.</summary>
    private sealed class CountingStream : Stream
    {
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
        public override void WriteByte(byte value) => _length++;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
