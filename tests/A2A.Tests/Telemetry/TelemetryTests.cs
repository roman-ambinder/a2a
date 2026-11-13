using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Diagnostics;
using A2A.Json;
using A2A.Server;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Telemetry;
using A2A.Server.Uploads;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace A2A.Tests.Telemetry;

/// <summary>
/// Activity and meter listeners are process-wide: these tests never run next to other tests. Names and tag semantics are
/// main's tracing design (<see cref="A2ATelemetry"/>): spans named after the method, <c>a2a.method.name</c>, <c>a2a.task.id</c>,
/// error status with <c>error.type</c>/<c>a2a.error.code</c>, <c>a2a.canceled</c> for a caller's cancellation; the counters
/// carry <c>a2a.method.name</c> and <c>a2a.outcome</c>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryCollection
{
    public const string Name = "Telemetry";
}

[Collection(TelemetryCollection.Name)]
public sealed class TelemetryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // ---------------------------------------------------------------- listeners

    /// <summary>Records the stopped activities of the given sources.</summary>
    private sealed class Spans : IDisposable
    {
        private readonly ActivityListener _listener;

        public Spans(params string[] sources)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = s => sources.Contains(s.Name),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = a => Started.Enqueue(a),
                ActivityStopped = a => Stopped.Enqueue(a),
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public ConcurrentQueue<Activity> Started { get; } = new();
        public ConcurrentQueue<Activity> Stopped { get; } = new();

        public Activity[] Named(string name) => Stopped.Where(a => a.OperationName == name).ToArray();

        /// <summary>Waits until a span named <paramref name="name"/> has stopped (server spans stop after the response is written).</summary>
        public async Task<Activity[]> WaitForAsync(string name, int count = 1)
        {
            var until = DateTime.UtcNow + Timeout;
            while (Named(name).Length < count && DateTime.UtcNow < until) await Task.Delay(10);
            return Named(name);
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Records the measurements of the <c>A2A.Server</c> meter.</summary>
    private sealed class Counters : IDisposable
    {
        private readonly MeterListener _listener = new();

        public Counters()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == A2ATelemetry.ServerMeterName) listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var map = new Dictionary<string, object?>();
                foreach (var tag in tags) map[tag.Key] = tag.Value;
                Measurements.Enqueue((instrument.Name, value, map));
            });
            _listener.Start();
        }

        public ConcurrentQueue<(string Name, long Value, Dictionary<string, object?> Tags)> Measurements { get; } = new();

        public long Sum(string name, Func<Dictionary<string, object?>, bool>? where = null) =>
            Measurements.Where(m => m.Name == name && (where is null || where(m.Tags))).Sum(m => m.Value);

        public void Dispose() => _listener.Dispose();
    }

    private static ServerCallContext AliceCall() => new()
    {
        Principal = "alice",
        User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", "alice")], "Test")),
    };

    /// <summary>Runs <paramref name="hook"/> as the authorization decision.</summary>
    private sealed class HookAuthorization(Func<CancellationToken, bool> hook) : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(hook(cancellationToken));
    }

    private static Action<IServiceCollection> Authorize(Func<CancellationToken, bool> hook) =>
        s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler>(new HookAuthorization(hook)));

    private sealed class DenyAll : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);
    }

    private sealed class PassThrough : IA2AOperationFilter
    {
        public ValueTask<object?> InvokeAsync(A2AOperationContext context, object request, A2AOperationDelegate next, CancellationToken cancellationToken) =>
            next(context, request, cancellationToken);
    }

    private sealed class Blocker : IA2AOperationFilter
    {
        public ValueTask<object?> InvokeAsync(A2AOperationContext context, object request, A2AOperationDelegate next, CancellationToken cancellationToken) =>
            throw A2AException.InvalidParams("blocked by a filter");
    }

    // ---------------------------------------------------------------- server spans

    [Theory]
    [InlineData(TaskState.Completed, "TASK_STATE_COMPLETED")]
    [InlineData(TaskState.InputRequired, "TASK_STATE_INPUT_REQUIRED")]
    public void Task_state_value_is_the_proto_name(TaskState state, string expected) =>
        Assert.Equal(expected, A2ATelemetry.TaskStateValue(state));

    [Fact]
    public void Error_type_is_the_a2a_reason()
    {
        Assert.Equal("TASK_NOT_FOUND", A2ATelemetry.ErrorTypeOf(A2AException.TaskNotFound("t")));
        Assert.Equal(typeof(InvalidOperationException).FullName, A2ATelemetry.ErrorTypeOf(new InvalidOperationException()));
        Assert.Equal(A2AErrors.PayloadTooLargeReason, A2ATelemetry.ErrorTypeOf(A2AException.PayloadTooLarge(10))); // ReasonOverride wins
    }

    [Fact]
    public void Stream_event_of_a_final_task_reports_its_state()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("test.final-task");
        using var activity = source.StartActivity("stream")!;

        A2ATelemetry.AddStreamEvent(activity, Status(TaskState.Working));
        A2ATelemetry.AddStreamEvent(activity, new TaskEvent(new AgentTask { Id = "t-1", ContextId = "c-1", Status = new AgentTaskStatus { State = TaskState.Completed } }));

        var last = activity.Events.Last(e => e.Name == A2ATelemetry.Events.StreamEvent);
        Assert.Equal("task", last.Tags.Single(t => t.Key == A2ATelemetry.Events.EventKind).Value);
        Assert.Equal("TASK_STATE_COMPLETED", last.Tags.Single(t => t.Key == A2ATelemetry.Tags.TaskState).Value);
    }

    /// <summary>A host filter that opens its own span (another source) around the operation.</summary>
    private sealed class OwnSpanFilter : IA2AOperationFilter
    {
        private static readonly ActivitySource Own = new("test.host-filter");

        public async ValueTask<object?> InvokeAsync(A2AOperationContext context, object request, A2AOperationDelegate next, CancellationToken cancellationToken)
        {
            using var _ = Own.StartActivity("host-filter");
            return await next(context, request, cancellationToken);
        }
    }

    [Fact]
    public async Task Agent_tags_survive_a_host_filter_that_opens_its_own_span()
    {
        await using var host = await TestAgentHost.StartAsync(services: s =>
        {
            s.AddSingleton(new OwnSpanFilter());
            s.AddA2AServer().AddOperationFilter<OwnSpanFilter>(A2AFilterStage.AfterAuthorization);
        });
        using var spans = new Spans(A2ATelemetry.ServerSourceName, "test.host-filter");

        await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("hello");

        var send = Assert.Single(await spans.WaitForAsync("SendMessage"));
        Assert.Equal("Scenario Agent", send.GetTagItem(A2ATelemetry.Tags.AgentName));
    }

    [Fact]
    public async Task Client_span_uses_the_card_it_was_created_with_after_a_refresh()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var spans = new Spans(A2ATelemetry.ClientSourceName);
        var client = host.Client(ProtocolBindings.JsonRpc);

        var extended = await client.RefreshWithExtendedAgentCardAsync();
        Assert.Equal("Extended card for alice", extended.Description);
        Assert.Same(extended, client.AgentCard);
        await client.GetTaskAsync("t-1").ContinueWith(_ => { });

        var get = Assert.Single(spans.Named("GetTask"));
        Assert.Equal("Test agent", get.GetTagItem(A2ATelemetry.Tags.AgentDescription));
    }

    [Fact]
    public async Task Server_span_per_operation_with_tags()
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(ProtocolBindings.JsonRpc);
        using var spans = new Spans(A2ATelemetry.ServerSourceName);
        using var counters = new Counters();

        var task = (await client.SendMessageAsync("input")).RequireTask();
        await client.GetTaskAsync(task.Id);

        var get = Assert.Single(await spans.WaitForAsync("GetTask"));
        Assert.Equal(A2ATelemetry.ServerSourceName, get.Source.Name);
        Assert.Equal("GetTask", get.GetTagItem(A2ATelemetry.Tags.Operation));
        Assert.Equal(ProtocolBindings.JsonRpc, get.GetTagItem(A2ATelemetry.Tags.Binding));
        Assert.Null(get.GetTagItem(A2ATelemetry.Tags.Tenant)); // set only for a non-default tenant
        Assert.Equal(task.Id, get.GetTagItem(A2ATelemetry.Tags.TaskId));
        Assert.NotEqual(ActivityStatusCode.Error, get.Status);

        var send = Assert.Single(await spans.WaitForAsync("SendMessage"));
        Assert.Equal("SendMessage", send.GetTagItem(A2ATelemetry.Tags.Operation));
        Assert.Equal(task.Id, send.GetTagItem(A2ATelemetry.Tags.TaskId)); // from the result: a new task
        Assert.NotEqual(ActivityStatusCode.Error, send.Status);

        // Privacy: no principal and no bodies on the span.
        Assert.DoesNotContain(get.TagObjects, t => Equals(t.Value, "alice") || Equals(t.Value, "input"));

        Assert.Equal(1, counters.Sum("a2a.server.requests", t => Equals(t["a2a.method.name"], "GetTask") && Equals(t["a2a.outcome"], "ok")));
        Assert.Equal(1, counters.Sum("a2a.server.requests", t => Equals(t["a2a.method.name"], "SendMessage") && Equals(t["a2a.outcome"], "ok")));
    }

    [Fact]
    public async Task Denied_call_has_span_with_error_outcome()
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler, DenyAll>()));
        using var spans = new Spans(A2ATelemetry.ServerSourceName);
        using var counters = new Counters();

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(ProtocolBindings.JsonRpc).GetTaskAsync("t-denied"));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);

        var span = Assert.Single(await spans.WaitForAsync("GetTask"));
        Assert.Equal(nameof(A2AErrorCode.TaskNotFound), span.GetTagItem(A2ATelemetry.Tags.ErrorCode));
        Assert.Equal("TASK_NOT_FOUND", span.GetTagItem(A2ATelemetry.Tags.ErrorType));
        Assert.Equal("GetTask", span.GetTagItem(A2ATelemetry.Tags.Operation));
        Assert.Equal("a2a.method.name", A2ATelemetry.Tags.Operation);
        Assert.Null(span.GetTagItem("rpc.method"));
        Assert.Null(span.GetTagItem("rpc.system"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("t-denied", span.GetTagItem(A2ATelemetry.Tags.TaskId));
        Assert.Equal(1, counters.Sum("a2a.server.requests", t => Equals(t["a2a.method.name"], "GetTask") && Equals(t["a2a.outcome"], "error")
            && Equals(t["error.type"], "TASK_NOT_FOUND")));
    }

    [Fact]
    public async Task Telemetry_wraps_host_filters()
    {
        // Outermost: a refusal by a filter before authorization still gets one span, and a pass-through filter adds none.
        await using var host = await TestAgentHost.StartAsync(services: s =>
        {
            s.AddSingleton(new Blocker());
            s.AddSingleton(new PassThrough());
            s.AddA2AServer()
                .AddOperationFilter<PassThrough>(A2AFilterStage.AfterAuthorization)
                .AddOperationFilter<Blocker>(A2AFilterStage.BeforeAuthorization);
        });
        using var spans = new Spans(A2ATelemetry.ServerSourceName);

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(ProtocolBindings.HttpJson).GetTaskAsync("t-1"));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);

        var span = Assert.Single(await spans.WaitForAsync("GetTask"));
        Assert.Equal(nameof(A2AErrorCode.InvalidParams), span.GetTagItem(A2ATelemetry.Tags.ErrorCode));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task Streaming_span_covers_the_whole_stream()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var spans = new Spans(A2ATelemetry.ServerSourceName);
        using var counters = new Counters();
        const string text = "gate telemetry-stream";
        var gate = ScenarioAgent.Gate(text);

        using var cts = new CancellationTokenSource(Timeout);
        var events = 0;
        await foreach (var _ in host.Client(ProtocolBindings.JsonRpc).SendStreamingMessageAsync(Message.FromUser(text), cancellationToken: cts.Token))
        {
            if (++events == 1)
            {
                Assert.Contains(spans.Started, a => a.OperationName == "SendStreamingMessage");
                Assert.Empty(spans.Named("SendStreamingMessage")); // still open while events flow
                gate.TrySetResult();
            }
        }

        var span = Assert.Single(await spans.WaitForAsync("SendStreamingMessage"));
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
        Assert.Equal(events, Convert.ToInt32(span.GetTagItem(A2ATelemetry.Tags.StreamEventCount)));
        Assert.Equal(events, span.Events.Count(e => e.Name == A2ATelemetry.Events.StreamEvent));
        Assert.Equal(1, counters.Sum("a2a.server.streams.opened"));
        Assert.Equal(1, counters.Sum("a2a.server.requests", t => Equals(t["a2a.method.name"], "SendStreamingMessage") && Equals(t["a2a.outcome"], "ok")));
    }

    [Fact]
    public async Task Fast_path_without_listeners_allocates_no_activity()
    {
        string? seen = "not run";
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddA2AServer().AddAgentExecutor((context, updater, ct) =>
        {
            seen = Activity.Current?.OperationName;
            return updater.CompleteAsync("ok", ct).AsTask();
        }));
        Activity.Current = null;

        Assert.False(HasServerListeners());
        await host.Handler.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("x") }, AliceCall());
        Assert.Null(seen);

        // Control: with a listener the same call runs inside the agent run's span (a child of the operation span).
        using (new Spans(A2ATelemetry.ServerSourceName))
            await host.Handler.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("x") }, AliceCall());
        Assert.Equal(A2ATelemetry.SpanNames.AgentExecution, seen);
    }

    /// <summary>Whether anything listens to the server's source (a probe source of the same name sees the same listeners).</summary>
    private static bool HasServerListeners()
    {
        using var probe = new ActivitySource(A2ATelemetry.ServerSourceName);
        return probe.HasListeners();
    }

    // ---------------------------------------------------------------- counters

    [Fact]
    public async Task Events_counter_matches_emitted_events()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var counters = new Counters();

        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("task")).RequireTask();
        await host.WaitIdleAsync();

        var stored = 0;
        await foreach (var _ in host.App.Services.GetRequiredService<EventStore>().ReadAfterAsync(new TaskKey("", "alice", task.Id), 0)) stored++;
        Assert.True(stored >= 5, $"only {stored} events");
        Assert.Equal(stored, counters.Sum("a2a.server.events"));
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "sent")]
    [InlineData(HttpStatusCode.BadRequest, "dead_lettered")]
    public async Task Push_deliveries_counter_records_the_attempt_outcome(HttpStatusCode status, string outcome)
    {
        using var counters = new Counters();
        var key = new TaskKey("", "alice", "t-push");
        var store = new InMemoryPushConfigStore();
        await store.SaveAsync(new PushConfigRecord(key, new TaskPushNotificationConfig { Id = "c1", TaskId = key.TaskId, Url = "https://hooks.example/cb" }));
        var options = new A2AServerOptions();
        options.PushNotifications.AllowPrivateNetworks = true;
        var outbox = new InMemoryPushOutbox();
        var tasks = new InMemoryTaskStore(new InMemoryEventStore(options.Streaming), outbox);
        var now = DateTimeOffset.UtcNow;
        var record = new TaskRecord("", "alice", new AgentTask { Id = key.TaskId, ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working } }, 0, now, now);
        await tasks.CommitAsync(new TaskCommit(record, 0, StreamResponse.Of(Message.FromUser("x")), [new PushDelivery(key, "c1", 0, "d1")]));
        var service = new PushDeliveryService(outbox, store, new EphemeralPushSecretProtector(), new HttpMessageInvoker(new StatusHandler(status)), options);

        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None));

        Assert.Equal(1, counters.Sum("a2a.server.push.deliveries"));
        Assert.Equal(1, counters.Sum("a2a.server.push.deliveries", t => Equals(t["a2a.outcome"], outcome)));
        Assert.Equal(outcome == "dead_lettered" ? 1 : 0, counters.Sum("a2a.server.push.dead_letters"));
    }

    // ---------------------------------------------------------------- client spans

    [Fact]
    public async Task Client_span_and_traceparent_header_sent()
    {
        // A real socket: HttpClient injects traceparent in its own handler (DiagnosticsHandler inside SocketsHttpHandler),
        // which a custom primary handler such as RecordingHandler or the TestServer handler bypasses.
        string? traceparent = null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var task = new AgentTask { Id = "t-1", ContextId = "c-1", Status = new AgentTaskStatus { State = TaskState.Completed } };
        var body = $$"""{"jsonrpc":"2.0","id":"1","result":{{A2AJson.Serialize(task)}}}""";
        app.MapPost("/rpc", (HttpContext http) =>
        {
            traceparent = http.Request.Headers["traceparent"];
            return Results.Text(body, "application/json");
        });
        await app.StartAsync();

        using var spans = new Spans(A2ATelemetry.ClientSourceName);
        using var http = new HttpClient();
        var client = new A2AClient(new JsonRpcTransport(http, new Uri(app.Urls.First() + "/rpc")));
        var got = await client.GetTaskAsync(new GetTaskRequest { Id = "t-1" });
        Assert.Equal("t-1", got.Id);

        var span = Assert.Single(spans.Named("GetTask"));
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Equal("GetTask", span.GetTagItem(A2ATelemetry.Tags.Operation));
        Assert.Equal(ProtocolBindings.JsonRpc, span.GetTagItem(A2ATelemetry.Tags.Binding));
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
        Assert.NotNull(traceparent);
        Assert.Equal(span.TraceId.ToHexString(), traceparent!.Split('-')[1]);
    }

    [Fact]
    public async Task Client_span_for_errors_and_stream_open()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var spans = new Spans(A2ATelemetry.ClientSourceName);
        var client = host.Client(ProtocolBindings.HttpJson);

        await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync("missing"));
        var failed = Assert.Single(spans.Named("GetTask"));
        Assert.Equal(nameof(A2AErrorCode.TaskNotFound), failed.GetTagItem(A2ATelemetry.Tags.ErrorCode));
        Assert.Equal(ActivityStatusCode.Error, failed.Status);

        using var cts = new CancellationTokenSource(Timeout);
        await foreach (var _ in client.SendStreamingMessageAsync(Message.FromUser("task"), cancellationToken: cts.Token)) { }
        var open = Assert.Single(spans.Named("SendStreamingMessage"));
        Assert.Equal(ActivityKind.Client, open.Kind);
        Assert.NotEqual(ActivityStatusCode.Error, open.Status);
    }

    // ---------------------------------------------------------------- outcome accuracy

    [Fact]
    public async Task Server_side_cancellation_is_an_error()
    {
        // A cancellation the caller did not ask for (here: an unrelated token) is a server fault, as the bindings report it.
        await using var host = await TestAgentHost.StartAsync(services: Authorize(_ => throw new OperationCanceledException(new CancellationToken(canceled: true))));
        using var spans = new Spans(A2ATelemetry.ServerSourceName);
        using var counters = new Counters();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Handler.GetTaskAsync(new GetTaskRequest { Id = "t-1" }, AliceCall()));

        var span = Assert.Single(spans.Named("GetTask"));
        Assert.Equal(typeof(OperationCanceledException).FullName, span.GetTagItem(A2ATelemetry.Tags.ErrorType));
        Assert.Null(span.GetTagItem(A2ATelemetry.ErrorTypeCanceledTag));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(1, counters.Sum("a2a.server.requests", t => Equals(t["a2a.outcome"], "error")));
    }

    [Fact]
    public async Task Caller_cancellation_is_canceled()
    {
        using var caller = new CancellationTokenSource();
        await using var host = await TestAgentHost.StartAsync(services: Authorize(ct =>
        {
            caller.Cancel();
            ct.ThrowIfCancellationRequested();
            return true;
        }));
        using var spans = new Spans(A2ATelemetry.ServerSourceName);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Handler.GetTaskAsync(new GetTaskRequest { Id = "t-1" }, AliceCall(), caller.Token));

        var span = Assert.Single(spans.Named("GetTask"));
        Assert.Equal(true, span.GetTagItem(A2ATelemetry.ErrorTypeCanceledTag));
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task Abandoned_stream_is_canceled()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var spans = new Spans(A2ATelemetry.ServerSourceName);
        using var counters = new Counters();
        const string text = "gate telemetry-abandoned";
        var gate = ScenarioAgent.Gate(text);
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            await foreach (var _ in host.Handler.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser(text) }, AliceCall(), cancellationToken: cts.Token))
                break; // the reader stops after the first event: the stream is disposed before its end
        }
        finally
        {
            gate.TrySetResult();
        }

        var span = Assert.Single(spans.Named("SendStreamingMessage"));
        Assert.Equal(true, span.GetTagItem(A2ATelemetry.ErrorTypeCanceledTag));
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
        Assert.Equal(1, counters.Sum("a2a.server.requests", t => Equals(t["a2a.method.name"], "SendStreamingMessage") && Equals(t["a2a.outcome"], "canceled")));
    }

    [Fact]
    public async Task Streaming_work_runs_inside_the_server_span()
    {
        ActivityTraceId? trace = null;
        ActivitySpanId? parent = null;
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddA2AServer().AddAgentExecutor((context, updater, ct) =>
        {
            trace = Activity.Current?.TraceId;
            parent = Activity.Current?.ParentSpanId; // the agent runs in its run span, a child of the stream's span
            return updater.CompleteAsync("ok", ct).AsTask();
        }));
        using var spans = new Spans(A2ATelemetry.ServerSourceName);
        Activity.Current = null;

        using var cts = new CancellationTokenSource(Timeout);
        await foreach (var _ in host.Handler.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("x") }, AliceCall(), cancellationToken: cts.Token)) { }

        var server = Assert.Single(spans.Named("SendStreamingMessage"));
        Assert.Equal(server.TraceId, trace);
        Assert.Equal(server.SpanId, parent);
        Assert.Null(Activity.Current); // the reader's current activity is unchanged
    }

    [Fact]
    public async Task Client_caller_cancellation_is_canceled_and_timeout_is_an_error()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var spans = new Spans(A2ATelemetry.ClientSourceName);
        var transport = new A2AClient(new HttpJsonTransport(host.Http("alice"), new Uri(TestAgentHost.BaseUrl)));

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.GetTaskAsync(new GetTaskRequest { Id = "t-1" }, cancellationToken: canceled.Token));
        var get = Assert.Single(spans.Named("GetTask"));
        Assert.Equal(true, get.GetTagItem(A2ATelemetry.ErrorTypeCanceledTag));
        Assert.NotEqual(ActivityStatusCode.Error, get.Status);

        // A timeout is not the caller's cancellation: an error.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendMessageAsync(
            new SendMessageRequest { Message = Message.FromUser("slow") },
            new A2ARequestOptions { Timeout = TimeSpan.FromMilliseconds(200), MaxRetries = 0 }));
        var send = Assert.Single(spans.Named("SendMessage"));
        Assert.Null(send.GetTagItem(A2ATelemetry.ErrorTypeCanceledTag));
        Assert.Equal(ActivityStatusCode.Error, send.Status);
    }

    public static TheoryData<string, bool> StreamingClientMethods() => new()
    {
        { "send", true }, { "send", false },
        { "subscribe", true }, { "subscribe", false },
        { "observe", true }, { "observe", false },
    };

    /// <summary>
    /// A caller's cancellation of a client stream is <c>a2a.canceled</c>, not an error, whether the token goes to the method
    /// (<paramref name="methodToken"/>) or to the enumerator (<c>WithCancellation</c>).
    /// </summary>
    [Theory]
    [MemberData(nameof(StreamingClientMethods))]
    public async Task Client_stream_caller_cancellation_is_canceled(string method, bool methodToken)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(ProtocolBindings.JsonRpc);
        var text = $"gate {Guid.NewGuid():N}";
        var gate = ScenarioAgent.Gate(text);
        try
        {
            string? taskId = null;
            if (method != "send")
                taskId = (await client.SendMessageAsync(Message.FromUser(text), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask().Id;

            using var spans = new Spans(A2ATelemetry.ClientSourceName);
            using var caller = new CancellationTokenSource(Timeout);
            var methodCt = methodToken ? caller.Token : CancellationToken.None;
            var stream = method switch
            {
                "send" => client.SendStreamingMessageAsync(Message.FromUser(text), cancellationToken: methodCt),
                "subscribe" => client.SubscribeToTaskAsync(taskId!, cancellationToken: methodCt),
                _ => client.ObserveTaskAsync(taskId!, cancellationToken: methodCt),
            };
            var enumeratorCt = methodToken ? CancellationToken.None : caller.Token;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in stream.WithCancellation(enumeratorCt)) await caller.CancelAsync();
            });

            var operation = method == "send" ? "SendStreamingMessage" : "SubscribeToTask";
            var span = Assert.Single(spans.Named(operation));
            Assert.Equal(true, span.GetTagItem(A2ATelemetry.ErrorTypeCanceledTag));
            Assert.Null(span.GetTagItem(A2ATelemetry.Tags.ErrorType));
            Assert.NotEqual(ActivityStatusCode.Error, span.Status);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    // ---------------------------------------------------------------- convention attributes

    private static Activity SampledActivity() => new("test") { IsAllDataRequested = true };

    private static IEnumerable<string> Values(object? value) => value switch
    {
        null => [],
        string text => [text],
        IEnumerable<string> many => many,
        _ => [value.ToString() ?? ""],
    };

    private static void AssertNoSecret(IEnumerable<Activity> spans, string secret)
    {
        foreach (var span in spans)
        {
            foreach (var tag in span.TagObjects) Assert.DoesNotContain(Values(tag.Value), v => v.Contains(secret, StringComparison.Ordinal));
            foreach (var evt in span.Events)
                foreach (var tag in evt.Tags) Assert.DoesNotContain(Values(tag.Value), v => v.Contains(secret, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Set_message_tags_ids_and_never_content()
    {
        using var activity = SampledActivity();
        var message = new Message
        {
            MessageId = "m-1",
            Role = Role.User,
            ContextId = "c-1",
            TaskId = "t-1",
            ReferenceTaskIds = ["r-1"],
            Parts = [Part.FromText("SECRET-TEXT")],
            Metadata = new() { ["k"] = System.Text.Json.JsonSerializer.SerializeToElement("SECRET-META") },
        };

        A2ATelemetry.SetMessage(activity, message);

        Assert.Equal("m-1", activity.GetTagItem(A2ATelemetry.Tags.MessageId));
        Assert.Equal("c-1", activity.GetTagItem(A2ATelemetry.Tags.ContextId));
        Assert.Equal("t-1", activity.GetTagItem(A2ATelemetry.Tags.TaskId));
        Assert.Equal(new[] { "r-1" }, Assert.IsType<string[]>(activity.GetTagItem(A2ATelemetry.Tags.ReferenceTaskIds)));
        AssertNoSecret([activity], "SECRET");
    }

    [Fact]
    public void Set_message_skips_empty_references()
    {
        using var activity = SampledActivity();
        A2ATelemetry.SetMessage(activity, new Message { MessageId = "m-1", Role = Role.User, ReferenceTaskIds = [], Parts = [Part.FromText("x")] });
        Assert.Null(activity.GetTagItem(A2ATelemetry.Tags.ReferenceTaskIds));
    }

    [Fact]
    public void Set_agent_skips_empty_card_fields()
    {
        using var activity = SampledActivity();
        var card = TestAgentHost.DefaultCard();
        card.Name = "Bot";
        card.Description = "";
        card.Version = "1.2";

        A2ATelemetry.SetAgent(activity, card);

        Assert.Equal("Bot", activity.GetTagItem(A2ATelemetry.Tags.AgentName));
        Assert.Equal("1.2", activity.GetTagItem(A2ATelemetry.Tags.AgentVersion));
        Assert.Null(activity.GetTagItem(A2ATelemetry.Tags.AgentDescription));
        Assert.DoesNotContain(activity.TagObjects, t => t.Key == A2ATelemetry.Tags.AgentDescription);
    }

    private static StreamResponse Status(TaskState state) => new StatusUpdateEvent(
        new TaskStatusUpdateEvent { TaskId = "t-1", ContextId = "c-1", Status = new AgentTaskStatus { State = state } });

    [Fact]
    public void Stream_keeps_one_state_and_drops_mixed_states()
    {
        using var activity = SampledActivity();

        A2ATelemetry.SetStreamResult(activity, Status(TaskState.Working));
        A2ATelemetry.SetStreamResult(activity, Status(TaskState.Working));
        Assert.Equal("TASK_STATE_WORKING", activity.GetTagItem(A2ATelemetry.Tags.TaskState));

        A2ATelemetry.SetStreamResult(activity, Status(TaskState.Completed));
        Assert.Null(activity.GetTagItem(A2ATelemetry.Tags.TaskState));

        A2ATelemetry.SetStreamResult(activity, Status(TaskState.Completed));
        Assert.Null(activity.GetTagItem(A2ATelemetry.Tags.TaskState));
        Assert.Equal("t-1", activity.GetTagItem(A2ATelemetry.Tags.TaskId));
    }

    [Fact]
    public void Set_message_and_agent_skip_unsampled_span()
    {
        using var activity = new Activity("unsampled") { IsAllDataRequested = false };
        var message = new Message { MessageId = "m-1", Role = Role.User, ContextId = "c-1", TaskId = "t-1", ReferenceTaskIds = ["r-1"], Parts = [Part.FromText("x")] };

        A2ATelemetry.SetMessage(activity, message);
        A2ATelemetry.SetAgent(activity, TestAgentHost.DefaultCard());
        A2ATelemetry.SetOperationTags(activity, "SendMessage", "JSONRPC", "acme", "t-1", "c-1", "1.0");
        A2ATelemetry.SetStreamResult(activity, Status(TaskState.Working));

        Assert.Empty(activity.TagObjects);
        A2ATelemetry.SetMessage(null, message);
        A2ATelemetry.SetAgent(null, TestAgentHost.DefaultCard());
        A2ATelemetry.SetStreamResult(null, Status(TaskState.Working));
    }

    [Fact]
    public async Task Server_operation_span_carries_message_agent_and_protocol_version()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var spans = new Spans(A2ATelemetry.ServerSourceName);
        var message = Message.FromUser("SECRET-TEXT");
        message.Metadata = new() { ["k"] = System.Text.Json.JsonSerializer.SerializeToElement("SECRET-META") };

        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync(new SendMessageRequest { Message = message })).RequireTask();

        var send = Assert.Single(await spans.WaitForAsync("SendMessage"));
        Assert.Equal(message.MessageId, send.GetTagItem(A2ATelemetry.Tags.MessageId));
        Assert.Equal("1.0", send.GetTagItem(A2ATelemetry.Tags.ProtocolVersion));
        Assert.Equal("Scenario Agent", send.GetTagItem(A2ATelemetry.Tags.AgentName));
        Assert.Equal(task.ContextId, send.GetTagItem(A2ATelemetry.Tags.ContextId));
        Assert.Null(send.GetTagItem("gen_ai.operation.name"));
        AssertNoSecret(spans.Stopped, "SECRET");
    }

    [Fact]
    public async Task Server_stream_span_drops_the_state_of_a_multi_state_stream()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var spans = new Spans(A2ATelemetry.ServerSourceName);
        var states = new List<string>();

        using var cts = new CancellationTokenSource(Timeout);
        await foreach (var evt in host.Client(ProtocolBindings.JsonRpc).SendStreamingMessageAsync(Message.FromUser("task"), cancellationToken: cts.Token))
        {
            if (evt is StatusUpdateEvent { StatusUpdate.Status.State: var state }) states.Add(A2ATelemetry.TaskStateValue(state));
            else if (evt is TaskEvent { Task.Status.State: var initial }) states.Add(A2ATelemetry.TaskStateValue(initial));
        }

        var span = Assert.Single(await spans.WaitForAsync("SendStreamingMessage"));
        Assert.True(states.Distinct().Count() > 1, "the scenario goes through more than one state");
        Assert.Equal("Scenario Agent", span.GetTagItem(A2ATelemetry.Tags.AgentName));
        Assert.Null(span.GetTagItem(A2ATelemetry.Tags.TaskState));
        var last = span.Events.Last(e => e.Name == A2ATelemetry.Events.StreamEvent && e.Tags.Any(t => t.Key == A2ATelemetry.Tags.TaskState));
        Assert.Equal("TASK_STATE_COMPLETED", last.Tags.Single(t => t.Key == A2ATelemetry.Tags.TaskState).Value);
    }

    [Fact]
    public async Task Per_tenant_spans_carry_their_own_agent_name()
    {
        await using var host = await TestAgentHost.StartAsync(
            services: s => s.AddA2AServer().AddTenantCardProvider<TenantCards>().AddAgentExecutorResolver<TenantAgents>());
        using var spans = new Spans(A2ATelemetry.ServerSourceName);

        foreach (var tenant in new[] { "acme", "globex" })
            await host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { Tenant = tenant, ValidateCapabilities = false }).SendMessageAsync("hello");

        var sends = await spans.WaitForAsync("SendMessage", 2);
        Assert.Equal(2, sends.Length);
        Assert.Equal("acme agent", Assert.Single(sends, a => Equals(a.GetTagItem(A2ATelemetry.Tags.Tenant), "acme")).GetTagItem(A2ATelemetry.Tags.AgentName));
        Assert.Equal("globex agent", Assert.Single(sends, a => Equals(a.GetTagItem(A2ATelemetry.Tags.Tenant), "globex")).GetTagItem(A2ATelemetry.Tags.AgentName));
    }

    [Fact]
    public async Task Client_span_carries_card_protocol_version_and_message_id()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var spans = new Spans(A2ATelemetry.ClientSourceName);
        var message = Message.FromUser("SECRET-TEXT");

        await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync(new SendMessageRequest { Message = message });
        await foreach (var _ in host.Client(ProtocolBindings.JsonRpc).SendStreamingMessageAsync(Message.FromUser("task"))) { }

        var send = Assert.Single(spans.Named("SendMessage"));
        Assert.Equal("Scenario Agent", send.GetTagItem(A2ATelemetry.Tags.AgentName));
        Assert.Equal("1.0.0", send.GetTagItem(A2ATelemetry.Tags.AgentVersion));
        Assert.Equal("1.0", send.GetTagItem(A2ATelemetry.Tags.ProtocolVersion));
        Assert.Equal(message.MessageId, send.GetTagItem(A2ATelemetry.Tags.MessageId));
        var stream = Assert.Single(spans.Named("SendStreamingMessage"));
        Assert.NotNull(stream.GetTagItem(A2ATelemetry.Tags.MessageId));
        Assert.Equal("Scenario Agent", stream.GetTagItem(A2ATelemetry.Tags.AgentName));
        AssertNoSecret(spans.Stopped, "SECRET");
    }

    [Fact]
    public async Task Client_without_a_card_has_no_agent_tags()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var spans = new Spans(A2ATelemetry.ClientSourceName);
        var client = new A2AClient(new HttpJsonTransport(host.Http("alice"), new Uri(TestAgentHost.BaseUrl)));

        await client.GetTaskAsync(new GetTaskRequest { Id = "t-1" }).ContinueWith(_ => { });

        var get = Assert.Single(spans.Named("GetTask"));
        Assert.Null(get.GetTagItem(A2ATelemetry.Tags.AgentName));
        Assert.Null(get.GetTagItem(A2ATelemetry.Tags.AgentVersion));
        Assert.Equal("1.0", get.GetTagItem(A2ATelemetry.Tags.ProtocolVersion));
    }

    // ---------------------------------------------------------------- run span: cancel vs host shutdown

    /// <summary>Works until its token fires.</summary>
    private sealed class BlockingAgent
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAgentExecutor Executor => new DelegateAgentExecutor(async (_, u, ct) =>
        {
            await u.StartWorkAsync("working", ct);
            Started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
        });
    }

    private static async Task<AgentTask> StartBlockedAsync(TestAgentHost host, BlockingAgent agent)
    {
        var task = (await host.Handler.SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser("go"),
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        }, AliceCall()).WaitAsync(Timeout)).RequireTask();
        await agent.Started.Task.WaitAsync(Timeout);
        return task;
    }

    private static async Task<Activity> RunSpanAsync(Spans spans, string taskId, string kind = "execute")
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            var run = spans.Named(A2ATelemetry.SpanNames.AgentExecution).FirstOrDefault(a =>
                Equals(a.GetTagItem(A2ATelemetry.Tags.TaskId), taskId) && Equals(a.GetTagItem(A2ATelemetry.Tags.ExecutionKind), kind));
            if (run is not null) return run;
            await Task.Delay(10);
        }
        throw new Xunit.Sdk.XunitException($"No {kind} run span for {taskId}.");
    }

    [Fact]
    public async Task Run_canceled_by_CancelTask_is_canceled()
    {
        var agent = new BlockingAgent();
        await using var host = await TestAgentHost.StartAsync(services: s => s.Replace(ServiceDescriptor.Singleton(agent.Executor)));
        using var spans = new Spans(A2ATelemetry.ServerSourceName);
        var task = await StartBlockedAsync(host, agent);

        await host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = task.Id }, AliceCall()).WaitAsync(Timeout);

        var run = await RunSpanAsync(spans, task.Id);
        Assert.Equal(true, run.GetTagItem(A2ATelemetry.ErrorTypeCanceledTag));
        Assert.Null(run.GetTagItem(A2A.Server.Execution.TaskExecutionEngine.InterruptedTag));
        Assert.NotEqual(ActivityStatusCode.Error, run.Status);
    }

    [Fact]
    public async Task Run_stopped_by_host_shutdown_is_interrupted_not_canceled()
    {
        var agent = new BlockingAgent();
        await using var host = await TestAgentHost.StartAsync(services: s => s.Replace(ServiceDescriptor.Singleton(agent.Executor)));
        using var spans = new Spans(A2ATelemetry.ServerSourceName);
        var task = await StartBlockedAsync(host, agent);

        host.Engine.Shutdown();
        await host.Engine.WhenIdleAsync().WaitAsync(Timeout);

        var run = await RunSpanAsync(spans, task.Id);
        Assert.Null(run.GetTagItem(A2ATelemetry.ErrorTypeCanceledTag));
        Assert.Equal(true, run.GetTagItem(A2A.Server.Execution.TaskExecutionEngine.InterruptedTag));
        Assert.NotEqual(ActivityStatusCode.Error, run.Status);
        Assert.Null(run.GetTagItem(A2ATelemetry.Tags.ErrorType));
    }

    // ---------------------------------------------------------------- upload span errors

    /// <summary>An object store whose writes fail with <see cref="Fail"/>, or honour the token.</summary>
    private sealed class FailingStore : ArtifactObjectStore
    {
        private readonly InMemoryArtifactObjectStore _inner = new();
        public Exception? Fail { get; init; }

        public override ValueTask<ArtifactObjectWriteResult> PutAsync(string key, Stream content, ArtifactObjectInfo info, long maxBytes, CancellationToken cancellationToken = default)
        {
            if (Fail is not null) throw Fail;
            cancellationToken.ThrowIfCancellationRequested();
            return _inner.PutAsync(key, content, info, maxBytes, cancellationToken);
        }

        public override ValueTask<ArtifactObjectContent?> OpenReadAsync(string key, ArtifactByteRange? range = null, CancellationToken cancellationToken = default) =>
            _inner.OpenReadAsync(key, range, cancellationToken);

        public override ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);

        public override IAsyncEnumerable<ArtifactObjectEntry> ListAsync(string prefix, CancellationToken cancellationToken = default) =>
            _inner.ListAsync(prefix, cancellationToken);
    }

    private static ArtifactStorageOptions UploadStorage()
    {
        var storage = new ArtifactStorageOptions { PublicBaseUrl = new Uri("https://agent.test/a2a") };
        storage.Uploads.Enabled = true;
        return storage;
    }

    private static Activity UploadSpan(Spans spans, string tag, string value) =>
        Assert.Single(spans.Named(ServerTelemetry.UploadSpanName), a => Equals(a.GetTagItem(tag), value));

    private static async Task UploadAsync(string kind, ArtifactObjectStore store, string owner, CancellationToken ct)
    {
        if (kind == "offload")
            await new ArtifactOffload(store, UploadStorage(), "", owner).UploadAsync(new MemoryStream(new byte[64]), null, null, null, ct);
        else
            await new InboundFiles(store, UploadStorage(), TimeProvider.System).UploadAsync(
                new ServerCallContext { Principal = "alice", Tenant = owner, User = AliceCall().User }, new MemoryStream(new byte[64]), null, null, null, ct);
    }

    private static string OwnerTag(string kind) => kind == "offload" ? A2ATelemetry.Tags.TaskId : A2ATelemetry.Tags.Tenant;

    [Theory]
    [InlineData("offload")]
    [InlineData("inbound")]
    public async Task Failed_upload_span_has_the_documented_error_type(string kind)
    {
        using var spans = new Spans(A2ATelemetry.ServerSourceName);
        var owner = "fail-" + kind;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UploadAsync(kind, new FailingStore { Fail = new InvalidOperationException("boom") }, owner, CancellationToken.None));

        var span = UploadSpan(spans, OwnerTag(kind), owner);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, span.GetTagItem(A2ATelemetry.Tags.ErrorType));
        Assert.Equal(typeof(InvalidOperationException).FullName, span.StatusDescription);
    }

    [Theory]
    [InlineData("offload")]
    [InlineData("inbound")]
    public async Task Upload_canceled_by_the_caller_is_canceled_not_an_error(string kind)
    {
        using var spans = new Spans(A2ATelemetry.ServerSourceName);
        var owner = "cancel-" + kind;
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UploadAsync(kind, new FailingStore(), owner, caller.Token));

        var span = UploadSpan(spans, OwnerTag(kind), owner);
        Assert.Equal(true, span.GetTagItem(A2ATelemetry.ErrorTypeCanceledTag));
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
        Assert.Null(span.GetTagItem(A2ATelemetry.Tags.ErrorType));
    }

    // ---------------------------------------------------------------- one instrumentation scope

    [Fact]
    public void Artifact_bytes_served_counter_shares_the_server_meter_scope()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(A2A.Server.AspNetCore.ArtifactEndpoint).TypeHandle);
        _ = ServerTelemetry.Requests; // the server meter's counters exist
        var meters = new ConcurrentDictionary<string, Meter>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == A2ATelemetry.ServerMeterName) meters[instrument.Name] = instrument.Meter;
            },
        };
        listener.Start();

        var served = meters["a2a.server.artifact.bytes.served"];
        var requests = meters[A2ATelemetry.Metrics.Requests];
        Assert.NotNull(requests.Version);
        Assert.Equal(requests.Version, served.Version);
    }
}
