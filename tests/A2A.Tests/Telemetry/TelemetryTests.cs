using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using A2A.Client;
using A2A.Client.Telemetry;
using A2A.Client.Transports;
using A2A.Json;
using A2A.Server;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using A2A.Server.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace A2A.Tests.Telemetry;

/// <summary>Activity and meter listeners are process-wide: these tests never run next to other tests.</summary>
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
                if (instrument.Meter.Name == A2AServerTelemetry.MeterName) listener.EnableMeasurementEvents(instrument);
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

    private static string Hash(string taskId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(taskId)))[..16].ToLowerInvariant();

    private static ServerCallContext AliceCall() => new()
    {
        Principal = "alice",
        User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", "alice")], "Test")),
    };

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

    [Fact]
    public async Task Server_span_per_operation_with_tags()
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(ProtocolBindings.JsonRpc);
        using var spans = new Spans(A2AServerTelemetry.ActivitySourceName);
        using var counters = new Counters();

        var task = (await client.SendMessageAsync("input")).RequireTask();
        await client.GetTaskAsync(task.Id);

        var get = Assert.Single(await spans.WaitForAsync("a2a.server.GetTask"));
        Assert.Equal(ActivityKind.Server, get.Kind);
        Assert.Equal(A2AServerTelemetry.ActivitySourceName, get.Source.Name);
        Assert.Equal("GetTask", get.GetTagItem("a2a.operation"));
        Assert.Equal("", get.GetTagItem("a2a.tenant"));
        Assert.Equal(Hash(task.Id), get.GetTagItem("a2a.task_id_hash"));
        Assert.Equal("ok", get.GetTagItem("a2a.outcome"));
        Assert.NotEqual(ActivityStatusCode.Error, get.Status);

        var send = Assert.Single(await spans.WaitForAsync("a2a.server.SendMessage"));
        Assert.Equal("SendMessage", send.GetTagItem("a2a.operation"));
        Assert.Null(send.GetTagItem("a2a.task_id_hash")); // a new task: the request names none
        Assert.Equal("ok", send.GetTagItem("a2a.outcome"));

        // Privacy: no principal and no bodies on the span.
        Assert.DoesNotContain(get.TagObjects, t => Equals(t.Value, "alice") || Equals(t.Value, task.Id));

        Assert.Equal(1, counters.Sum("a2a.server.requests", t => Equals(t["a2a.operation"], "GetTask") && Equals(t["a2a.outcome"], "ok")));
        Assert.Equal(1, counters.Sum("a2a.server.requests", t => Equals(t["a2a.operation"], "SendMessage") && Equals(t["a2a.outcome"], "ok")));
    }

    [Fact]
    public async Task Denied_call_has_span_with_error_outcome()
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler, DenyAll>()));
        using var spans = new Spans(A2AServerTelemetry.ActivitySourceName);
        using var counters = new Counters();

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(ProtocolBindings.JsonRpc).GetTaskAsync("t-denied"));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);

        var span = Assert.Single(await spans.WaitForAsync("a2a.server.GetTask"));
        Assert.Equal("error:TaskNotFound", span.GetTagItem("a2a.outcome"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(Hash("t-denied"), span.GetTagItem("a2a.task_id_hash"));
        Assert.Equal(1, counters.Sum("a2a.server.requests", t => Equals(t["a2a.operation"], "GetTask") && Equals(t["a2a.outcome"], "error:TaskNotFound")));
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
        using var spans = new Spans(A2AServerTelemetry.ActivitySourceName);

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(ProtocolBindings.HttpJson).GetTaskAsync("t-1"));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);

        var span = Assert.Single(await spans.WaitForAsync("a2a.server.GetTask"));
        Assert.Equal("error:InvalidParams", span.GetTagItem("a2a.outcome"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task Streaming_span_covers_the_whole_stream()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var spans = new Spans(A2AServerTelemetry.ActivitySourceName);
        using var counters = new Counters();
        const string text = "gate telemetry-stream";
        var gate = ScenarioAgent.Gate(text);

        using var cts = new CancellationTokenSource(Timeout);
        var events = 0;
        await foreach (var _ in host.Client(ProtocolBindings.JsonRpc).SendStreamingMessageAsync(Message.FromUser(text), cancellationToken: cts.Token))
        {
            if (++events == 1)
            {
                Assert.Contains(spans.Started, a => a.OperationName == "a2a.server.SendStreamingMessage");
                Assert.Empty(spans.Named("a2a.server.SendStreamingMessage")); // still open while events flow
                gate.TrySetResult();
            }
        }

        var span = Assert.Single(await spans.WaitForAsync("a2a.server.SendStreamingMessage"));
        Assert.Equal(ActivityKind.Server, span.Kind);
        Assert.Equal("ok", span.GetTagItem("a2a.outcome"));
        Assert.Equal(1, counters.Sum("a2a.server.streams.opened"));
        Assert.Equal(1, counters.Sum("a2a.server.requests", t => Equals(t["a2a.operation"], "SendStreamingMessage") && Equals(t["a2a.outcome"], "ok")));
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

        Assert.False(A2AServerTelemetry.Source.HasListeners());
        await host.Handler.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("x") }, AliceCall());
        Assert.Null(seen);

        // Control: with a listener the same call runs inside the operation span.
        using (new Spans(A2AServerTelemetry.ActivitySourceName))
            await host.Handler.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("x") }, AliceCall());
        Assert.Equal("a2a.server.SendMessage", seen);
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
    [InlineData(HttpStatusCode.OK, "delivered")]
    [InlineData(HttpStatusCode.BadRequest, "failed")]
    public async Task Push_deliveries_counter_records_the_final_result(HttpStatusCode status, string outcome)
    {
        using var counters = new Counters();
        var key = new TaskKey("", "alice", "t-push");
        var protector = new EphemeralPushSecretProtector();
        var store = new InMemoryPushConfigStore();
        await store.SaveAsync(new PushConfigRecord(key, new TaskPushNotificationConfig { Id = "c1", TaskId = key.TaskId, Url = "https://hooks.example/cb" }));
        var options = new A2AServerOptions();
        options.PushNotifications.AllowPrivateNetworks = true;
        using var sender = new HttpPushNotificationSender(new HttpMessageInvoker(new StatusHandler(status)), store, protector, options);

        sender.Enqueue(key, new SequencedEvent(1, StreamResponse.Of(Message.FromUser("x"))));
        await sender.WhenIdleAsync();

        Assert.Equal(1, counters.Sum("a2a.server.push.deliveries"));
        Assert.Equal(1, counters.Sum("a2a.server.push.deliveries", t => Equals(t["a2a.outcome"], outcome)));
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

        using var spans = new Spans(A2AClientTelemetry.ActivitySourceName);
        using var http = new HttpClient();
        var transport = new JsonRpcTransport(http, new Uri(app.Urls.First() + "/rpc"));
        var got = await transport.GetTaskAsync(new GetTaskRequest { Id = "t-1" });
        Assert.Equal("t-1", got.Id);

        var span = Assert.Single(spans.Named("a2a.client.GetTask"));
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Equal("GetTask", span.GetTagItem("a2a.operation"));
        Assert.Equal("ok", span.GetTagItem("a2a.outcome"));
        Assert.NotNull(traceparent);
        Assert.Equal(span.TraceId.ToHexString(), traceparent!.Split('-')[1]);
    }

    [Fact]
    public async Task Client_span_for_errors_and_stream_open()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var spans = new Spans(A2AClientTelemetry.ActivitySourceName);
        var client = host.Client(ProtocolBindings.HttpJson);

        await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync("missing"));
        var failed = Assert.Single(spans.Named("a2a.client.GetTask"));
        Assert.Equal("error:TaskNotFound", failed.GetTagItem("a2a.outcome"));
        Assert.Equal(ActivityStatusCode.Error, failed.Status);

        using var cts = new CancellationTokenSource(Timeout);
        await foreach (var _ in client.SendStreamingMessageAsync(Message.FromUser("task"), cancellationToken: cts.Token)) { }
        var open = Assert.Single(spans.Named("a2a.client.SendStreamingMessage"));
        Assert.Equal(ActivityKind.Client, open.Kind);
        Assert.Equal("ok", open.GetTagItem("a2a.outcome"));
    }
}
