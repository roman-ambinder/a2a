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
    /// Encodes <paramref name="evt"/> into a new byte array owned by the returned event. The bytes are the same as
    /// <see cref="A2AJson.SerializeToUtf8Bytes{T}"/> of the event.
    /// </summary>
    /// <param name="sequence">The sequence to stamp; 0 when it is not known yet (the bytes never contain it).</param>
    /// <param name="evt">The event.</param>
    public static SequencedEvent Encode(long sequence, StreamResponse evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return new SequencedEvent(sequence, evt, A2AJson.SerializeToUtf8Bytes(evt));
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
        JsonSerializer.Serialize(counter, evt, A2AJson.GetTypeInfo<StreamResponse>());
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
