using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.Channels;
using A2A.Client;
using A2A.Client.Sse;
using A2A.Client.Transports;

namespace A2A.Tests;

/// <summary>
///     <see cref="A2AClientOptions.StreamKeepAliveTimeout" />: a stream whose server has sent keep-alives and then goes
///     silent without closing is treated as a dropped connection, and the resumable calls reconnect from the last event id.
/// </summary>
public class StreamKeepAliveTimeoutTests
{
    private const string Endpoint = "http://localhost/a2a";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Timeout60 = TimeSpan.FromSeconds(60);

    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    /// <summary>A response body fed chunk by chunk by the test; a read waits for the next chunk and honors its token.</summary>
    private sealed class LiveStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[] _current = [];
        private int _pos;
        private int _reads;

        private int _started;

        /// <summary>Reads that are waiting now (0 or 1).</summary>
        public int WaitingReads => Volatile.Read(ref _reads);

        /// <summary>Reads that have waited for a chunk so far.</summary>
        public int ReadsStarted => Volatile.Read(ref _started);

        public bool ReadWasCancelled { get; private set; }

        public void Send(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        public void End() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pos >= _current.Length)
            {
                Interlocked.Increment(ref _reads);
                Interlocked.Increment(ref _started);
                try
                {
                    if (!await _chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
                    _current = await _chunks.Reader.ReadAsync(cancellationToken);
                    _pos = 0;
                }
                catch (OperationCanceledException)
                {
                    ReadWasCancelled = true;
                    throw;
                }
                finally
                {
                    Interlocked.Decrement(ref _reads);
                }
            }

            var n = Math.Min(buffer.Length, _current.Length - _pos);
            _current.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Waits (in real time) until <paramref name="condition" /> holds.</summary>
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not hold in time.");
            await Task.Delay(5);
        }
    }

    private static IAsyncEnumerator<SseFrame> Frames(LiveStream stream, TimeSpan? timeout, TimeProvider time,
        CancellationToken ct = default) =>
        SseReader.ReadFramesAsync(stream, SseReader.DefaultMaxEventBytes, timeout, time, ct).GetAsyncEnumerator(ct);

    [Fact]
    public async Task Silence_after_a_keep_alive_fails_the_read_as_a_dropped_connection()
    {
        var time = new FakeTimeProvider();
        var stream = new LiveStream();
        await using var frames = Frames(stream, Timeout60, time);
        stream.Send(": ka\n\ndata: one\n\n");
        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.Equal("one", frames.Current.DataText);

        var next = frames.MoveNextAsync().AsTask();
        await Until(() => stream.WaitingReads == 1 && time.PendingTimers == 1);
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(next.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<IOException>(() => next.WaitAsync(Wait));
        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.Contains("60", error.Message);
        Assert.True(stream.ReadWasCancelled); // the blocked read was aborted, not left behind
        Assert.Equal(0, time.PendingTimers);
    }

    /// <summary>Counts the timers made through it.</summary>
    private sealed class CountingTime(TimeProvider inner) : TimeProvider
    {
        private int _created;
        public int Created => Volatile.Read(ref _created);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _created);
            return inner.CreateTimer(callback, state, dueTime, period);
        }
    }

    [Fact]
    public async Task Keep_alive_comments_keep_the_stream_alive_past_the_timeout()
    {
        var time = new FakeTimeProvider();
        var counting = new CountingTime(time);
        var stream = new LiveStream();
        await using var frames = Frames(stream, Timeout60, counting);
        stream.Send(": ka\n\n");
        var next = frames.MoveNextAsync().AsTask();
        for (var i = 0; i < 5; i++)
        {
            var reads = i + 1; // the keep-alive just sent was read, and the next read waits
            await Until(() => stream.ReadsStarted > reads && stream.WaitingReads == 1 && time.PendingTimers == 1);
            time.Advance(TimeSpan.FromSeconds(45)); // 5 x 45 s: well past 60 s in all, never 60 s without bytes
            Assert.False(next.IsCompleted);
            stream.Send(": ka\n\n");
        }

        stream.Send("data: late\n\n");
        Assert.True(await next.WaitAsync(Wait));
        Assert.Equal("late", frames.Current.DataText);
        Assert.Equal(1, counting.Created); // one timer per stream, re-armed, not one per read
        Assert.Equal(0, time.PendingTimers); // and stopped while the caller holds the event
    }

    [Fact]
    public async Task Events_also_reset_the_timer()
    {
        var time = new FakeTimeProvider();
        var stream = new LiveStream();
        await using var frames = Frames(stream, Timeout60, time);
        stream.Send(": ka\n\n");
        for (var i = 0; i < 3; i++)
        {
            var next = frames.MoveNextAsync().AsTask();
            await Until(() => stream.WaitingReads == 1 && time.PendingTimers == 1);
            time.Advance(TimeSpan.FromSeconds(50));
            stream.Send($"data: e{i}\n\n");
            Assert.True(await next.WaitAsync(Wait));
            Assert.Equal($"e{i}", frames.Current.DataText);
        }
    }

    [Fact]
    public async Task The_timer_does_not_run_while_the_consumer_holds_a_frame()
    {
        var time = new FakeTimeProvider();
        var stream = new LiveStream();
        await using var frames = Frames(stream, Timeout60, time);
        stream.Send(": ka\n\ndata: one\n\n");
        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(Wait));

        time.Advance(TimeSpan.FromMinutes(10)); // a slow consumer is not a silent server
        Assert.Equal(0, time.PendingTimers);
        stream.Send("data: two\n\n");
        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.Equal("two", frames.Current.DataText);
    }

    [Fact]
    public async Task Caller_cancellation_stays_OperationCanceledException()
    {
        var time = new FakeTimeProvider();
        var stream = new LiveStream();
        using var cts = new CancellationTokenSource();
        await using var frames = Frames(stream, Timeout60, time, cts.Token);
        stream.Send(": ka\n\n");
        var next = frames.MoveNextAsync().AsTask();
        await Until(() => stream.WaitingReads == 1 && time.PendingTimers == 1);

        cts.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(Wait));
        Assert.Equal(cts.Token, error.CancellationToken);
    }

    [Fact]
    public async Task Caller_cancellation_wins_when_the_timer_also_fired()
    {
        var time = new FakeTimeProvider();
        var stream = new LiveStream();
        using var cts = new CancellationTokenSource();
        await using var frames = Frames(stream, Timeout60, time, cts.Token);
        stream.Send(": ka\n\n");
        var next = frames.MoveNextAsync().AsTask();
        await Until(() => stream.WaitingReads == 1 && time.PendingTimers == 1);

        cts.Cancel();
        time.Advance(Timeout60);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(Wait));
    }

    [Fact]
    public async Task Off_keeps_the_old_behavior()
    {
        var time = new FakeTimeProvider();
        var stream = new LiveStream();
        using var cts = new CancellationTokenSource();
        await using var frames = Frames(stream, null, time, cts.Token);
        stream.Send(": ka\n\ndata: one\n\n");
        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(Wait));

        var next = frames.MoveNextAsync().AsTask();
        await Until(() => stream.WaitingReads == 1);
        time.Advance(TimeSpan.FromHours(3));
        await Task.Delay(50);
        Assert.False(next.IsCompleted);
        Assert.Equal(0, time.PendingTimers);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(Wait));
    }

    [Fact]
    public async Task A_peer_that_has_sent_no_keep_alive_is_not_timed_out()
    {
        // A peer that never sends keep-alives (no comment line yet) may stay quiet for as long as its task runs.
        var time = new FakeTimeProvider();
        var stream = new LiveStream();
        await using var frames = Frames(stream, Timeout60, time);
        stream.Send("data: one\n\n");
        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(Wait));

        var next = frames.MoveNextAsync().AsTask();
        await Until(() => stream.WaitingReads == 1);
        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.False(next.IsCompleted);

        stream.Send(": ka\n\n"); // now it has shown it sends keep-alives: the timer applies
        await Until(() => stream.WaitingReads == 1 && time.PendingTimers == 1);
        time.Advance(Timeout60);
        await Assert.ThrowsAsync<IOException>(() => next.WaitAsync(Wait));
    }

    [Fact]
    public void The_option_rejects_zero_and_negative_values()
    {
        var options = new A2AClientOptions();
        Assert.Equal(TimeSpan.FromSeconds(60), options.StreamKeepAliveTimeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.StreamKeepAliveTimeout = TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.StreamKeepAliveTimeout = TimeSpan.FromSeconds(-1));
        options.StreamKeepAliveTimeout = null;
        Assert.Null(options.StreamKeepAliveTimeout);
        options.StreamKeepAliveTimeout = Timeout.InfiniteTimeSpan; // off, like null
        Assert.Equal(Timeout.InfiniteTimeSpan, options.StreamKeepAliveTimeout);
    }

    // --- Through the client: ObserveTaskAsync and SendStreamingMessageAsync reconnect from the last id. ---

    private static string Payload(string binding, string json) =>
        binding == ProtocolBindings.JsonRpc ? $$"""{"jsonrpc":"2.0","id":"x","result":{{json}}}""" : json;

    private static string TaskFrame() =>
        "{\"task\":{\"id\":\"t1\",\"contextId\":\"c1\",\"status\":{\"state\":\"TASK_STATE_WORKING\"}}}";

    private static string StatusFrame(string state) =>
        "{\"statusUpdate\":{\"taskId\":\"t1\",\"contextId\":\"c1\",\"status\":{\"state\":\"" + state + "\"}}}";

    private static string Event(string binding, string json, string id) =>
        $"id: {id}\ndata: {Payload(binding, json)}\n\n";

    private sealed class LiveHandler : HttpMessageHandler
    {
        public ConcurrentQueue<(string? LastEventId, LiveStream Stream)> Calls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var lei = request.Headers.TryGetValues("Last-Event-ID", out var v) ? v.Single() : null;
            var stream = new LiveStream();
            Calls.Enqueue((lei, stream));
            var content = new StreamContent(stream);
            content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static A2AClient NewClient(LiveHandler handler, string binding, TimeProvider time, TimeSpan? timeout)
    {
        var options = new A2AClientOptions { TimeProvider = time, StreamKeepAliveTimeout = timeout };
        var http = new HttpClient(handler);
        IA2ATransport transport = binding == ProtocolBindings.JsonRpc
            ? new JsonRpcTransport(http, new Uri(Endpoint), options)
            : new HttpJsonTransport(http, new Uri(Endpoint), options);
        return new A2AClient(transport, options);
    }

    private static string Describe(StreamResponse e) => e switch
    {
        TaskEvent t => $"task:{t.Task.Status.State}",
        StatusUpdateEvent s => $"status:{s.StatusUpdate.Status.State}",
        _ => e.GetType().Name
    };

    private static async Task<List<string>> Drain(IAsyncEnumerable<StreamResponse> stream, ConcurrentQueue<string> seen)
    {
        await foreach (var e in stream) seen.Enqueue(Describe(e));
        return seen.ToList();
    }

    /// <summary>
    ///     The first connection sends a keep-alive and two events, then goes silent without closing. The client detects the
    ///     silence after the timeout, waits the first backoff, reconnects with Last-Event-ID and gets the rest.
    /// </summary>
    private static async Task SilentConnectionIsResumed(string binding, Func<A2AClient, IAsyncEnumerable<StreamResponse>> call)
    {
        var time = new FakeTimeProvider();
        var handler = new LiveHandler();
        var client = NewClient(handler, binding, time, Timeout60);
        var seen = new ConcurrentQueue<string>();
        var run = Drain(call(client), seen);

        await Until(() => handler.Calls.Count == 1);
        var first = handler.Calls.Single().Stream;
        first.Send(": ka\n\n" + Event(binding, TaskFrame(), "t1:1") + Event(binding, StatusFrame("TASK_STATE_WORKING"), "t1:2"));
        await Until(() => seen.Count == 2 && first.WaitingReads == 1 && time.PendingTimers == 1);

        time.Advance(Timeout60); // the connection is dead: nothing more arrives
        await Until(() => first.ReadWasCancelled && time.PendingTimers == 1); // now the 500 ms backoff
        time.Advance(TimeSpan.FromMilliseconds(500));

        await Until(() => handler.Calls.Count == 2);
        var (lastEventId, second) = handler.Calls.ToArray()[1];
        Assert.Equal("t1:2", lastEventId);
        second.Send(Event(binding, StatusFrame("TASK_STATE_WORKING"), "t1:3") + Event(binding, StatusFrame("TASK_STATE_COMPLETED"), "t1:4"));
        second.End();

        var events = await run.WaitAsync(Wait);
        Assert.Equal(["task:Working", "status:Working", "status:Working", "status:Completed"], events);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public Task ObserveTask_reconnects_from_the_last_event_id_after_silence(string binding) =>
        SilentConnectionIsResumed(binding, c => c.ObserveTaskAsync("t1"));

    [Theory]
    [MemberData(nameof(Bindings))]
    public Task SendStreamingMessage_reconnects_from_the_last_event_id_after_silence(string binding) =>
        SilentConnectionIsResumed(binding, c => c.SendStreamingMessageAsync(new SendMessageRequest
        {
            Message = new Message { Role = Role.User, MessageId = "m1", Parts = [Part.FromText("hi")] }
        }));

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Option_off_keeps_waiting_on_a_silent_connection(string binding)
    {
        var time = new FakeTimeProvider();
        var handler = new LiveHandler();
        var client = NewClient(handler, binding, time, null);
        var seen = new ConcurrentQueue<string>();
        using var cts = new CancellationTokenSource();
        var run = Drain(client.ObserveTaskAsync("t1", cancellationToken: cts.Token), seen);

        await Until(() => handler.Calls.Count == 1);
        var first = handler.Calls.Single().Stream;
        first.Send(": ka\n\n" + Event(binding, TaskFrame(), "t1:1"));
        await Until(() => seen.Count == 1 && first.WaitingReads == 1);
        time.Advance(TimeSpan.FromHours(3));
        await Task.Delay(50);
        Assert.False(run.IsCompleted);
        Assert.Single(handler.Calls);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Wait));
        Assert.Single(handler.Calls); // caller cancellation is not a dropped connection: no reconnect
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Caller_cancellation_of_an_observed_stream_does_not_reconnect(string binding)
    {
        var time = new FakeTimeProvider();
        var handler = new LiveHandler();
        var client = NewClient(handler, binding, time, Timeout60);
        var seen = new ConcurrentQueue<string>();
        using var cts = new CancellationTokenSource();
        var run = Drain(client.ObserveTaskAsync("t1", cancellationToken: cts.Token), seen);

        await Until(() => handler.Calls.Count == 1);
        var first = handler.Calls.Single().Stream;
        first.Send(": ka\n\n" + Event(binding, TaskFrame(), "t1:1"));
        await Until(() => seen.Count == 1 && first.WaitingReads == 1 && time.PendingTimers == 1);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Wait));
        Assert.Single(handler.Calls);
    }
}
