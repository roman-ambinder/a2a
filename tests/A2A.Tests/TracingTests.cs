using System.Collections.Concurrent;
using System.Diagnostics;
using A2A.Client;
using A2A.Diagnostics;
using A2A.Server;

namespace A2A.Tests;

/// <summary>
/// Collects the spans of one trace. The listener is process-wide and tests run in parallel, so each test starts its own
/// root activity and only spans of that trace are kept.
/// </summary>
public sealed class TraceCollector : IDisposable
{
    private static readonly ActivitySource TestSource = new("A2A.Tests");
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<Activity> _stopped = new();

    public TraceCollector()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is A2ATelemetry.ServerSourceName or A2ATelemetry.ClientSourceName or "A2A.Tests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => _stopped.Enqueue(a),
        };
        ActivitySource.AddActivityListener(_listener);
        Root = TestSource.StartActivity("test-root", ActivityKind.Internal) ?? throw new InvalidOperationException("no root activity");
    }

    /// <summary>The test's root span; every SDK span of the test belongs to its trace.</summary>
    public Activity Root { get; }

    public ActivityTraceId TraceId => Root.TraceId;

    /// <summary>Stopped SDK spans of this trace (or of <paramref name="traceId"/>).</summary>
    public List<Activity> Spans(ActivityTraceId? traceId = null) =>
        _stopped.Where(a => a.TraceId == (traceId ?? TraceId) && a.Source.Name != "A2A.Tests").ToList();

    /// <summary>Waits until a stopped span matches (server stream spans stop after the client has read the last event).</summary>
    public async Task<Activity> WaitForAsync(Func<Activity, bool> match, ActivityTraceId? traceId = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var found = Spans(traceId).FirstOrDefault(match);
            if (found is not null) return found;
            if (DateTime.UtcNow > deadline)
                throw new Xunit.Sdk.XunitException("No matching span. Seen: " + string.Join(", ", Spans(traceId).Select(s => $"{s.Source.Name}:{s.OperationName}:{s.Kind}")));
            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        Root.Dispose();
        _listener.Dispose();
    }
}

public class TracingTests
{
    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson, ProtocolBindings.Grpc };

    private static bool IsServerOp(Activity a, string op) =>
        a.Source.Name == A2ATelemetry.ServerSourceName && a.OperationName == op;

    private static bool IsClientOp(Activity a, string op) =>
        a.Source.Name == A2ATelemetry.ClientSourceName && a.OperationName == op;

    /// <summary>True when <paramref name="ancestor"/> is the parent of <paramref name="span"/> or of one of its in-process parents (for example ASP.NET Core's request activity).</summary>
    private static bool DescendsFrom(Activity span, Activity ancestor)
    {
        for (var a = span; a is not null; a = a.Parent)
            if (a.ParentSpanId == ancestor.SpanId) return true;
        return false;
    }

    private static string? Tag(Activity a, string name) => a.GetTagItem(name)?.ToString();

    /// <summary>No tag or span event tag holds <paramref name="secret"/>.</summary>
    private static void AssertNoContent(IEnumerable<Activity> spans, string secret)
    {
        foreach (var span in spans)
        {
            foreach (var tag in span.TagObjects) Assert.DoesNotContain(secret, tag.Value?.ToString() ?? "", StringComparison.Ordinal);
            foreach (var evt in span.Events)
                foreach (var tag in evt.Tags) Assert.DoesNotContain(secret, tag.Value?.ToString() ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain(secret, span.StatusDescription ?? "", StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Send_message_links_client_server_and_agent_spans(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(grpc: true);
        using var trace = new TraceCollector();

        var task = (await host.Client(binding).SendMessageAsync("task s3cr3t-text")).RequireTask();
        await host.WaitIdleAsync();

        var client = await trace.WaitForAsync(a => IsClientOp(a, A2AProtocol.Methods.SendMessage));
        var server = await trace.WaitForAsync(a => IsServerOp(a, A2AProtocol.Methods.SendMessage));
        var run = await trace.WaitForAsync(a => a.OperationName == A2ATelemetry.SpanNames.AgentExecution);

        Assert.Equal(ActivityKind.Client, client.Kind);
        Assert.Equal(binding, Tag(client, A2ATelemetry.Tags.Binding));
        Assert.Equal(binding, Tag(server, A2ATelemetry.Tags.Binding));
        Assert.Equal(A2AProtocol.Methods.SendMessage, Tag(server, A2ATelemetry.Tags.Operation));
        Assert.True(DescendsFrom(client, trace.Root));
        Assert.True(DescendsFrom(server, client), "the server span joins the client's trace under the client span");
        Assert.Equal(server.SpanId, run.ParentSpanId);

        foreach (var span in new[] { client, server, run })
        {
            Assert.Equal(task.Id, Tag(span, A2ATelemetry.Tags.TaskId));
            Assert.Equal(task.ContextId, Tag(span, A2ATelemetry.Tags.ContextId));
            Assert.NotEqual(ActivityStatusCode.Error, span.Status);
        }
        Assert.Equal("TASK_STATE_COMPLETED", Tag(run, A2ATelemetry.Tags.TaskState));
        Assert.Equal("execute", Tag(run, A2ATelemetry.Tags.ExecutionKind));
        AssertNoContent(trace.Spans(), "s3cr3t");
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Stream_is_one_span_with_events_on_both_sides(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(grpc: true);
        using var trace = new TraceCollector();

        var received = 0;
        await foreach (var _ in host.Client(binding).SendStreamingMessageAsync(Message.FromUser("task")))
        {
            received++;
            // The stream's span never leaks to the code reading the stream.
            Assert.Same(trace.Root, Activity.Current);
        }
        await host.WaitIdleAsync();

        var client = await trace.WaitForAsync(a => IsClientOp(a, A2AProtocol.Methods.SendStreamingMessage));
        var server = await trace.WaitForAsync(a => IsServerOp(a, A2AProtocol.Methods.SendStreamingMessage));

        Assert.Equal(received, client.Events.Count(e => e.Name == A2ATelemetry.Events.StreamEvent));
        Assert.Equal(received, Convert.ToInt32(client.GetTagItem(A2ATelemetry.Tags.StreamEventCount)));
        Assert.Equal(received, server.Events.Count(e => e.Name == A2ATelemetry.Events.StreamEvent));
        Assert.Contains(server.Events, e => e.Tags.Any(t => t.Key == A2ATelemetry.Events.EventSequence));
        // The stream went through more than one state, so the spans carry none; the last event has the final one.
        Assert.Null(client.GetTagItem(A2ATelemetry.Tags.TaskState));
        Assert.Null(server.GetTagItem(A2ATelemetry.Tags.TaskState));
        foreach (var span in new[] { client, server })
        {
            var last = span.Events.Last(e => e.Tags.Any(t => t.Key == A2ATelemetry.Tags.TaskState));
            Assert.Equal("TASK_STATE_COMPLETED", last.Tags.Single(t => t.Key == A2ATelemetry.Tags.TaskState).Value);
        }
        Assert.True(DescendsFrom(server, client));
        // Events are span events, not child spans: the only children of the stream span are the agent run.
        Assert.All(trace.Spans().Where(s => s.ParentSpanId == server.SpanId), s => Assert.Equal(A2ATelemetry.SpanNames.AgentExecution, s.OperationName));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Server_reads_traceparent_on_every_binding(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(grpc: true);
        using var trace = new TraceCollector();
        var task = (await host.Client(binding).SendMessageAsync("task")).RequireTask();
        await host.WaitIdleAsync();

        // A caller-chosen trace context, sent as a plain header (gRPC metadata); it replaces the client span's.
        var remoteTrace = ActivityTraceId.CreateRandom();
        var remoteSpan = ActivitySpanId.CreateRandom();
        var options = new A2ARequestOptions();
        options.Headers[A2ATelemetry.TraceParentHeader] = $"00-{remoteTrace.ToHexString()}-{remoteSpan.ToHexString()}-01";
        options.Headers[A2ATelemetry.TraceStateHeader] = "vendor=value";
        await host.Client(binding).GetTaskAsync(task.Id, options: options);

        var server = await trace.WaitForAsync(a => IsServerOp(a, A2AProtocol.Methods.GetTask), remoteTrace);
        // Its parent is the caller's span, directly or through ASP.NET Core's request activity.
        Assert.True(server.ParentSpanId == remoteSpan || server.Parent?.ParentSpanId == remoteSpan);
        Assert.Equal(binding, Tag(server, A2ATelemetry.Tags.Binding));
    }

    [Fact]
    public async Task Handler_uses_call_trace_parent_when_no_activity_is_current()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var trace = new TraceCollector();
        var remote = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, isRemote: true);
        var call = new ServerCallContext { Principal = "alice", TraceParent = remote, Binding = "TEST" };

        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            await Assert.ThrowsAsync<A2AException>(() => host.Handler.GetTaskAsync(new GetTaskRequest { Id = "missing" }, call));
        }
        finally
        {
            Activity.Current = previous;
        }

        var server = await trace.WaitForAsync(a => IsServerOp(a, A2AProtocol.Methods.GetTask), remote.TraceId);
        Assert.Equal(remote.SpanId, server.ParentSpanId);
        Assert.Equal(ActivityKind.Server, server.Kind);
        Assert.Equal("TEST", Tag(server, A2ATelemetry.Tags.Binding));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Errors_set_error_status_and_code(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(grpc: true);
        using var trace = new TraceCollector();

        await Assert.ThrowsAsync<A2AException>(() => host.Client(binding).GetTaskAsync("no-such-task"));

        var server = await trace.WaitForAsync(a => IsServerOp(a, A2AProtocol.Methods.GetTask));
        var client = await trace.WaitForAsync(a => IsClientOp(a, A2AProtocol.Methods.GetTask));
        foreach (var span in new[] { server, client })
        {
            Assert.Equal(ActivityStatusCode.Error, span.Status);
            Assert.Equal(nameof(A2AErrorCode.TaskNotFound), Tag(span, A2ATelemetry.Tags.ErrorCode));
            Assert.Equal("TASK_NOT_FOUND", Tag(span, A2ATelemetry.Tags.ErrorType));
            Assert.Equal("no-such-task", Tag(span, A2ATelemetry.Tags.TaskId));
        }
    }

    [Fact]
    public async Task Agent_failure_marks_the_run_span()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var trace = new TraceCollector();

        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("fail")).RequireTask();
        Assert.Equal(TaskState.Failed, task.Status.State);
        await host.WaitIdleAsync();

        var run = await trace.WaitForAsync(a => a.OperationName == A2ATelemetry.SpanNames.AgentExecution);
        Assert.Equal(ActivityStatusCode.Error, run.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, Tag(run, A2ATelemetry.Tags.ErrorType));
        Assert.Equal("TASK_STATE_FAILED", Tag(run, A2ATelemetry.Tags.TaskState));
        AssertNoContent(trace.Spans(), "boom"); // the exception message is not recorded
    }

    [Fact]
    public async Task Time_limit_marks_the_run_span()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.MaxTaskDuration = TimeSpan.FromMilliseconds(200));
        using var trace = new TraceCollector();

        var task = (await host.Client(ProtocolBindings.HttpJson).SendMessageAsync("slow")).RequireTask();
        Assert.Equal(TaskState.Failed, task.Status.State);
        await host.WaitIdleAsync();

        var run = await trace.WaitForAsync(a => a.OperationName == A2ATelemetry.SpanNames.AgentExecution);
        Assert.Equal(ActivityStatusCode.Error, run.Status);
        Assert.Equal(true, run.GetTagItem(A2ATelemetry.Tags.TimedOut));
        Assert.Equal("TimeLimit", Tag(run, A2ATelemetry.Tags.ErrorType));
    }

    [Fact]
    public async Task Webhook_post_carries_traceparent_of_its_delivery_span()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var trace = new TraceCollector();
        var hook = $"http://hooks.test/{Guid.NewGuid():N}";

        await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser("task"),
            Configuration = new SendMessageConfiguration { TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = hook, Token = "s3cr3t-token" } },
        });
        await host.WaitIdleAsync();

        var posts = host.Webhooks.Calls.Where(c => c.Request.RequestUri!.ToString() == hook).ToList();
        Assert.NotEmpty(posts);
        var run = await trace.WaitForAsync(a => a.OperationName == A2ATelemetry.SpanNames.AgentExecution);
        var deliveries = trace.Spans().Where(a => a.OperationName == A2ATelemetry.SpanNames.PushDelivery).ToList();
        Assert.Equal(posts.Count, deliveries.Count);
        foreach (var (request, _) in posts)
        {
            var header = Assert.Single(request.Headers.GetValues(A2ATelemetry.TraceParentHeader));
            Assert.True(ActivityContext.TryParse(header, null, out var sent));
            Assert.Equal(trace.TraceId, sent.TraceId);
            var delivery = Assert.Single(deliveries, d => d.SpanId == sent.SpanId);
            Assert.Equal(run.SpanId, delivery.ParentSpanId);
            Assert.Equal(ActivityKind.Client, delivery.Kind);
            Assert.Equal("hooks.test", Tag(delivery, A2ATelemetry.Tags.ServerAddress));
            Assert.Equal("200", Tag(delivery, A2ATelemetry.Tags.HttpStatusCode));
            Assert.NotNull(Tag(delivery, A2ATelemetry.Tags.PushConfigId));
        }
        AssertNoContent(trace.Spans(), "s3cr3t");
        AssertNoContent(trace.Spans(), hook);
    }

    [Fact]
    public async Task Every_operation_has_a_span()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var trace = new TraceCollector();
        var client = host.Client(ProtocolBindings.HttpJson);

        var task = (await client.SendMessageAsync("input")).RequireTask();
        await client.GetTaskAsync(task.Id);
        await client.ListTasksAsync(new ListTasksRequest());
        var config = await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = task.Id, Url = "http://hooks.test/ops" });
        await client.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = task.Id, Id = config.Id! });
        await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = task.Id });
        await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = task.Id, Id = config.Id! });
        await client.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest());

        var slow = (await client.SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser("slow"),
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        })).RequireTask();
        var subscribed = Task.Run(async () =>
        {
            await foreach (var _ in client.SubscribeToTaskAsync(slow.Id)) { }
        });
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (host.Handler.OpenStreams == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        await client.CancelTaskAsync(slow.Id);
        await subscribed;
        await host.WaitIdleAsync();

        string[] ops =
        [
            A2AProtocol.Methods.SendMessage, A2AProtocol.Methods.GetTask, A2AProtocol.Methods.ListTasks, A2AProtocol.Methods.CancelTask,
            A2AProtocol.Methods.SubscribeToTask, A2AProtocol.Methods.CreateTaskPushNotificationConfig, A2AProtocol.Methods.GetTaskPushNotificationConfig,
            A2AProtocol.Methods.ListTaskPushNotificationConfigs, A2AProtocol.Methods.DeleteTaskPushNotificationConfig, A2AProtocol.Methods.GetExtendedAgentCard,
        ];
        foreach (var op in ops)
        {
            var server = await trace.WaitForAsync(a => IsServerOp(a, op));
            var clientSpan = await trace.WaitForAsync(a => IsClientOp(a, op));
            Assert.True(DescendsFrom(server, clientSpan), op);
        }
        var cancelRun = await trace.WaitForAsync(a => a.OperationName == A2ATelemetry.SpanNames.AgentExecution && Tag(a, A2ATelemetry.Tags.ExecutionKind) == "cancel");
        Assert.Equal(slow.Id, Tag(cancelRun, A2ATelemetry.Tags.TaskId));
        var config2 = await trace.WaitForAsync(a => IsServerOp(a, A2AProtocol.Methods.CreateTaskPushNotificationConfig));
        Assert.Equal(config.Id, Tag(config2, A2ATelemetry.Tags.PushConfigId));
    }

    [Fact]
    public async Task Real_http_client_sends_one_traceparent()
    {
        // SocketsHttpHandler propagates the current activity itself (and starts its own span when System.Net.Http is
        // listened to); the SDK's header must not be doubled or replaced by a foreign trace.
        using var httpListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "System.Net.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(httpListener);
        using var trace = new TraceCollector();

        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var server = new System.Net.HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var received = Task.Run(async () =>
        {
            var ctx = await server.GetContextAsync();
            var values = ctx.Request.Headers.GetValues(A2ATelemetry.TraceParentHeader) ?? [];
            var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32001,\"message\":\"nope\"}}"u8.ToArray();
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
            return values;
        });

        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var client = new A2AClient(new A2A.Client.Transports.JsonRpcTransport(http, new Uri($"http://127.0.0.1:{port}/")));
        await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync("t1"));

        var header = Assert.Single(await received);
        Assert.True(ActivityContext.TryParse(header, null, out var sent));
        Assert.Equal(trace.TraceId, sent.TraceId);
    }

    [Fact]
    public void Trace_context_helpers_round_trip()
    {
        var context = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, "k=v");
        var headers = new Dictionary<string, string>();
        A2ATelemetry.InjectTraceContext(context, (k, v) => headers[k] = v);
        Assert.Equal($"00-{context.TraceId.ToHexString()}-{context.SpanId.ToHexString()}-01", headers[A2ATelemetry.TraceParentHeader]);
        Assert.Equal("k=v", headers[A2ATelemetry.TraceStateHeader]);

        Assert.True(A2ATelemetry.TryExtractTraceContext(headers[A2ATelemetry.TraceParentHeader], headers[A2ATelemetry.TraceStateHeader], out var parsed));
        Assert.Equal(context.TraceId, parsed.TraceId);
        Assert.Equal(context.SpanId, parsed.SpanId);
        Assert.Equal("k=v", parsed.TraceState);
        Assert.True(parsed.IsRemote);

        Assert.False(A2ATelemetry.TryExtractTraceContext("not-a-traceparent", null, out _));
        Assert.False(A2ATelemetry.TryExtractTraceContext(null, null, out _));
        headers.Clear();
        A2ATelemetry.InjectTraceContext(default(ActivityContext), (k, v) => headers[k] = v);
        Assert.Empty(headers);
    }
}
