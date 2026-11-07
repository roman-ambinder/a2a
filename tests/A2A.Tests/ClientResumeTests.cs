using System.Collections.Concurrent;
using System.Net;
using System.Text;
using A2A.Client;
using A2A.Client.Streaming;
using A2A.Client.Transports;
using Xunit;

namespace A2A.Tests;

/// <summary>Client stream resume: <c>ObserveTaskAsync</c> and <c>SendStreamingMessageAsync</c> reconnect with <c>Last-Event-ID</c> (design §7.4).</summary>
public class ClientResumeTests
{
    private const string Endpoint = "http://localhost/a2a";

    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    private static string Payload(string binding, string json) =>
        binding == ProtocolBindings.JsonRpc ? $$"""{"jsonrpc":"2.0","id":"x","result":{{json}}}""" : json;

    private static string TaskFrame(string state = "TASK_STATE_WORKING") =>
        "{\"task\":{\"id\":\"t1\",\"contextId\":\"c1\",\"status\":{\"state\":\"" + state + "\"}}}";

    private static string StatusFrame(string state) =>
        "{\"statusUpdate\":{\"taskId\":\"t1\",\"contextId\":\"c1\",\"status\":{\"state\":\"" + state + "\"}}}";

    private sealed record Frame(string Json, string? Id);

    private static readonly Frame[] Run =
    [
        new(TaskFrame(), "t1:1"),
        new(StatusFrame("TASK_STATE_WORKING"), "t1:2"),
        new(StatusFrame("TASK_STATE_WORKING"), "t1:3"),
        new(StatusFrame("TASK_STATE_COMPLETED"), "t1:4"),
    ];

    private static string Sse(string binding, IEnumerable<Frame> frames)
    {
        var sb = new StringBuilder();
        foreach (var f in frames)
        {
            if (f.Id is not null) sb.Append("id: ").Append(f.Id).Append('\n');
            sb.Append("data: ").Append(Payload(binding, f.Json)).Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>Serves the bytes, then fails with an IOException like a dropped connection.</summary>
    private sealed class DroppingStream(byte[] bytes, bool drop) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= bytes.Length)
            {
                if (drop) throw new IOException("connection reset");
                return 0;
            }
            var n = Math.Min(count, bytes.Length - _pos);
            Array.Copy(bytes, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pos >= bytes.Length)
            {
                if (drop) throw new IOException("connection reset");
                return ValueTask.FromResult(0);
            }
            var n = Math.Min(buffer.Length, bytes.Length - _pos);
            bytes.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return ValueTask.FromResult(n);
        }
    }

    private static HttpResponseMessage SseResponse(string body, bool drop)
    {
        var content = new StreamContent(new DroppingStream(Encoding.UTF8.GetBytes(body), drop));
        content.Headers.ContentType = new("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed record Call(HttpRequestMessage Request, string Body, string? LastEventId, string? Token);

    private sealed class ScriptedHandler(params Func<Call, HttpResponseMessage>[] script) : HttpMessageHandler
    {
        public ConcurrentQueue<Call> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var lei = request.Headers.TryGetValues("Last-Event-ID", out var v) ? v.Single() : null;
            var token = request.Headers.TryGetValues("X-Test-Token", out var t) ? t.Single() : null;
            var call = new Call(request, body, lei, token);
            Calls.Enqueue(call);
            return script[Math.Min(Calls.Count, script.Length) - 1](call);
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        public ConcurrentQueue<TimeSpan> Delays { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                Delays.Enqueue(dueTime);
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }
            return new FakeTimer();
        }

        private sealed class FakeTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static A2AClient NewClient(ScriptedHandler handler, string binding, FakeTimeProvider? time = null)
    {
        var options = new A2AClientOptions { TimeProvider = time ?? new FakeTimeProvider() };
        var http = new HttpClient(handler);
        IA2AClient transport = binding == ProtocolBindings.JsonRpc
            ? new JsonRpcTransport(http, new Uri(Endpoint), options)
            : new HttpJsonTransport(http, new Uri(Endpoint), options);
        return new A2AClient(transport, options);
    }

    private static async Task<List<StreamResponse>> Collect(IAsyncEnumerable<StreamResponse> stream)
    {
        var list = new List<StreamResponse>();
        await foreach (var e in stream) list.Add(e);
        return list;
    }

    private static string Describe(StreamResponse e) => e switch
    {
        TaskEvent t => $"task:{t.Task.Id}:{t.Task.Status.State}",
        StatusUpdateEvent s => $"status:{s.StatusUpdate.Status.State}",
        _ => e.GetType().Name,
    };

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Observe_survives_connection_drop(string binding)
    {
        var plain = NewClient(new ScriptedHandler(_ => SseResponse(Sse(binding, Run), drop: false)), binding);
        var expected = (await Collect(plain.ObserveTaskAsync("t1"))).Select(Describe).ToList();

        var time = new FakeTimeProvider();
        // The second connection replays frame 2 as well: the client must not yield it twice.
        var handler = new ScriptedHandler(
            _ => SseResponse(Sse(binding, Run.Take(2)), drop: true),
            _ => SseResponse(Sse(binding, Run.Skip(1)), drop: false));
        var client = NewClient(handler, binding, time);

        var actual = (await Collect(client.ObserveTaskAsync("t1", new A2ARequestOptions { Headers = { ["X-Test-Token"] = "abc" } }))).Select(Describe).ToList();

        Assert.Equal(expected, actual);
        var calls = handler.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Null(calls[0].LastEventId);
        Assert.Equal("t1:2", calls[1].LastEventId);
        Assert.Equal("abc", calls[0].Token);
        Assert.Equal("abc", calls[1].Token); // same per-call headers on the reconnect
        Assert.Equal(TimeSpan.FromMilliseconds(500), Assert.Single(time.Delays));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Observe_ignores_id_less_frames_when_tracking_the_cursor(string binding)
    {
        var frames = new[] { new Frame(TaskFrame(), "t1:1"), new Frame(StatusFrame("TASK_STATE_WORKING"), null) };
        var handler = new ScriptedHandler(
            _ => SseResponse(Sse(binding, frames), drop: true),
            _ => SseResponse(Sse(binding, [Run[3]]), drop: false));
        var client = NewClient(handler, binding);

        var events = await Collect(client.ObserveTaskAsync("t1"));

        Assert.Equal(3, events.Count);
        Assert.Equal("t1:1", handler.Calls.ToArray()[1].LastEventId);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Observe_finished_task_yields_final_snapshot(string binding)
    {
        string Unsupported() => binding == ProtocolBindings.JsonRpc
            ? """{"jsonrpc":"2.0","id":"x","error":{"code":-32004,"message":"terminal"}}"""
            : """{"error":{"code":400,"status":"FAILED_PRECONDITION","message":"terminal","details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"UNSUPPORTED_OPERATION","domain":"a2a-protocol.org"}]}}""";
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(binding == ProtocolBindings.JsonRpc ? HttpStatusCode.OK : HttpStatusCode.BadRequest) { Content = new StringContent(Unsupported(), Encoding.UTF8, "application/json") },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Payload(binding, TaskFrame("TASK_STATE_COMPLETED")[8..^1]), Encoding.UTF8, "application/json") });
        var client = NewClient(handler, binding);

        var events = await Collect(client.ObserveTaskAsync("t1"));

        var only = Assert.IsType<TaskEvent>(Assert.Single(events));
        Assert.Equal(TaskState.Completed, only.Task.Status.State);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Observe_gives_up_after_five_failures(string binding)
    {
        var time = new FakeTimeProvider();
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("down"));
        var client = NewClient(handler, binding, time);

        await Assert.ThrowsAsync<HttpRequestException>(() => Collect(client.ObserveTaskAsync("t1")));

        Assert.Equal(6, handler.Calls.Count); // the first connect plus five reconnects
        Assert.Equal([500, 1000, 2000, 4000, 8000], time.Delays.Select(d => (int)d.TotalMilliseconds).ToArray());
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task SendStreaming_resumes_through_subscribe_once_the_task_id_is_known(string binding)
    {
        var handler = new ScriptedHandler(
            _ => SseResponse(Sse(binding, Run.Take(2)), drop: true),
            _ => SseResponse(Sse(binding, Run.Skip(2)), drop: false));
        var client = NewClient(handler, binding);

        var events = await Collect(client.SendStreamingMessageAsync(Message.FromUser("hi"), options: new A2ARequestOptions { Headers = { ["X-Test-Token"] = "k" } }));

        Assert.Equal(4, events.Count);
        var calls = handler.Calls.ToArray();
        Assert.Equal("t1:2", calls[1].LastEventId);
        Assert.Equal("k", calls[1].Token);
        Assert.Contains(binding == ProtocolBindings.JsonRpc ? "SubscribeToTask" : ":subscribe", binding == ProtocolBindings.JsonRpc ? calls[1].Body : calls[1].Request.RequestUri!.AbsoluteUri);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task SendStreaming_drop_before_first_event_is_not_resumed(string binding)
    {
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("down"));
        var client = NewClient(handler, binding);

        await Assert.ThrowsAsync<HttpRequestException>(() => Collect(client.SendStreamingMessageAsync(Message.FromUser("hi"))));

        Assert.Single(handler.Calls);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Cancellation_is_not_retried(string binding)
    {
        var handler = new ScriptedHandler(_ => SseResponse(Sse(binding, Run.Take(1)), drop: true));
        var client = NewClient(handler, binding);
        using var cts = new CancellationTokenSource();
        var seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.ObserveTaskAsync("t1", cancellationToken: cts.Token))
            {
                seen++;
                cts.Cancel();
            }
        });

        Assert.Equal(1, seen);
        Assert.Single(handler.Calls);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Observe_resumes_after_clean_close_mid_task(string binding)
    {
        var handler = new ScriptedHandler(
            _ => SseResponse(Sse(binding, Run.Take(2)), drop: false),
            _ => SseResponse(Sse(binding, Run.Skip(2)), drop: false));
        var client = NewClient(handler, binding);

        var events = await Collect(client.ObserveTaskAsync("t1"));

        Assert.Equal(Run.Select(f => Describe(Parse(f))).ToList(), events.Select(Describe).ToList());
        Assert.Equal("t1:2", handler.Calls.ToArray()[1].LastEventId);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Observe_gives_up_when_server_always_closes_without_events(string binding)
    {
        var time = new FakeTimeProvider();
        var handler = new ScriptedHandler(_ => SseResponse("", drop: false));
        var client = NewClient(handler, binding, time);

        await Assert.ThrowsAsync<IOException>(() => Collect(client.ObserveTaskAsync("t1")));

        Assert.Equal(6, handler.Calls.Count);
        Assert.Equal(5, time.Delays.Count);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Clean_close_after_terminal_event_makes_no_extra_request(string binding)
    {
        var handler = new ScriptedHandler(_ => SseResponse(Sse(binding, Run), drop: false));
        var client = NewClient(handler, binding);

        Assert.Equal(4, (await Collect(client.ObserveTaskAsync("t1"))).Count);

        Assert.Single(handler.Calls);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Resume_denied_after_clean_close_surfaces_the_error(string binding)
    {
        var handler = new ScriptedHandler(
            _ => SseResponse(Sse(binding, Run.Take(2)), drop: false),
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("", Encoding.UTF8, "text/plain") });
        var client = NewClient(handler, binding);

        var ex = await Assert.ThrowsAsync<A2AException>(() => Collect(client.ObserveTaskAsync("t1")));

        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Clean_close_at_input_required_with_ids_is_resumed(string binding)
    {
        var handler = new ScriptedHandler(
            _ => SseResponse(Sse(binding, [new(TaskFrame(), "t1:1"), new(StatusFrame("TASK_STATE_INPUT_REQUIRED"), "t1:2")]), drop: false),
            _ => SseResponse(Sse(binding, [Run[3] with { Id = "t1:3" }]), drop: false));
        var client = NewClient(handler, binding);

        var events = await Collect(client.ObserveTaskAsync("t1"));

        Assert.Equal(3, events.Count);
        Assert.Equal("t1:2", handler.Calls.ToArray()[1].LastEventId);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Clean_close_at_input_required_from_a_peer_without_ids_ends(string binding)
    {
        var handler = new ScriptedHandler(
            _ => SseResponse(Sse(binding, [new(TaskFrame(), null), new(StatusFrame("TASK_STATE_INPUT_REQUIRED"), null)]), drop: false));
        var client = NewClient(handler, binding);

        Assert.Equal(2, (await Collect(client.ObserveTaskAsync("t1"))).Count);

        Assert.Single(handler.Calls);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Reset_after_the_terminal_event_does_not_reconnect(string binding)
    {
        var handler = new ScriptedHandler(_ => SseResponse(Sse(binding, Run), drop: true));
        var client = NewClient(handler, binding);

        Assert.Equal(4, (await Collect(client.ObserveTaskAsync("t1"))).Count);

        Assert.Single(handler.Calls);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Cursor_for_another_task_is_not_tracked(string binding)
    {
        var handler = new ScriptedHandler(
            _ => SseResponse(Sse(binding, [new(TaskFrame(), "other:5")]), drop: true),
            _ => SseResponse(Sse(binding, Run), drop: false));
        var client = NewClient(handler, binding);

        await Collect(client.ObserveTaskAsync("t1"));

        Assert.Null(handler.Calls.ToArray()[1].LastEventId);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task UnsupportedOperation_on_a_running_task_is_rethrown(string binding)
    {
        string Unsupported() => binding == ProtocolBindings.JsonRpc
            ? """{"jsonrpc":"2.0","id":"x","error":{"code":-32004,"message":"nope"}}"""
            : """{"error":{"code":400,"status":"FAILED_PRECONDITION","message":"nope","details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"UNSUPPORTED_OPERATION","domain":"a2a-protocol.org"}]}}""";
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(binding == ProtocolBindings.JsonRpc ? HttpStatusCode.OK : HttpStatusCode.BadRequest) { Content = new StringContent(Unsupported(), Encoding.UTF8, "application/json") },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Payload(binding, TaskFrame("TASK_STATE_WORKING")[8..^1]), Encoding.UTF8, "application/json") });
        var client = NewClient(handler, binding);

        var ex = await Assert.ThrowsAsync<A2AException>(() => Collect(client.ObserveTaskAsync("t1")));

        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Budget_resets_after_progress(string binding)
    {
        // Eight drops in a row, each delivering one new sequence: more than five in total, but never five without progress.
        var script = new List<Func<Call, HttpResponseMessage>>();
        for (var n = 1; n <= 8; n++)
        {
            var id = $"t1:{n}";
            script.Add(_ => SseResponse(Sse(binding, [new(n == 1 ? TaskFrame() : StatusFrame("TASK_STATE_WORKING"), id)]), drop: true));
        }
        script.Add(_ => SseResponse(Sse(binding, [Run[3] with { Id = "t1:9" }]), drop: false));
        var handler = new ScriptedHandler([.. script]);
        var time = new FakeTimeProvider();
        var client = NewClient(handler, binding, time);

        Assert.Equal(9, (await Collect(client.ObserveTaskAsync("t1"))).Count);

        Assert.Equal(9, handler.Calls.Count);
        Assert.All(time.Delays, d => Assert.Equal(TimeSpan.FromMilliseconds(500), d)); // the budget restarted every time
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Frames_without_ids_do_not_reset_the_budget(string binding)
    {
        var handler = new ScriptedHandler(_ => SseResponse(Sse(binding, [new(StatusFrame("TASK_STATE_WORKING"), null)]), drop: true));
        var client = NewClient(handler, binding);

        await Assert.ThrowsAsync<IOException>(() => Collect(client.ObserveTaskAsync("t1")));

        Assert.Equal(6, handler.Calls.Count);
    }

    private sealed class ThirdPartyTransport : IA2AClient, IResumableTransport
    {
        public ConcurrentQueue<string?> LastEventIds { get; } = new();

        IAsyncEnumerable<StreamFrame> IResumableTransport.SendStreamingFramesAsync(SendMessageRequest request, A2ARequestOptions? options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        async IAsyncEnumerable<StreamFrame> IResumableTransport.SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options, string? lastEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastEventIds.Enqueue(lastEventId);
            // The second run replays t1:3: the server replays the cursor frame, and the client must drop it.
            var frames = LastEventIds.Count == 1 ? Run.Take(3) : Run.Skip(2);
            foreach (var f in frames) yield return new StreamFrame(Parse(f), f.Id);
            if (LastEventIds.Count == 1) throw new IOException("connection reset");
        }

        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The client must use the resumable path.");

        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ThirdPartyFactory(ThirdPartyTransport transport) : IA2ATransportFactory
    {
        public string ProtocolBinding => "X-THIRD-PARTY";
        public bool CanCreate(AgentInterface agentInterface) => true;
        public IA2AClient Create(AgentInterface agentInterface, HttpClient httpClient, A2AClientOptions options) => transport;
    }

    [Fact]
    public async Task Third_party_resumable_transport_gets_resume()
    {
        // Tests see internals; a third party does not. Check the contract is really public.
        Assert.True(typeof(IResumableTransport).IsPublic);
        Assert.True(typeof(StreamFrame).IsPublic);
        var transport = new ThirdPartyTransport();
        var options = new A2AClientOptions { TimeProvider = new FakeTimeProvider(), SupportedBindings = ["X-THIRD-PARTY"] };
        options.TransportFactories.Add(new ThirdPartyFactory(transport));
        var card = new AgentCard
        {
            Name = "third",
            Description = "third party",
            Version = "1",
            DefaultInputModes = ["text/plain"],
            DefaultOutputModes = ["text/plain"],
            Skills = [],
            Capabilities = new AgentCapabilities { Streaming = true },
            SupportedInterfaces = [new AgentInterface { Url = "x://agent", ProtocolBinding = "X-THIRD-PARTY", ProtocolVersion = "1.0" }],
        };
        var client = A2AClientFactory.Create(card, new HttpClient(), options);

        var events = await Collect(client.ObserveTaskAsync("t1"));

        Assert.Equal(Run.Select(f => Describe(Parse(f))).ToList(), events.Select(Describe).ToList());
        var ids = transport.LastEventIds.ToArray();
        Assert.Equal(2, ids.Length);
        Assert.Null(ids[0]);
        Assert.Equal("t1:3", ids[1]);
    }

    private static StreamResponse Parse(Frame f) => A2A.Json.A2AJson.Deserialize<StreamResponse>(f.Json);
}
