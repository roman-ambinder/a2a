using System.Text;
using A2A.Client.Sse;

namespace A2A.Tests;

public class SseReaderTests
{
    private static async Task<List<SseEvent>> Read(byte[] bytes, int chunk = int.MaxValue)
    {
        var events = new List<SseEvent>();
        await foreach (var e in SseReader.ReadAsync(new ChunkedStream(bytes, chunk))) events.Add(e);
        return events;
    }

    private static Task<List<SseEvent>> Read(string text, int chunk = int.MaxValue) => Read(Encoding.UTF8.GetBytes(text), chunk);

    public static TheoryData<int> Chunks => new() { int.MaxValue, 1, 2, 3, 7, 4096 };

    [Theory]
    [MemberData(nameof(Chunks))]
    public async Task Crlf_and_lf_and_multiline_data(int chunk)
    {
        var big = new string('x', 70_000);
        var text =
            ": comment\r\n" +
            "id: 1\r\ndata: a\r\ndata: b\r\n\r\n" +                 // CRLF, two data lines joined with \n
            ": keep-alive\n\n" +                                   // a comment-only frame dispatches nothing
            "event: x\nid: 7\ndata:c\n\n" +                        // LF, no space after the colon
            $"data: {big}\n\n" +                                   // one 70,000-byte line
            "data: lone cr\r\r" +                                  // a bare CR ends a line
            "data:  two spaces\ndata\n\n" +                        // only the first space is stripped; a bare field name is empty data
            "data: incomplete";                                    // no blank line: dropped at the end of the stream
        var events = await Read(text, chunk);

        Assert.Equal(5, events.Count);
        Assert.Equal(new SseEvent("a\nb", null, "1"), events[0]);
        Assert.Equal("1", events[0].FrameId);
        Assert.Equal(new SseEvent("c", "x", "7"), events[1]);
        Assert.Equal("7", events[1].FrameId);
        Assert.Equal(big, events[2].Data);
        Assert.Equal("7", events[2].Id);                           // the last id carries over to later events
        Assert.Null(events[2].FrameId);                            // but this frame had no id line
        Assert.Null(events[2].EventType);                          // the event type does not carry over
        Assert.Equal("lone cr", events[3].Data);
        Assert.Equal(" two spaces\n", events[4].Data);
    }

    // Chunks of 1 and 2 bytes split the BOM across reads (the former StreamReader-based reader missed it then).
    [Theory]
    [MemberData(nameof(Chunks))]
    public async Task Bom_at_the_start_is_skipped_and_utf8_survives_chunk_boundaries(int chunk)
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("data: héllo wörld ✓ 🎉\nid: ü\n\n")).ToArray();
        var events = await Read(bytes, chunk);
        var e = Assert.Single(events);
        Assert.Equal("héllo wörld ✓ 🎉", e.Data);
        Assert.Equal("ü", e.FrameId);
    }

    [Fact]
    public async Task Id_without_data_is_kept_and_id_with_nul_is_ignored()
    {
        var events = await Read("id: 5\nevent: lost\n\ndata: a\n\nid: bad\0id\ndata: b\n\n");
        Assert.Equal(2, events.Count);
        Assert.Equal(new SseEvent("a", null, "5"), events[0]);    // id survives a frame without data; the event type does not
        Assert.Null(events[0].FrameId);
        Assert.Equal(new SseEvent("b", null, "5"), events[1]);    // an id containing NUL is ignored
        Assert.Null(events[1].FrameId);
    }

    [Fact]
    public async Task Unknown_fields_and_retry_are_ignored_and_empty_data_dispatches()
    {
        var events = await Read("retry: 100\nfoo: bar\ndata:\n\nevent: e\n\n");
        var e = Assert.Single(events);
        Assert.Equal("", e.Data);
    }

    [Fact]
    public async Task Trailing_cr_at_the_end_dispatches()
    {
        var events = await Read("data: x\n\r");
        Assert.Equal("x", Assert.Single(events).Data);
    }

    [Fact]
    public async Task Stream_is_disposed_when_reading_ends()
    {
        var stream = new ChunkedStream(Encoding.UTF8.GetBytes("data: a\n\n"), int.MaxValue);
        await foreach (var _ in SseReader.ReadAsync(stream)) { }
        Assert.True(stream.Disposed);
    }

    private static async Task<List<SseEvent>> Read(string text, int maxEventBytes, int chunk)
    {
        var events = new List<SseEvent>();
        await foreach (var e in SseReader.ReadAsync(new ChunkedStream(Encoding.UTF8.GetBytes(text), chunk), maxEventBytes, CancellationToken.None)) events.Add(e);
        return events;
    }

    private static async Task<A2AException> TooLarge(Func<Task> read)
    {
        var ex = await Assert.ThrowsAsync<A2AException>(read);
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
        Assert.DoesNotContain("SECRET", ex.Message); // never echoes stream data
        return ex;
    }

    [Fact]
    public void Default_limit_is_16_MiB() => Assert.Equal(16 * 1024 * 1024, SseReader.DefaultMaxEventBytes);

    [Fact]
    public void Limit_below_one_is_refused_by_the_call()
    {
        var stream = new ChunkedStream([], int.MaxValue);
        Assert.Throws<ArgumentOutOfRangeException>(() => SseReader.ReadAsync(stream, 0, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Chunks))]
    public async Task Event_of_exactly_the_limit_is_delivered(int chunk)
    {
        var events = await Read($"data: {new string('x', 1000)}\n\n", 1000, chunk);
        Assert.Equal(1000, Assert.Single(events).Data.Length);
    }

    [Theory]
    [MemberData(nameof(Chunks))]
    public async Task Event_one_byte_over_the_limit_is_refused(int chunk)
    {
        var text = $"data: {new string('x', 1001)}\n\n";
        var ex = await TooLarge(() => Read(text, 1000, chunk));
        Assert.Contains("1000", ex.Message);
    }

    [Fact]
    public async Task Joined_lines_count_with_their_newline()
    {
        Assert.Equal("aaaa\nbbbb", Assert.Single(await Read("data: aaaa\ndata: bbbb\n\n", 9, int.MaxValue)).Data);
        await TooLarge(() => Read("data: aaaa\ndata: bbbb\n\n", 8, int.MaxValue));
    }

    [Fact]
    public async Task Endless_line_without_a_newline_is_refused_after_a_bounded_read()
    {
        const int limit = 64 * 1024;
        var stream = new EndlessStream("data: SECRET"u8.ToArray(), 4096);
        await TooLarge(async () =>
        {
            await foreach (var _ in SseReader.ReadAsync(stream, limit, CancellationToken.None)) { }
        });
        Assert.True(stream.BytesRead > limit, $"read only {stream.BytesRead}");
        Assert.True(stream.BytesRead <= (2L * limit) + 4096, $"read {stream.BytesRead} bytes");
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Many_short_data_lines_without_a_blank_line_are_refused()
    {
        var text = string.Concat(Enumerable.Repeat("data: SECRET\n", 10_000));
        await TooLarge(() => Read(text, 1000, 4096));
    }

    [Fact]
    public async Task Many_comment_lines_do_not_add_up()
    {
        var text = string.Concat(Enumerable.Repeat(": keep-alive\n", 20_000)) + "data: ok\n\n";
        Assert.Equal("ok", Assert.Single(await Read(text, 1000, 4096)).Data);
    }

    [Fact]
    public async Task The_count_starts_again_with_each_event()
    {
        var one = $"data: {new string('x', 60)}\n\n";
        var events = await Read(one + one, 100, 7);
        Assert.Equal(2, events.Count); // 120 bytes together, over the limit of 100, but each event is under it
    }

    /// <summary>A stream that returns <c>head</c> and then the same byte forever, <c>chunk</c> bytes at most per read.</summary>
    private sealed class EndlessStream(byte[] head, int chunk) : Stream
    {
        private long _position;
        public long BytesRead => Volatile.Read(ref _position);
        public bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var n = Math.Min(buffer.Length, chunk);
            for (var i = 0; i < n; i++) buffer[i] = _position + i < head.Length ? head[_position + i] : (byte)'x';
            _position += n;
            return n;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    /// <summary>A read-only stream that returns at most <c>chunk</c> bytes per read.</summary>
    private sealed class ChunkedStream(byte[] data, int chunk) : Stream
    {
        private int _position;
        public bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var n = Math.Min(Math.Min(buffer.Length, chunk), data.Length - _position);
            data.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
