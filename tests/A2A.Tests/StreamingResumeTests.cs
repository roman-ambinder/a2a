using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using A2A.Client.Sse;
using A2A.Json;
using A2A.Server;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests;

/// <summary>Resumable SSE (spec design §7.4) and stream limits (§7.2 re-check, §7.7), read as raw SSE on both bindings.</summary>
public class StreamingResumeTests
{
    public static TheoryData<string> Bindings => BindingTests.Bindings;

    public static TheoryData<string, string> BadCursors
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var binding in new[] { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson })
                foreach (var cursor in new[] { "other:5", "other:2", "abc" })
                    data.Add(binding, cursor);
            return data;
        }
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // ---------------------------------------------------------------- cursor format

    [Fact]
    public void Cursor_round_trips_and_rejects_bad_values()
    {
        Assert.Equal("t1:7", StreamCursor.Format("t1", 7));
        Assert.True(StreamCursor.TryParse("t1:7", "t1", out var seq));
        Assert.Equal(7, seq);
        Assert.False(StreamCursor.TryParse(null, "t1", out _));
        Assert.False(StreamCursor.TryParse("", "t1", out _));
        Assert.False(StreamCursor.TryParse("abc", "t1", out _));
        Assert.False(StreamCursor.TryParse("t2:7", "t1", out _));
        Assert.False(StreamCursor.TryParse("t1:0", "t1", out _));
        Assert.False(StreamCursor.TryParse("t1:-3", "t1", out _));
        Assert.False(StreamCursor.TryParse("t1:+3", "t1", out _));
        Assert.False(StreamCursor.TryParse("t1:x", "t1", out _));
        Assert.False(StreamCursor.TryParse("t1:", "t1", out _));
        Assert.False(StreamCursor.TryParse(":7", "t1", out _));
    }

    // ---------------------------------------------------------------- ids and resume

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Frames_carry_task_scoped_ids(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        await using var stream = await RawSse.OpenAsync(host, SendStream(binding, "task"), binding);
        var frames = await stream.ReadToEndAsync(Timeout);

        var taskId = Assert.IsType<TaskEvent>(frames[0].Event).Task.Id;
        Assert.True(frames.Count >= 5);
        var first = SequenceOf(frames[0], taskId);
        for (var i = 1; i < frames.Count; i++) Assert.Equal($"{taskId}:{first + i}", frames[i].Id);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Resume_replays_missed_events_without_gaps(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        string taskId, cursor;
        await using (var stream = await RawSse.OpenAsync(host, SendStream(binding, "task"), binding))
        {
            var f1 = await stream.NextAsync(Timeout);
            var f2 = await stream.NextAsync(Timeout);
            taskId = Assert.IsType<TaskEvent>(f1!.Event).Task.Id;
            cursor = f2!.Id!;
        } // drop the connection

        Assert.Equal($"{taskId}:2", cursor);
        await using var resumed = await RawSse.OpenAsync(host, Subscribe(binding, taskId, cursor), binding);
        var frames = await resumed.ReadToEndAsync(Timeout);

        Assert.Equal([3L, 4L, 5L], frames.Select(f => SequenceOf(f, taskId)));
        Assert.Equal(TaskState.Completed, Assert.IsType<StatusUpdateEvent>(frames[^1].Event).StatusUpdate.Status.State);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Resume_while_running_merges_replay_and_live_without_gaps(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();
        await WaitForSequenceAsync(host, task.Id, 2); // Task snapshot (1), Working (2)

        await using var resumed = await RawSse.OpenAsync(host, Subscribe(binding, task.Id, $"{task.Id}:1"), binding);
        var replayed = await resumed.NextAsync(Timeout);
        Assert.Equal(2, SequenceOf(replayed!, task.Id));
        await client.CancelTaskAsync(task.Id);
        var rest = await resumed.ReadToEndAsync(Timeout);

        Assert.Equal([3L], rest.Select(f => SequenceOf(f, task.Id)));
        Assert.Equal(TaskState.Canceled, Assert.IsType<StatusUpdateEvent>(rest[^1].Event).StatusUpdate.Status.State);
    }

    /// <summary>A bus that loses some events, like a lossy multi-instance bus would.</summary>
    private sealed class LossyBus : TaskEventBus
    {
        public override void Publish(string taskId, SequencedEvent evt)
        {
            if (evt.Sequence % 4 != 0) base.Publish(taskId, evt);
        }
    }

    [Fact]
    public async Task Live_gap_is_filled_from_the_store()
    {
        await using var host = await TestAgentHost.StartAsync(services: s =>
        {
            s.AddScoped<IAgentExecutor, InterleavingAgent>();
            s.AddSingleton<TaskEventBus, LossyBus>();
        });
        var call = AliceCall();
        await using var send = host.Handler.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("go") }, call).GetAsyncEnumerator();
        Assert.True(await send.MoveNextAsync());
        var taskId = Assert.IsType<TaskEvent>(send.Current.Event).Task.Id;

        var received = new List<SequencedEvent>();
        await foreach (var e in host.Handler.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId }, call).WithCancellation(new CancellationTokenSource(Timeout).Token))
        {
            received.Add(e);
            if (received.Count == 1) _ = host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, call);
        }

        Assert.True(received.Count > 4);
        for (var i = 1; i < received.Count; i++) Assert.Equal(received[i - 1].Sequence + 1, received[i].Sequence);
        Assert.Equal(TaskState.Canceled, Assert.IsType<StatusUpdateEvent>(received[^1].Event).StatusUpdate.Status.State);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Resume_with_trimmed_cursor_gets_snapshot(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.Streaming.ReplayBufferSize = 2);
        var task = (await host.Client(binding).SendMessageAsync("task")).RequireTask();
        await host.WaitIdleAsync();

        await using var resumed = await RawSse.OpenAsync(host, Subscribe(binding, task.Id, $"{task.Id}:1"), binding);
        var frames = await resumed.ReadToEndAsync(Timeout);

        var snapshot = Assert.IsType<TaskEvent>(frames[0].Event).Task;
        Assert.Equal(task.Id, snapshot.Id);
        Assert.Equal($"{task.Id}:5", frames[0].Id);
        Assert.Single(frames); // terminal: the snapshot is the whole stream
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Resume_on_finished_task_returns_snapshot_and_closes(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var task = (await host.Client(binding).SendMessageAsync("task")).RequireTask();
        await host.WaitIdleAsync();
        var last = await host.App.Services.GetRequiredService<EventStore>().LastSequenceAsync(new TaskKey("", "alice", task.Id));

        await using var resumed = await RawSse.OpenAsync(host, Subscribe(binding, task.Id, StreamCursor.Format(task.Id, last)), binding);
        var frames = await resumed.ReadToEndAsync(Timeout);

        var frame = Assert.Single(frames);
        Assert.Equal(TaskState.Completed, Assert.IsType<TaskEvent>(frame.Event).Task.Status.State);
        Assert.Equal(StreamCursor.Format(task.Id, last), frame.Id);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Fresh_subscribe_on_finished_task_is_unsupported(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync("task")).RequireTask();
        await host.WaitIdleAsync();

        var ex = await Assert.ThrowsAsync<A2AException>(async () => { await foreach (var _ in client.SubscribeToTaskAsync(task.Id)) { } });
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
    }

    [Theory]
    [MemberData(nameof(BadCursors))]
    public async Task Cursor_for_other_task_or_garbage_gets_snapshot(string binding, string cursor)
    {
        await using var host = await TestAgentHost.StartAsync();
        var task = (await host.Client(binding).SendMessageAsync("task")).RequireTask();
        await host.WaitIdleAsync();

        await using var resumed = await RawSse.OpenAsync(host, Subscribe(binding, task.Id, cursor), binding);
        var frames = await resumed.ReadToEndAsync(Timeout);

        Assert.Equal(task.Id, Assert.IsType<TaskEvent>(frames[0].Event).Task.Id);
        Assert.Single(frames);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Send_stream_with_cursor_resumes_the_named_task(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        string taskId, cursor;
        await using (var stream = await RawSse.OpenAsync(host, SendStream(binding, "task"), binding))
        {
            var f1 = await stream.NextAsync(Timeout);
            var f2 = await stream.NextAsync(Timeout);
            taskId = Assert.IsType<TaskEvent>(f1!.Event).Task.Id;
            cursor = f2!.Id!;
        }

        await using var resumed = await RawSse.OpenAsync(host, SendStream(binding, "task", cursor), binding);
        var frames = await resumed.ReadToEndAsync(Timeout);
        Assert.Equal([3L, 4L, 5L], frames.Select(f => SequenceOf(f, taskId)));
        var list = await host.Client(binding).ListTasksAsync(new ListTasksRequest());
        Assert.Single(list.Tasks); // no second task was started
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Send_stream_for_another_task_ignores_the_cursor(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var other = (await client.SendMessageAsync("task")).RequireTask();
        var waiting = (await client.SendMessageAsync("input")).RequireTask();
        Assert.Equal(TaskState.InputRequired, waiting.Status.State);

        // The message continues "waiting"; the cursor names "other". The message must not be dropped.
        await using var stream = await RawSse.OpenAsync(host, SendStream(binding, "more", StreamCursor.Format(other.Id, 2), taskId: waiting.Id), binding);
        var frames = await stream.ReadToEndAsync(Timeout);

        Assert.All(frames, f => SequenceOf(f, waiting.Id));
        var last = Assert.IsType<StatusUpdateEvent>(frames[^1].Event).StatusUpdate;
        Assert.Equal(TaskState.Completed, last.Status.State);
        Assert.Equal("got: more", last.Status.Message!.GetText());
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Caught_up_resume_starts_promptly_without_keep_alive(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.Streaming.KeepAliveInterval = TimeSpan.Zero);
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();
        await WaitForSequenceAsync(host, task.Id, 2);

        var watch = Stopwatch.StartNew();
        await using var resumed = await RawSse.OpenAsync(host, Subscribe(binding, task.Id, StreamCursor.Format(task.Id, 2)), binding);
        Assert.True(resumed.IsEventStream);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(900), $"headers took {watch.Elapsed}"); // not the 1 s priming fallback

        await client.CancelTaskAsync(task.Id);
        var frames = await resumed.ReadToEndAsync(Timeout);
        Assert.Equal([3L], frames.Select(f => SequenceOf(f, task.Id)));
        Assert.Equal(TaskState.Canceled, Assert.IsType<StatusUpdateEvent>(frames[0].Event).StatusUpdate.Status.State);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Send_stream_with_unparsable_cursor_is_a_normal_send(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        await using var stream = await RawSse.OpenAsync(host, SendStream(binding, "task", "abc"), binding);
        var frames = await stream.ReadToEndAsync(Timeout);
        Assert.Equal(1, SequenceOf(frames[0], Assert.IsType<TaskEvent>(frames[0].Event).Task.Id));
        Assert.Equal(TaskState.Completed, Assert.IsType<StatusUpdateEvent>(frames[^1].Event).StatusUpdate.Status.State);
    }

    // ---------------------------------------------------------------- keep-alive

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Keep_alive_comment_sent_when_idle(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.Streaming.KeepAliveInterval = TimeSpan.FromMilliseconds(50));
        using var response = await host.Http().SendAsync(SendStream(binding, "slow"), HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        using var cts = new CancellationTokenSource(Timeout);
        var text = new StringBuilder();
        var buffer = new byte[4096];
        while (!text.ToString().Contains(": ka\n"))
        {
            var read = await body.ReadAsync(buffer, cts.Token);
            Assert.True(read > 0, "stream ended before a keep-alive");
            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
    }

    // ---------------------------------------------------------------- admission

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Too_many_streams_per_task_rejected(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o =>
        {
            o.Limits.MaxConcurrentStreamsPerTask = 1;
            o.EnableRateLimiting = false;
        });
        var task = (await host.Client(binding).SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();

        var first = await RawSse.OpenAsync(host, Subscribe(binding, task.Id), binding);
        Assert.IsType<TaskEvent>((await first.NextAsync(Timeout))!.Event);

        await using (var second = await RawSse.OpenAsync(host, Subscribe(binding, task.Id), binding))
        {
            Assert.False(second.IsEventStream);
            Assert.Equal((HttpStatusCode)429, second.Response.StatusCode);
            Assert.Contains("RATE_LIMITED", await second.Response.Content.ReadAsStringAsync());
        }

        // A dropped connection gives its slot back.
        await first.DisposeAsync();
        await EventuallyAsync(async () =>
        {
            await using var third = await RawSse.OpenAsync(host, Subscribe(binding, task.Id), binding);
            return third.IsEventStream && (await third.NextAsync(Timeout))?.Event is TaskEvent;
        });
        await EventuallyAsync(() => Task.FromResult(host.Handler.OpenStreams == 0));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Stream_slot_is_released_after_normal_completion(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o =>
        {
            o.Limits.MaxConcurrentStreamsPerPrincipal = 1;
            o.EnableRateLimiting = false;
        });
        for (var i = 0; i < 3; i++)
        {
            await using var stream = await RawSse.OpenAsync(host, SendStream(binding, "task"), binding);
            Assert.True(stream.IsEventStream, $"stream {i} was rejected");
            var frames = await stream.ReadToEndAsync(Timeout);
            Assert.Equal(TaskState.Completed, Assert.IsType<StatusUpdateEvent>(frames[^1].Event).StatusUpdate.Status.State);
            Assert.Equal(0, host.Handler.OpenStreams);
        }
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Too_many_streams_per_principal_rejected(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o =>
        {
            o.Limits.MaxConcurrentStreamsPerPrincipal = 1;
            o.EnableRateLimiting = false;
        });
        await using var first = await RawSse.OpenAsync(host, SendStream(binding, "slow"), binding);
        Assert.IsType<TaskEvent>((await first.NextAsync(Timeout))!.Event);

        await using var second = await RawSse.OpenAsync(host, SendStream(binding, "slow"), binding);
        Assert.Equal((HttpStatusCode)429, second.Response.StatusCode);
        Assert.Contains("RATE_LIMITED", await second.Response.Content.ReadAsStringAsync());

        // Another caller has its own budget.
        await using var other = await RawSse.OpenAsync(host, SendStream(binding, "slow", user: "bob"), binding);
        Assert.True(other.IsEventStream);
    }

    // ---------------------------------------------------------------- re-auth and duration

    private sealed class Switchable : A2AAuthorizationHandler
    {
        public volatile bool Allow = true;

        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperation op, object request, CancellationToken ct) =>
            ValueTask.FromResult(Allow);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Stream_ends_when_authorization_revoked(string binding)
    {
        var auth = new Switchable();
        await using var host = await TestAgentHost.StartAsync(
            o => o.Limits.StreamReauthInterval = TimeSpan.FromMilliseconds(100),
            services: s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler>(auth)));
        await using var stream = await RawSse.OpenAsync(host, SendStream(binding, "slow"), binding);
        Assert.IsType<TaskEvent>((await stream.NextAsync(Timeout))!.Event);

        auth.Allow = false;
        var watch = Stopwatch.StartNew();
        var rest = await stream.ReadToEndAsync(TimeSpan.FromSeconds(1));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.All(rest, f => Assert.Null(f.Error));
        Assert.DoesNotContain(rest, f => f.Event is StatusUpdateEvent { StatusUpdate.Status.State: var s } && s.IsTerminal());
        await EventuallyAsync(() => Task.FromResult(host.Handler.OpenStreams == 0));
    }

    /// <summary>Emits artifacts without end (about 5 per millisecond at most), faster than a reader that pauses can take them.</summary>
    private sealed class FloodAgent : IAgentExecutor
    {
        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            await updater.StartWorkAsync(cancellationToken: ct);
            for (var i = 0; ; i++)
            {
                await updater.AddArtifactAsync([Part.FromText("x")], artifactId: "flood", cancellationToken: ct);
                if (i % 5 == 0) await Task.Delay(1, ct);
            }
        }
    }

    [Fact]
    public async Task Busy_stream_rechecks_authorization()
    {
        var auth = new Switchable();
        await using var host = await TestAgentHost.StartAsync(
            o => o.Limits.StreamReauthInterval = TimeSpan.FromMilliseconds(100),
            services: s =>
            {
                s.AddScoped<IAgentExecutor, FloodAgent>();
                s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler>(auth));
            });
        await using var stream = host.Handler.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("go") }, AliceCall()).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        var taskId = Assert.IsType<TaskEvent>(stream.Current.Event).Task.Id;
        await Task.Delay(300); // a backlog builds up, so every following MoveNext completes at once
        await WaitForSequenceAsync(host, taskId, 50);

        auth.Allow = false;
        var watch = Stopwatch.StartNew();
        var after = 0;
        while (await stream.MoveNextAsync().AsTask().WaitAsync(Timeout))
        {
            after++;
            if (watch.Elapsed > TimeSpan.FromSeconds(2)) break;
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"stream still open after {watch.Elapsed}");
        Assert.True(after <= 1, $"{after} events were sent after the re-check deadline passed");

        auth.Allow = true;
        await host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, AliceCall()); // stop the flood
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Stream_ends_cleanly_at_max_duration(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.Limits.MaxStreamDuration = TimeSpan.FromMilliseconds(300));
        await using var stream = await RawSse.OpenAsync(host, SendStream(binding, "slow"), binding);
        var frames = await stream.ReadToEndAsync(Timeout);
        Assert.IsType<TaskEvent>(frames[0].Event);
        Assert.All(frames, f => Assert.Null(f.Error));
        await EventuallyAsync(() => Task.FromResult(host.Handler.OpenStreams == 0));
    }

    // ---------------------------------------------------------------- event size

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Oversized_event_fails_task(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.Limits.MaxEventBytes = 1024);
        var client = host.Client(binding);
        var sent = (await client.SendMessageAsync("big")).RequireTask();
        await host.WaitIdleAsync();

        var task = await client.GetTaskAsync(sent.Id);
        Assert.Equal(TaskState.Failed, task.Status.State);
        Assert.Equal("The agent produced an event larger than the configured limit.", task.Status.Message!.GetText());
        Assert.Null(task.Artifacts);
    }

    // ---------------------------------------------------------------- helpers

    private static ServerCallContext AliceCall() => new()
    {
        Principal = "alice",
        User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", "alice")], "Test")),
    };

    private static long SequenceOf(Frame frame, string taskId)
    {
        Assert.NotNull(frame.Id);
        Assert.True(StreamCursor.TryParse(frame.Id, taskId, out var seq), $"bad id '{frame.Id}'");
        return seq;
    }

    private static async Task WaitForSequenceAsync(TestAgentHost host, string taskId, long sequence)
    {
        var store = host.App.Services.GetRequiredService<EventStore>();
        await EventuallyAsync(async () => await store.LastSequenceAsync(new TaskKey("", "alice", taskId)) >= sequence);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(50);
        }
    }

    private static HttpRequestMessage SendStream(string binding, string text, string? lastEventId = null, string user = "alice", string? taskId = null)
    {
        var task = taskId is null ? "" : $$$""","taskId":"{{{taskId}}}" """.TrimEnd();
        var message = $$$"""{"message":{"messageId":"{{{Guid.NewGuid():N}}}","role":"ROLE_USER","parts":[{"text":"{{{text}}}"}]{{{task}}}}}""";
        return Build(binding == ProtocolBindings.JsonRpc
            ? Rpc(A2AProtocol.Methods.SendStreamingMessage, message)
            : new HttpRequestMessage(HttpMethod.Post, "/a2a/message:stream") { Content = Json(message) }, lastEventId, user);
    }

    private static HttpRequestMessage Subscribe(string binding, string taskId, string? lastEventId = null, string user = "alice") =>
        Build(binding == ProtocolBindings.JsonRpc
            ? Rpc(A2AProtocol.Methods.SubscribeToTask, $$"""{"id":"{{taskId}}"}""")
            : new HttpRequestMessage(HttpMethod.Get, $"/a2a/tasks/{taskId}:subscribe"), lastEventId, user);

    private static HttpRequestMessage Rpc(string method, string paramsJson) =>
        new(HttpMethod.Post, "/a2a") { Content = Json($$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}","params":{{paramsJson}}}""") };

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static HttpRequestMessage Build(HttpRequestMessage req, string? lastEventId, string user)
    {
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        req.Headers.Add("X-Test-User", user);
        if (lastEventId is not null) req.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        return req;
    }

    /// <summary>One SSE frame: the <c>id:</c> field and the event, or the error the frame carried.</summary>
    private sealed record Frame(string? Id, StreamResponse? Event, string? Error);

    private sealed class RawSse : IAsyncDisposable
    {
        private readonly string _binding;
        private readonly HttpClient _http;
        private IAsyncEnumerator<SseEvent>? _events;

        private RawSse(HttpClient http, HttpResponseMessage response, string binding)
        {
            _http = http;
            Response = response;
            _binding = binding;
        }

        public HttpResponseMessage Response { get; }

        public bool IsEventStream => Response.Content.Headers.ContentType?.MediaType == "text/event-stream";

        public static async Task<RawSse> OpenAsync(TestAgentHost host, HttpRequestMessage request, string binding)
        {
            var http = host.Http();
            using var cts = new CancellationTokenSource(Timeout);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return new RawSse(http, response, binding);
        }

        /// <summary>The next frame, or null at the end of the stream.</summary>
        public async Task<Frame?> NextAsync(TimeSpan timeout)
        {
            if (!IsEventStream) Assert.Fail($"not an event stream: {(int)Response.StatusCode} {await Response.Content.ReadAsStringAsync()}");
            _events ??= SseReader.ReadAsync(await Response.Content.ReadAsStreamAsync()).GetAsyncEnumerator();
            var move = _events.MoveNextAsync().AsTask();
            if (await Task.WhenAny(move, Task.Delay(timeout)) != move) throw new TimeoutException("no SSE frame in time");
            if (!await move) return null;
            return Parse(_events.Current);
        }

        public async Task<List<Frame>> ReadToEndAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            var frames = new List<Frame>();
            while (await NextAsync(deadline - DateTime.UtcNow is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero) is { } f) frames.Add(f);
            return frames;
        }

        private Frame Parse(SseEvent e)
        {
            using var doc = JsonDocument.Parse(e.Data);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error)) return new Frame(e.Id, null, error.GetRawText());
            var payload = _binding == ProtocolBindings.JsonRpc ? root.GetProperty("result") : root;
            return new Frame(e.Id, A2AJson.Deserialize<StreamResponse>(payload), null);
        }

        public async ValueTask DisposeAsync()
        {
            if (_events is not null)
            {
                try { await _events.DisposeAsync(); }
                catch (Exception) { /* the connection is going away */ }
            }
            Response.Dispose();
            _http.Dispose();
        }
    }
}
