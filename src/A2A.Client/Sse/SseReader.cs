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

    /// <inheritdoc />
    public bool Equals(SseEvent? other)
    {
        return other is not null && Data == other.Data && EventType == other.EventType && Id == other.Id;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(Data, EventType, Id);
    }
}

/// <summary>
///     A dispatched event whose data is still UTF-8 bytes in the reader's buffer (<see cref="SseReader.ReadFramesAsync(Stream, int, CancellationToken)" />
///     ). One
///     instance per stream, reused: use it before the next read.
/// </summary>
internal sealed class SseFrame
{
    /// <summary>The data field as UTF-8. Valid until the next read of the stream.</summary>
    public ReadOnlyMemory<byte> Data { get; set; }

    /// <summary>The event field, if any.</summary>
    public string? EventType { get; set; }

    /// <summary>The last event id seen on the stream so far.</summary>
    public string? Id { get; set; }

    /// <summary>The id field of this event only, or null.</summary>
    public string? FrameId { get; set; }

    /// <summary>The data decoded as text, as <see cref="SseEvent.Data" /> has it.</summary>
    public string DataText => Encoding.UTF8.GetString(Data.Span);

    /// <summary>A copy that does not depend on the reader's buffer.</summary>
    public SseEvent ToEvent()
    {
        return new SseEvent(DataText, EventType, Id) { FrameId = FrameId };
    }
}

/// <summary>
///     Server-Sent Events parser (WHATWG HTML §9.2.6). Handles CRLF/LF/CR, multi-line data, comments, and a UTF-8
///     BOM.
/// </summary>
/// <remarks>
///     Lines are found and their field names compared as UTF-8 bytes in a pooled buffer; text is decoded once per value,
///     and the
///     <see cref="SseEvent.Data" /> of an event is decoded once when the event is dispatched. One event may hold at most
///     <see cref="DefaultMaxEventBytes" /> bytes of data by default (a longer line, or more lines without a blank line, is
///     refused
///     with <see cref="A2AErrorCode.InvalidAgentResponse" />), so a hostile agent cannot exhaust memory. The stream is
///     disposed when reading ends.
/// </remarks>
public static class SseReader
{
    /// <summary>
    ///     The most data bytes one event may hold unless a caller says otherwise: 16 MiB, the size limit of an agent
    ///     response.
    /// </summary>
    public const int DefaultMaxEventBytes = 16 * 1024 * 1024;

    private const int InitialBufferSize = 4096;
    private const int FieldPrefix = 6; // "data: ": a line of exactly the limit in data is this much longer
    private static ReadOnlySpan<byte> Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>
    ///     Reads events from <paramref name="stream" /> until it ends. An event may hold at most
    ///     <see cref="DefaultMaxEventBytes" /> bytes of data.
    /// </summary>
    /// <exception cref="A2AException"><see cref="A2AErrorCode.InvalidAgentResponse" /> when an event is larger than the limit.</exception>
#pragma warning disable RS0027 // shipped with an optional token; the overload below must not take one (RS0026 would follow)
    public static IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, CancellationToken cancellationToken = default) =>
        ReadCoreAsync(stream, DefaultMaxEventBytes, cancellationToken);
#pragma warning restore RS0027

    /// <summary>
    ///     Reads events from <paramref name="stream" /> until it ends. An event may hold at most
    ///     <paramref name="maxEventBytes" /> bytes
    ///     of data (the lines joined with <c>\n</c>). A longer line, or more lines without a blank line, ends the read with
    ///     <see cref="A2AErrorCode.InvalidAgentResponse" />, before the memory for it is taken.
    /// </summary>
    /// <param name="stream">The stream to read. It is disposed when reading ends.</param>
    /// <param name="maxEventBytes">The most data bytes one event may hold. At least 1.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="maxEventBytes" /> is below 1. Thrown by this call, not by
    ///     the first read.
    /// </exception>
    /// <exception cref="A2AException"><see cref="A2AErrorCode.InvalidAgentResponse" /> when an event is larger than the limit.</exception>
    public static IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, int maxEventBytes,
        CancellationToken cancellationToken)
    {
        if (maxEventBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxEventBytes), maxEventBytes,
                "The event size limit must be at least 1.");
        return ReadCoreAsync(stream, maxEventBytes, cancellationToken);
    }

    private static A2AException TooLarge(int maxEventBytes)
    {
        return new A2AException(A2AErrorCode.InvalidAgentResponse,
            $"The agent sent a server-sent event larger than the limit of {maxEventBytes} bytes.");
    }

    private static IAsyncEnumerable<SseEvent> ReadCoreAsync(Stream stream, int maxEventBytes,
        CancellationToken cancellationToken) =>
        ReadEventsAsync(stream, maxEventBytes, null, TimeProvider.System, cancellationToken);

    /// <summary>
    ///     <see cref="ReadAsync(Stream, int, CancellationToken)" /> with the keep-alive timeout of
    ///     <see cref="ReadFramesAsync(Stream, int, TimeSpan?, TimeProvider, CancellationToken)" />.
    /// </summary>
    internal static async IAsyncEnumerable<SseEvent> ReadEventsAsync(Stream stream, int maxEventBytes,
        TimeSpan? keepAliveTimeout, TimeProvider timeProvider, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in ReadFramesAsync(stream, maxEventBytes, keepAliveTimeout, timeProvider,
                           cancellationToken).ConfigureAwait(false))
            yield return frame.ToEvent();
    }

    /// <summary>
    ///     Reads the events of <paramref name="stream" /> as <see cref="SseFrame" />s: the data stays UTF-8 bytes in the
    ///     reader's
    ///     buffer. The same frame instance is returned every time and its data is valid only until the next read, so a caller
    ///     uses it at once. The transports parse JSON straight from it; <see cref="ReadAsync(Stream, CancellationToken)" />
    ///     decodes it to a <see cref="SseEvent" />.
    /// </summary>
    internal static IAsyncEnumerable<SseFrame> ReadFramesAsync(Stream stream, int maxEventBytes,
        CancellationToken cancellationToken) =>
        ReadFramesAsync(stream, maxEventBytes, null, TimeProvider.System, cancellationToken);

    /// <summary>
    ///     <see cref="ReadFramesAsync(Stream, int, CancellationToken)" /> with a keep-alive timeout
    ///     (<see cref="A2AClientOptions.StreamKeepAliveTimeout" />): once the stream has carried a comment line, a read that
    ///     gets no bytes for <paramref name="keepAliveTimeout" /> is aborted and the read fails with an
    ///     <see cref="IOException" />, a dropped connection. Null or infinite turns it off. Caller cancellation stays an
    ///     <see cref="OperationCanceledException" />.
    /// </summary>
    internal static async IAsyncEnumerable<SseFrame> ReadFramesAsync(Stream stream, int maxEventBytes,
        TimeSpan? keepAliveTimeout, TimeProvider timeProvider, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (keepAliveTimeout == Timeout.InfiniteTimeSpan) keepAliveTimeout = null;
        StreamIdleTimer? idle = null; // created when the first comment arrives: one per stream
        var buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        var parser = new LineParser(maxEventBytes);
        var used = 0; // high-water mark of filled bytes, cleared before the buffer goes back to the pool
        try
        {
            int start = 0, end = 0;
            var eof = false;
            var atStart = true; // a BOM is only skipped at the very start of the stream
            var skipLf = false; // the last line ended with CR: a LF right after it belongs to the same line break
            var scanned = 0; // bytes after start already searched for a line break: a long line is searched once, not per read
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

                    var newline = buffer.AsSpan(start + scanned, end - start - scanned)
                        .IndexOfAny((byte)'\r', (byte)'\n');
                    if (newline >= 0)
                    {
                        newline += scanned;
                        scanned = 0;
                        cancellationToken.ThrowIfCancellationRequested();
                        var lineStart = start;
                        var terminator = buffer[start + newline];
                        start += newline + 1;
                        if (terminator == (byte)'\r') skipLf = true;
                        if (parser.Process(buffer.AsSpan(lineStart, newline))) yield return parser.Frame;
                        continue;
                    }

                    // Relative to start, so it holds when the partial line is moved to the front or the buffer grows.
                    scanned = end - start;
                    // End of stream: per spec, an incomplete line or event is discarded.
                    if (eof) yield break;
                    // A line that is still open and already longer than any allowed line: refuse it before the buffer grows.
                    if (end - start > (long)maxEventBytes + FieldPrefix) throw TooLarge(maxEventBytes);
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
                    buffer.AsSpan(0, used).Clear();
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                    used = end;
                }

                cancellationToken.ThrowIfCancellationRequested();
                // A peer that has sent a keep-alive keeps sending them: from then on, silence means a dead connection.
                if (idle is null && keepAliveTimeout is { } timeout && parser.SawComment)
                    idle = new StreamIdleTimer(timeout, timeProvider, cancellationToken);
                int read;
                if (idle is null)
                {
                    read = await stream.ReadAsync(buffer.AsMemory(end), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        read = await stream.ReadAsync(buffer.AsMemory(end), idle.Arm()).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (idle.Expired)
                    {
                        throw idle.Error(ex);
                    }
                    catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested &&
                                                                ex.CancellationToken != cancellationToken)
                    {
                        // The read saw the linked token: report the caller's, as a read without the timer does.
                        throw new OperationCanceledException(ex.Message, ex, cancellationToken);
                    }
                    finally
                    {
                        idle.Disarm();
                    }
                }

                if (read == 0) eof = true;
                else end += read;
                if (end > used) used = end;
            }
        }
        finally
        {
            idle?.Dispose();
            buffer.AsSpan(0, used).Clear(); // event payloads can carry secrets; do not leave them in the shared pool
            ArrayPool<byte>.Shared.Return(buffer);
            parser.Dispose();
            stream.Dispose();
        }
    }

    /// <summary>The per-stream field state; turns lines into events.</summary>
    private sealed class LineParser(int maxEventBytes) : IDisposable
    {
        private byte[] _data = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        private int _dataLength;
        private int _dataUsed; // high-water mark, cleared before the buffer goes back to the pool
        private string? _eventType;
        private string? _frameId;
        private bool _hasData;
        private string? _lastEventType;
        private string? _lastId;

        /// <summary>True once a comment line (a keep-alive) has been read.</summary>
        public bool SawComment { get; private set; }

        /// <summary>The event the last <see cref="Process" /> call dispatched. Reused; its data is valid until the next call.</summary>
        public SseFrame Frame { get; } = new();

        public void Dispose()
        {
            _data.AsSpan(0, Math.Max(_dataUsed, _dataLength)).Clear();
            ArrayPool<byte>.Shared.Return(_data);
            _data = [];
        }

        /// <summary>
        ///     Applies one line (without its terminator). True when a blank line dispatched an event into
        ///     <see cref="Frame" />.
        /// </summary>
        public bool Process(ReadOnlySpan<byte> line)
        {
            if (line.IsEmpty)
            {
                if (_dataLength > _dataUsed) _dataUsed = _dataLength;
                var dispatched = _hasData;
                if (dispatched)
                {
                    // The bytes stay in _data until the next line is applied, which is after the caller used the frame.
                    Frame.Data = _data.AsMemory(0, _dataLength);
                    Frame.EventType = _eventType;
                    Frame.Id = _lastId;
                    Frame.FrameId = _frameId;
                }

                _dataLength = 0;
                _hasData = false;
                _eventType = null;
                _frameId = null;
                return dispatched;
            }

            if (line[0] == (byte)':')
            {
                SawComment = true; // comment / keep-alive
                return false;
            }

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
                // The same name as the last one (a stream usually repeats one or two): no new string.
                _eventType = _lastEventType is { } last && Matches(value, last) ? last : Encoding.UTF8.GetString(value);
                _lastEventType = _eventType;
            }
            else if (field.SequenceEqual("id"u8))
            {
                if (value.IndexOf((byte)0) < 0) _lastId = _frameId = Encoding.UTF8.GetString(value);
            }

            // "retry" and unknown fields are ignored.
            return false;
        }

        /// <summary>
        ///     True when <paramref name="value" /> is the UTF-8 of <paramref name="text" /> (ASCII compare; anything else is
        ///     decoded).
        /// </summary>
        private static bool Matches(ReadOnlySpan<byte> value, string text)
        {
            if (value.Length != text.Length) return false;
            for (var i = 0; i < value.Length; i++)
                if (value[i] != text[i] || value[i] > 0x7F)
                    return false;
            return true;
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
            // Counts the joining \n too. Long math: the sum can pass int.MaxValue.
            if ((long)_dataLength + more > maxEventBytes) throw TooLarge(maxEventBytes);
            if (_dataLength + more <= _data.Length) return;
            var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(_data.Length * 2, _dataLength + more));
            _data.AsSpan(0, _dataLength).CopyTo(bigger);
            _data.AsSpan(0, Math.Max(_dataUsed, _dataLength)).Clear();
            ArrayPool<byte>.Shared.Return(_data);
            _data = bigger;
            _dataUsed = _dataLength;
        }
    }
}