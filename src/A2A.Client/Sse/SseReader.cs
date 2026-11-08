using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace A2A.Client.Sse;

/// <summary>A dispatched Server-Sent Event.</summary>
/// <param name="Data">The data field.</param>
/// <param name="EventType">The event field, if any.</param>
/// <param name="Id">The last event id seen on the stream so far (WHATWG semantics: it carries over to later events).</param>
public sealed record SseEvent(string Data, string? EventType, string? Id)
{
    /// <summary>The id field of this event only. Null when this event carried no <c>id:</c> line. Not part of equality.</summary>
    public string? FrameId { get; init; }

    /// <inheritdoc/>
    public bool Equals(SseEvent? other) => other is not null && Data == other.Data && EventType == other.EventType && Id == other.Id;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Data, EventType, Id);
}

/// <summary>Server-Sent Events parser (WHATWG HTML §9.2.6). Handles CRLF/LF/CR, multi-line data, comments, and a UTF-8 BOM.</summary>
/// <remarks>
/// Lines are found and their field names compared as UTF-8 bytes in a pooled buffer; text is decoded once per value, and the
/// <see cref="SseEvent.Data"/> of an event is decoded once when the event is dispatched. Lines have no length limit.
/// The stream is disposed when reading ends.
/// </remarks>
public static class SseReader
{
    private const int InitialBufferSize = 4096;
    private static ReadOnlySpan<byte> Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Reads events from <paramref name="stream"/> until it ends.</summary>
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        var parser = new LineParser();
        try
        {
            int start = 0, end = 0;
            var eof = false;
            var atStart = true;     // a BOM is only skipped at the very start of the stream
            var skipLf = false;     // the last line ended with CR: a LF right after it belongs to the same line break
            while (true)
            {
                if (atStart && !eof && end - start < Bom.Length && Bom.StartsWith(buffer.AsSpan(start, end - start)))
                {
                    // Not enough bytes yet to tell whether the stream starts with a BOM.
                }
                else
                {
                    if (atStart)
                    {
                        if (buffer.AsSpan(start, end - start).StartsWith(Bom)) start += Bom.Length;
                        atStart = false;
                    }
                    if (skipLf && start < end)
                    {
                        if (buffer[start] == (byte)'\n') start++;
                        skipLf = false;
                    }
                    var newline = buffer.AsSpan(start, end - start).IndexOfAny((byte)'\r', (byte)'\n');
                    if (newline >= 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var lineStart = start;
                        var terminator = buffer[start + newline];
                        start += newline + 1;
                        if (terminator == (byte)'\r') skipLf = true;
                        if (parser.Process(buffer.AsSpan(lineStart, newline)) is { } evt) yield return evt;
                        continue;
                    }
                    // End of stream: per spec, an incomplete line or event is discarded.
                    if (eof) yield break;
                }

                // Need more bytes: make room (move the partial line to the front, or grow), then read.
                if (start > 0)
                {
                    buffer.AsSpan(start, end - start).CopyTo(buffer);
                    end -= start;
                    start = 0;
                }
                else if (end == buffer.Length)
                {
                    var bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, end).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(end), cancellationToken).ConfigureAwait(false);
                if (read == 0) eof = true;
                else end += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            parser.Dispose();
            stream.Dispose();
        }
    }

    /// <summary>The per-stream field state; turns lines into events.</summary>
    private sealed class LineParser : IDisposable
    {
        private byte[] _data = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        private int _dataLength;
        private bool _hasData;
        private string? _eventType;
        private string? _lastId;
        private string? _frameId;

        /// <summary>Applies one line (without its terminator). Returns the event a blank line dispatches, if any.</summary>
        public SseEvent? Process(ReadOnlySpan<byte> line)
        {
            if (line.IsEmpty)
            {
                SseEvent? evt = _hasData
                    ? new SseEvent(Encoding.UTF8.GetString(_data, 0, _dataLength), _eventType, _lastId) { FrameId = _frameId }
                    : null;
                _dataLength = 0;
                _hasData = false;
                _eventType = null;
                _frameId = null;
                return evt;
            }

            if (line[0] == (byte)':') return null; // comment / keep-alive

            var colon = line.IndexOf((byte)':');
            ReadOnlySpan<byte> field, value;
            if (colon < 0)
            {
                field = line;
                value = default;
            }
            else
            {
                field = line[..colon];
                value = line[(colon + 1)..];
                if (!value.IsEmpty && value[0] == (byte)' ') value = value[1..];
            }

            if (field.SequenceEqual("data"u8))
            {
                if (_hasData) Append((byte)'\n');
                Append(value);
                _hasData = true;
            }
            else if (field.SequenceEqual("event"u8))
            {
                _eventType = Encoding.UTF8.GetString(value);
            }
            else if (field.SequenceEqual("id"u8))
            {
                if (value.IndexOf((byte)0) < 0) _lastId = _frameId = Encoding.UTF8.GetString(value);
            }
            // "retry" and unknown fields are ignored.
            return null;
        }

        private void Append(byte b)
        {
            Reserve(1);
            _data[_dataLength++] = b;
        }

        private void Append(ReadOnlySpan<byte> bytes)
        {
            Reserve(bytes.Length);
            bytes.CopyTo(_data.AsSpan(_dataLength));
            _dataLength += bytes.Length;
        }

        private void Reserve(int more)
        {
            if (_dataLength + more <= _data.Length) return;
            var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(_data.Length * 2, _dataLength + more));
            _data.AsSpan(0, _dataLength).CopyTo(bigger);
            ArrayPool<byte>.Shared.Return(_data);
            _data = bigger;
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_data);
            _data = [];
        }
    }
}
