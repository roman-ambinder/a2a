using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using A2A.Client;
using A2A.Client.Resilience;
using A2A.Client.Transports;
using Xunit;

namespace A2A.Tests;

public class CircuitBreakerTests
{
    private const string TaskJson = """{"id":"t1","contextId":"c1","status":{"state":"TASK_STATE_WORKING"}}""";
    private const string TaskWithMessageJson = """{"id":"t1","contextId":"c1","status":{"state":"TASK_STATE_WORKING","message":{"messageId":"m1","role":"ROLE_AGENT","parts":[{"text":"hi"}]}}}""";
    private static readonly TimeSpan Open = TimeSpan.FromSeconds(30);

    /// <summary>Answers each request with <see cref="Behavior"/>. The request number starts at 1 and the body is the JSON-RPC text.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        private int _count;

        public Func<int, string, CancellationToken, Task<HttpResponseMessage>> Behavior { get; set; } = (_, _, _) => Task.FromResult(Ok());

        public int Count => Volatile.Read(ref _count);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var number = Interlocked.Increment(ref _count);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return await Behavior(number, body, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string? retryAfter = null)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter is not null) r.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return r;
    }

    private static HttpResponseMessage Ok(string taskJson = TaskJson) =>
        Json(HttpStatusCode.OK, $$"""{"jsonrpc":"2.0","id":"x","result":{{taskJson}}}""");

    private static HttpResponseMessage Status(HttpStatusCode status, string? retryAfter = null) => Json(status, "", retryAfter);

    private static HttpResponseMessage Sse(params string[] taskJsons) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Concat(taskJsons.Select(t => $$$"""data: {"jsonrpc":"2.0","id":"x","result":{"task":{{{t}}}}}""" + "\n\n")), Encoding.UTF8, "text/event-stream"),
    };

    private static Func<int, string, CancellationToken, Task<HttpResponseMessage>> Always(Func<HttpResponseMessage> make) =>
        (_, _, _) => Task.FromResult(make());

    private static Func<int, string, CancellationToken, Task<HttpResponseMessage>> Throws(Exception error) =>
        (_, _, _) => throw error;

    private sealed record Setup(A2AClient Client, Handler Handler, FakeTimeProvider Time);

    private static Setup Make(int threshold = 2, int maxRetries = 0, string? agent = null, bool breaker = true, Action<A2AClientOptions>? tweak = null)
    {
        var time = new FakeTimeProvider();
        var options = new A2AClientOptions { MaxRetries = maxRetries, TimeProvider = time };
        if (breaker) options.CircuitBreaker = new CircuitBreakerOptions { FailureThreshold = threshold, OpenDuration = Open };
        tweak?.Invoke(options);
        var handler = new Handler();
        var card = agent is null ? null : Card(agent);
        var transport = new JsonRpcTransport(new HttpClient(handler), new Uri("http://agent.test/a2a"), options);
        return new Setup(new A2AClient(transport, options, card), handler, time);
    }

    private static AgentCard Card(string name) => new()
    {
        Name = name,
        Description = "test agent",
        Version = "1",
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills = [],
        Capabilities = new AgentCapabilities { Streaming = true },
        SupportedInterfaces = [new AgentInterface { Url = "http://agent.test/a2a", ProtocolBinding = ProtocolBindings.JsonRpc, ProtocolVersion = "1.0" }],
    };

    private static Task<AgentTask> Get(A2AClient client, string id = "t1", CancellationToken ct = default) =>
        client.GetTaskAsync(new GetTaskRequest { Id = id }, null, ct);

    private static async Task Fails(A2AClient client)
    {
        var ex = await Assert.ThrowsAsync<A2AException>(() => Get(client));
        Assert.IsNotType<CircuitOpenException>(ex);
    }

    private static async Task Succeeds(A2AClient client) => Assert.Equal("t1", (await Get(client)).Id);

    private static async Task Refused(A2AClient client) => await Assert.ThrowsAsync<CircuitOpenException>(() => Get(client));

    /// <summary>Fails <paramref name="count"/> calls with HTTP 503.</summary>
    private static async Task Trip(Setup s, int count)
    {
        s.Handler.Behavior = Always(() => Status(HttpStatusCode.ServiceUnavailable));
        for (var i = 0; i < count; i++) await Fails(s.Client);
    }

    [Fact]
    public async Task Off_by_default()
    {
        var s = Make(breaker: false);
        s.Handler.Behavior = Always(() => Status(HttpStatusCode.ServiceUnavailable));

        for (var i = 0; i < 20; i++) await Fails(s.Client);

        Assert.Equal(20, s.Handler.Count);
        Assert.Null(new A2AClientOptions().CircuitBreaker);
    }

    [Fact]
    public async Task Opens_after_the_threshold_and_sends_nothing_while_open()
    {
        var s = Make(threshold: 3, agent: "Weather Agent");
        await Trip(s, 3);
        Assert.Equal(3, s.Handler.Count);

        s.Time.Advance(TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<CircuitOpenException>(() => Get(s.Client));

        Assert.Equal(3, s.Handler.Count);
        Assert.Equal(TimeSpan.FromSeconds(20), ex.RetryAfter);
        Assert.Contains("Weather Agent", ex.Message);
        Assert.Contains("No request was sent", ex.Message);
        Assert.Contains("20", ex.Message);
    }

    [Fact]
    public async Task Message_says_the_agent_when_the_name_is_not_known()
    {
        var s = Make(threshold: 1);
        await Trip(s, 1);

        var ex = await Assert.ThrowsAsync<CircuitOpenException>(() => Get(s.Client));

        Assert.Contains("the agent", ex.Message);
    }

    [Fact]
    public async Task Below_the_threshold_the_circuit_stays_closed()
    {
        var s = Make(threshold: 3);
        await Trip(s, 2);

        await Fails(s.Client);
        Assert.Equal(3, s.Handler.Count);
        await Refused(s.Client);
    }

    [Fact]
    public async Task A_success_resets_the_count()
    {
        var s = Make(threshold: 2);
        await Trip(s, 1);
        s.Handler.Behavior = Always(() => Ok());
        await Succeeds(s.Client);

        await Trip(s, 1);

        s.Handler.Behavior = Always(() => Ok());
        await Succeeds(s.Client); // still closed
    }

    [Fact]
    public async Task One_call_with_its_retries_counts_once()
    {
        var s = Make(threshold: 2, maxRetries: 2);
        s.Handler.Behavior = Always(() => Status(HttpStatusCode.ServiceUnavailable, retryAfter: "0"));

        await Fails(s.Client);
        await Fails(s.Client);

        Assert.Equal(6, s.Handler.Count);
        await Refused(s.Client);
        Assert.Equal(6, s.Handler.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Answers_of_a_healthy_peer_never_open_the_circuit_and_reset_the_count(HttpStatusCode status)
    {
        var s = Make(threshold: 2);

        for (var i = 0; i < 6; i++)
        {
            await Trip(s, 1);
            s.Handler.Behavior = Always(() => Status(status));
            await Fails(s.Client); // resets the run of failures
        }

        s.Handler.Behavior = Always(() => Ok());
        await Succeeds(s.Client);
        Assert.Equal(12 + 1, s.Handler.Count);
    }

    [Fact]
    public async Task A_failed_task_never_counts()
    {
        var s = Make(threshold: 1);
        s.Handler.Behavior = Always(() => Ok(TaskJson.Replace("WORKING", "FAILED")));

        for (var i = 0; i < 3; i++) Assert.Equal(TaskState.Failed, (await Get(s.Client)).Status.State);

        Assert.Equal(3, s.Handler.Count);
    }

    [Fact]
    public async Task A_task_level_error_never_counts()
    {
        var s = Make(threshold: 1);
        s.Handler.Behavior = Always(() => Json(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":"x","error":{"code":-32001,"message":"no such task"}}"""));

        for (var i = 0; i < 3; i++)
        {
            var ex = await Assert.ThrowsAsync<A2AException>(() => Get(s.Client));
            Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
        }

        Assert.Equal(3, s.Handler.Count);
    }

    [Fact]
    public async Task A_bare_server_error_neither_counts_nor_resets()
    {
        var s = Make(threshold: 2);
        await Trip(s, 1);

        s.Handler.Behavior = Always(() => Status(HttpStatusCode.InternalServerError));
        for (var i = 0; i < 5; i++) await Fails(s.Client); // does not count

        await Trip(s, 1); // the earlier failure was not reset, so this one opens
        await Refused(s.Client);
    }

    [Fact]
    public async Task A_transport_error_counts()
    {
        var s = Make(threshold: 2);
        s.Handler.Behavior = Throws(new HttpRequestException("connection refused"));

        for (var i = 0; i < 2; i++) await Assert.ThrowsAsync<HttpRequestException>(() => Get(s.Client));

        await Refused(s.Client);
    }

    [Fact]
    public async Task A_timeout_counts()
    {
        var s = Make(threshold: 2);
        s.Handler.Behavior = Throws(new TaskCanceledException("t", new TimeoutException()));

        for (var i = 0; i < 2; i++) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Get(s.Client));

        await Refused(s.Client);
    }

    [Fact]
    public async Task A_cancelled_call_neither_counts_nor_resets()
    {
        var s = Make(threshold: 2);
        await Trip(s, 1);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Get(s.Client, ct: cts.Token));
        s.Handler.Behavior = Throws(new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Get(s.Client));

        await Trip(s, 1); // the first failure still stands, so this one opens
        await Refused(s.Client);
    }

    [Fact]
    public async Task Cancelled_calls_alone_never_open_the_circuit()
    {
        var s = Make(threshold: 1);
        s.Handler.Behavior = Throws(new OperationCanceledException());

        for (var i = 0; i < 3; i++) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Get(s.Client));

        Assert.Equal(3, s.Handler.Count);
    }

    [Fact]
    public async Task Half_open_lets_one_probe_through_and_a_success_closes_the_circuit()
    {
        var s = Make(threshold: 1);
        await Trip(s, 1);
        s.Time.Advance(Open);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Handler.Behavior = async (_, _, _) => { started.TrySetResult(); await release.Task; return Ok(); };

        var probe = Get(s.Client);
        await started.Task;
        await Refused(s.Client); // a second call while the probe runs
        Assert.Equal(2, s.Handler.Count);

        release.SetResult();
        Assert.Equal("t1", (await probe).Id);

        for (var i = 0; i < 3; i++) await Succeeds(s.Client);
        Assert.Equal(5, s.Handler.Count);
    }

    [Fact]
    public async Task A_failed_probe_opens_the_circuit_for_a_full_duration()
    {
        var s = Make(threshold: 1);
        await Trip(s, 1);
        s.Time.Advance(Open);

        await Fails(s.Client); // the probe
        Assert.Equal(2, s.Handler.Count);

        var ex = await Assert.ThrowsAsync<CircuitOpenException>(() => Get(s.Client));
        Assert.Equal(Open, ex.RetryAfter);
        s.Time.Advance(Open - TimeSpan.FromSeconds(1));
        await Refused(s.Client);
        s.Time.Advance(TimeSpan.FromSeconds(1));
        await Fails(s.Client); // the next probe is sent
        Assert.Equal(3, s.Handler.Count);
    }

    [Fact]
    public async Task A_cancelled_probe_frees_the_slot()
    {
        var s = Make(threshold: 1);
        await Trip(s, 1);
        s.Time.Advance(Open);
        s.Handler.Behavior = Throws(new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Get(s.Client));

        s.Handler.Behavior = Always(() => Ok());
        await Succeeds(s.Client); // no time passed, and it may probe at once

        Assert.Equal(3, s.Handler.Count);
        await Succeeds(s.Client); // the circuit is closed
    }

    [Fact]
    public async Task A_hung_probe_is_replaced_after_the_open_duration()
    {
        var s = Make(threshold: 1);
        await Trip(s, 1);
        s.Time.Advance(Open);
        var hungStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Handler.Behavior = async (n, _, _) =>
        {
            if (n != 2) return Ok();
            hungStarted.TrySetResult();
            await hang.Task;
            return Status(HttpStatusCode.ServiceUnavailable);
        };

        var hung = Get(s.Client);
        await hungStarted.Task;
        await Refused(s.Client);

        s.Time.Advance(Open);
        await Succeeds(s.Client); // a new probe, and it closes the circuit
        Assert.Equal(3, s.Handler.Count);

        hang.SetResult(); // the old probe ends late: it says nothing now
        await Assert.ThrowsAsync<A2AException>(() => hung);
        for (var i = 0; i < 2; i++) await Succeeds(s.Client);
    }

    [Fact]
    public async Task A_late_failure_of_a_call_from_before_a_state_change_is_ignored()
    {
        var s = Make(threshold: 2);
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failOthers = true;
        s.Handler.Behavior = async (_, body, _) =>
        {
            if (body.Contains("slow"))
            {
                slowStarted.TrySetResult();
                await slowGate.Task;
                return Status(HttpStatusCode.ServiceUnavailable);
            }
            return failOthers ? Status(HttpStatusCode.ServiceUnavailable) : Ok();
        };

        var slow = Get(s.Client, "slow");
        await slowStarted.Task;
        await Fails(s.Client);
        await Fails(s.Client); // open
        await Refused(s.Client);
        s.Time.Advance(Open);
        failOthers = false;
        await Succeeds(s.Client); // half-open, then closed

        slowGate.SetResult();
        await Assert.ThrowsAsync<A2AException>(() => slow); // would be failure number 1 if it counted

        failOthers = true;
        await Fails(s.Client); // number 1 for the new state
        var before = s.Handler.Count;
        await Fails(s.Client); // still sent: the circuit was not open yet
        Assert.Equal(before + 1, s.Handler.Count);
        await Refused(s.Client);
    }

    [Fact]
    public async Task An_exception_from_the_message_hook_never_counts()
    {
        var s = Make(threshold: 1, tweak: o => o.OnMessageReceived = (_, _) => throw new HttpRequestException("hook"));
        s.Handler.Behavior = Always(() => Ok(TaskWithMessageJson));

        for (var i = 0; i < 3; i++) await Assert.ThrowsAsync<HttpRequestException>(() => Get(s.Client));

        Assert.Equal(3, s.Handler.Count);
    }

    [Fact]
    public async Task Validation_neither_counts_nor_is_blocked_by_an_open_circuit()
    {
        var s = Make(threshold: 1);
        await Trip(s, 1);
        await Refused(s.Client);
        var before = s.Handler.Count;

        var ex = await Assert.ThrowsAsync<A2AException>(() => Get(s.Client, ""));

        Assert.IsNotType<CircuitOpenException>(ex);
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal(before, s.Handler.Count);
    }

    [Fact]
    public async Task Every_unary_method_is_guarded()
    {
        var s = Make(threshold: 1);
        await Trip(s, 1);
        var before = s.Handler.Count;
        var c = s.Client;

        await Assert.ThrowsAsync<CircuitOpenException>(() => c.SendMessageAsync("hi"));
        await Assert.ThrowsAsync<CircuitOpenException>(() => c.ListTasksAsync(new ListTasksRequest()));
        await Assert.ThrowsAsync<CircuitOpenException>(() => c.CancelTaskAsync("t1"));
        await Assert.ThrowsAsync<CircuitOpenException>(() => c.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = "t1", Url = "https://hook.test/" }));
        await Assert.ThrowsAsync<CircuitOpenException>(() => c.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = "t1", Id = "p1" }));
        await Assert.ThrowsAsync<CircuitOpenException>(() => c.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = "t1" }));
        await Assert.ThrowsAsync<CircuitOpenException>(() => c.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = "t1", Id = "p1" }));
        await Assert.ThrowsAsync<CircuitOpenException>(() => c.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest()));

        Assert.Equal(before, s.Handler.Count);
    }

    [Fact]
    public async Task A_stream_that_cannot_connect_counts()
    {
        var s = Make(threshold: 2);
        s.Handler.Behavior = Throws(new HttpRequestException("connection refused"));

        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<HttpRequestException>(async () => { await foreach (var _ in s.Client.SubscribeToTaskAsync("t1")) { } });

        await Refused(s.Client);
    }

    [Fact]
    public async Task An_open_circuit_refuses_a_stream_on_the_first_read()
    {
        var s = Make(threshold: 1);
        await Trip(s, 1);

        var stream = s.Client.SubscribeToTaskAsync("t1");
        await Assert.ThrowsAsync<CircuitOpenException>(async () => { await foreach (var _ in stream) { } });

        Assert.Equal(1, s.Handler.Count);
    }

    [Fact]
    public async Task The_first_event_of_a_probe_stream_closes_the_circuit()
    {
        var s = Make(threshold: 1);
        await Trip(s, 1);
        s.Time.Advance(Open);
        s.Handler.Behavior = Always(() => Sse(TaskJson));

        await foreach (var _ in s.Client.SubscribeToTaskAsync("t1")) break;

        s.Handler.Behavior = Always(() => Ok());
        await Succeeds(s.Client); // closed: not refused as a second probe
    }

    [Fact]
    public async Task An_error_after_the_first_event_is_not_counted()
    {
        var s = Make(threshold: 1);
        s.Handler.Behavior = Always(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BreakingStream(Encoding.UTF8.GetBytes($$$"""data: {"jsonrpc":"2.0","id":"x","result":{"task":{{{TaskJson}}}}}""" + "\n\n")))
            {
                Headers = { ContentType = new("text/event-stream") },
            },
        });

        var seen = 0;
        await Assert.ThrowsAnyAsync<IOException>(async () => { await foreach (var _ in s.Client.SubscribeToTaskAsync("t1")) seen++; });
        Assert.Equal(1, seen);

        s.Handler.Behavior = Always(() => Ok());
        await Succeeds(s.Client);
    }

    [Fact]
    public async Task State_changes_are_counted_with_the_agent_name()
    {
        var agent = "metric-agent-" + Guid.NewGuid().ToString("N");
        var seen = new ConcurrentQueue<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "A2A" && instrument.Name == "a2a.client.circuit.state_changes") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? name = null, state = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "gen_ai.agent.name") name = tag.Value as string;
                if (tag.Key == "a2a.circuit.state") state = tag.Value as string;
            }
            if (name == agent && state is not null) seen.Enqueue(state);
        });
        listener.Start();
        var s = Make(threshold: 1, agent: agent);

        await Trip(s, 1);
        s.Time.Advance(Open);
        s.Handler.Behavior = Always(() => Ok());
        await Succeeds(s.Client);

        Assert.Equal(["open", "half_open", "closed"], seen.ToArray());
    }

    [Fact]
    public async Task The_factory_keeps_the_option()
    {
        var time = new FakeTimeProvider();
        var options = new A2AClientOptions
        {
            MaxRetries = 0,
            TimeProvider = time,
            CircuitBreaker = new CircuitBreakerOptions { FailureThreshold = 1, OpenDuration = Open },
        };
        var handler = new Handler { Behavior = Always(() => Status(HttpStatusCode.ServiceUnavailable)) };
        var client = A2AClientFactory.Create(Card("factory-agent"), new HttpClient(handler), options);

        await Fails(client);

        await Refused(client);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Two_clients_have_independent_state()
    {
        var a = Make(threshold: 1);
        var b = Make(threshold: 1);
        await Trip(a, 1);

        await Refused(a.Client);
        await Succeeds(b.Client);
    }

    [Fact]
    public void Options_are_validated()
    {
        var o = new CircuitBreakerOptions();
        Assert.Equal(5, o.FailureThreshold);
        Assert.Equal(TimeSpan.FromSeconds(30), o.OpenDuration);
        Assert.Throws<ArgumentOutOfRangeException>(() => o.FailureThreshold = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => o.OpenDuration = TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => o.OpenDuration = TimeSpan.FromSeconds(-1));
        o.FailureThreshold = 1;
        o.OpenDuration = TimeSpan.FromMilliseconds(1);
    }

    /// <summary>Returns its bytes, then fails like a dropped connection.</summary>
    private sealed class BreakingStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= data.Length) throw new IOException("connection reset");
            var n = Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Read(buffer, offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= data.Length) throw new IOException("connection reset");
            var n = Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return new ValueTask<int>(n);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
