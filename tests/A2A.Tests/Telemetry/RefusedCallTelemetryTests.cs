using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using A2A.Client;
using A2A.Diagnostics;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace A2A.Tests.Telemetry;

/// <summary>
/// Calls refused before the operation pipeline (by ASP.NET Core's authorization middleware, by call validation, or by the
/// binding itself) still get their operation span, one <c>a2a.server.requests</c> count and, for an authentication or
/// authorization refusal, one security event with its log line. A call that reaches the pipeline is counted there once.
/// Also: the operation span is a Server span when the current activity is one nobody records (ASP.NET Core's request
/// activity without a listener), so a ParentBased sampler keeps it.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class RefusedCallTelemetryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson, ProtocolBindings.Grpc };

    // ---------------------------------------------------------------- listeners

    private sealed class Spans : IDisposable
    {
        private readonly ActivityListener _listener;

        public Spans(SampleActivity<ActivityContext>? sample = null, params string[] sources)
        {
            var names = sources.Length == 0 ? [A2ATelemetry.ServerSourceName] : sources;
            _listener = new ActivityListener
            {
                ShouldListenTo = s => names.Contains(s.Name),
                Sample = sample ?? ((ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded),
                ActivityStopped = a => Stopped.Enqueue(a),
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public ConcurrentQueue<Activity> Stopped { get; } = new();

        public Activity[] Server => Stopped.Where(a => a.Source.Name == A2ATelemetry.ServerSourceName).ToArray();

        public async Task<Activity> WaitForAsync(Func<Activity, bool> match)
        {
            var until = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < until)
            {
                if (Stopped.FirstOrDefault(match) is { } found) return found;
                await Task.Delay(10);
            }

            throw new Xunit.Sdk.XunitException("No matching span. Seen: " +
                                               string.Join(", ", Stopped.Select(s => $"{s.Source.Name}:{s.OperationName}:{s.Kind}")));
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>OpenTelemetry's default sampler, ParentBased(AlwaysOn): a root is sampled, a child follows its parent's Recorded flag.</summary>
    private static ActivitySamplingResult ParentBased(ref ActivityCreationOptions<ActivityContext> options) =>
        options.Parent == default || options.Parent.TraceFlags.HasFlag(ActivityTraceFlags.Recorded)
            ? ActivitySamplingResult.AllDataAndRecorded
            : ActivitySamplingResult.None;

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

        public (long Value, Dictionary<string, object?> Tags)[] Of(string name) =>
            Measurements.Where(m => m.Name == name).Select(m => (m.Value, m.Tags)).ToArray();

        public long Sum(string name) => Measurements.Where(m => m.Name == name).Sum(m => m.Value);

        public async Task WaitForAsync(string name, long count)
        {
            var until = DateTime.UtcNow + Timeout;
            while (Sum(name) < count && DateTime.UtcNow < until) await Task.Delay(10);
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class CaptureProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, int Id, string Message)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
        public void Dispose() { }

        public (string Category, LogLevel Level, int Id, string Message)[] Security =>
            Entries.Where(e => e.Id is 1200 or 1201).ToArray();

        private sealed class Sink(CaptureProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue((category, logLevel, eventId.Id, formatter(state, exception)));
        }
    }

    private sealed class Headers(params (string Name, string Value)[] headers) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            foreach (var (name, value) in headers) request.Headers.Add(name, value);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static Action<IServiceCollection> Logging(CaptureProvider log) => s => s.AddLogging(b => b.AddProvider(log));

    private static string? Tag(Activity a, string name) => a.GetTagItem(name)?.ToString();

    private static object? Tag(Dictionary<string, object?> tags, string name) => tags.GetValueOrDefault(name);

    /// <summary>The operation name a binding knows before it reads the request: JSON-RPC names the method in the body.</summary>
    private static string Expected(string binding, string operation, bool bodyRead) =>
        binding == ProtocolBindings.JsonRpc && !bodyRead ? "_OTHER" : operation;

    private static async Task ExpectFailureAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception)
        {
            return; // the client's view is covered elsewhere; this test is about the server's telemetry
        }

        throw new Xunit.Sdk.XunitException("The call was expected to fail.");
    }

    // ---------------------------------------------------------------- #1: refusals before the pipeline

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Anonymous_caller_refused_by_the_authorization_middleware_is_traced_counted_and_logged(string binding)
    {
        var log = new CaptureProvider();
        await using var host = await TestAgentHost.StartAsync(services: Logging(log), grpc: true);
        using var spans = new Spans();
        using var counters = new Counters();

        await ExpectFailureAsync(() => host.Client(binding, user: null).GetTaskAsync("t-anon"));

        var operation = Expected(binding, A2AProtocol.Methods.GetTask, bodyRead: false);
        var span = await spans.WaitForAsync(a => a.Source.Name == A2ATelemetry.ServerSourceName);
        Assert.Single(spans.Server);
        Assert.Equal(operation, span.OperationName);
        Assert.Equal(operation, Tag(span, A2ATelemetry.Tags.Operation));
        Assert.Equal(binding, Tag(span, A2ATelemetry.Tags.Binding));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(nameof(A2AErrorCode.Unauthenticated), Tag(span, A2ATelemetry.Tags.ErrorCode));
        Assert.Equal("UNAUTHENTICATED", Tag(span, A2ATelemetry.Tags.ErrorType));

        await counters.WaitForAsync(A2ATelemetry.Metrics.Requests, 1);
        var request = Assert.Single(counters.Of(A2ATelemetry.Metrics.Requests));
        Assert.Equal(1, request.Value);
        Assert.Equal(operation, Tag(request.Tags, A2ATelemetry.Tags.Operation));
        Assert.Equal(A2ATelemetry.Outcomes.Error, Tag(request.Tags, A2ATelemetry.Tags.Outcome));
        Assert.Equal("UNAUTHENTICATED", Tag(request.Tags, A2ATelemetry.Tags.ErrorType));

        var security = Assert.Single(counters.Of(A2ATelemetry.Metrics.SecurityEvents));
        Assert.Equal(A2ATelemetry.SecurityEventNames.Unauthenticated, Tag(security.Tags, A2ATelemetry.Tags.SecurityEvent));
        // The method name only when it is known: a JSON-RPC body is not read before authorization.
        Assert.Equal(binding == ProtocolBindings.JsonRpc ? null : operation, Tag(security.Tags, A2ATelemetry.Tags.Operation));
        var line = Assert.Single(log.Security);
        Assert.Equal(1200, line.Id);
        Assert.Equal(LogLevel.Information, line.Level);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Caller_forbidden_by_the_authorization_middleware_is_a_permission_denied_event(string binding)
    {
        var log = new CaptureProvider();
        // RequireAuthorization() uses the default policy: authenticated, and (here) an admin, which alice is not.
        await using var host = await TestAgentHost.StartAsync(services: s =>
        {
            Logging(log)(s);
            s.AddAuthorization(o => o.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().RequireClaim("role", "admin").Build());
        }, grpc: true);
        using var spans = new Spans();
        using var counters = new Counters();

        await ExpectFailureAsync(() => host.Client(binding).GetTaskAsync("t-forbidden"));

        var operation = Expected(binding, A2AProtocol.Methods.GetTask, bodyRead: false);
        var span = await spans.WaitForAsync(a => a.Source.Name == A2ATelemetry.ServerSourceName);
        Assert.Single(spans.Server);
        Assert.Equal(operation, span.OperationName);
        Assert.Equal(nameof(A2AErrorCode.PermissionDenied), Tag(span, A2ATelemetry.Tags.ErrorCode));
        await counters.WaitForAsync(A2ATelemetry.Metrics.Requests, 1);
        var request = Assert.Single(counters.Of(A2ATelemetry.Metrics.Requests));
        Assert.Equal("PERMISSION_DENIED", Tag(request.Tags, A2ATelemetry.Tags.ErrorType));
        var security = Assert.Single(counters.Of(A2ATelemetry.Metrics.SecurityEvents));
        Assert.Equal(A2ATelemetry.SecurityEventNames.PermissionDenied, Tag(security.Tags, A2ATelemetry.Tags.SecurityEvent));
        var line = Assert.Single(log.Security);
        Assert.Equal(1201, line.Id);
        Assert.Equal(LogLevel.Warning, line.Level);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Unsupported_version_refused_by_call_validation_is_traced_and_counted(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(grpc: true);
        using var spans = new Spans();
        using var counters = new Counters();

        await ExpectFailureAsync(() => host.Client(binding, options: new A2AClientOptions { ProtocolVersion = "0.9" }).GetTaskAsync("t-version"));

        // JSON-RPC reads the method from the envelope before it validates the call.
        var operation = A2AProtocol.Methods.GetTask;
        var span = await spans.WaitForAsync(a => a.Source.Name == A2ATelemetry.ServerSourceName);
        Assert.Single(spans.Server);
        Assert.Equal(operation, span.OperationName);
        Assert.Equal(nameof(A2AErrorCode.VersionNotSupported), Tag(span, A2ATelemetry.Tags.ErrorCode));
        await counters.WaitForAsync(A2ATelemetry.Metrics.Requests, 1);
        var request = Assert.Single(counters.Of(A2ATelemetry.Metrics.Requests));
        Assert.Equal(operation, Tag(request.Tags, A2ATelemetry.Tags.Operation));
        Assert.Equal("VERSION_NOT_SUPPORTED", Tag(request.Tags, A2ATelemetry.Tags.ErrorType));
        Assert.Empty(counters.Of(A2ATelemetry.Metrics.SecurityEvents));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Authenticated_caller_without_a_principal_is_an_unauthenticated_event(string binding)
    {
        var log = new CaptureProvider();
        await using var host = await TestAgentHost.StartAsync(services: Logging(log), grpc: true);
        using var spans = new Spans();
        using var counters = new Counters();

        // X-Test-NoId: the caller is authenticated, but its identity has no claim the principal resolver accepts.
        var handler = new Headers(("X-Test-User", "bob"), ("X-Test-NoId", "1")) { InnerHandler = host.App.GetTestServer().CreateHandler() };
        var options = new A2AClientOptions();
        A2A.Client.IA2ATransport transport = binding switch
        {
            ProtocolBindings.JsonRpc => new A2A.Client.Transports.JsonRpcTransport(new HttpClient(handler), new Uri(TestAgentHost.BaseUrl), options),
            ProtocolBindings.HttpJson => new A2A.Client.Transports.HttpJsonTransport(new HttpClient(handler), new Uri(TestAgentHost.BaseUrl), options),
            _ => new A2A.Client.Grpc.GrpcTransport(global::Grpc.Net.Client.GrpcChannel.ForAddress(host.App.GetTestServer().BaseAddress,
                new global::Grpc.Net.Client.GrpcChannelOptions { HttpHandler = handler }).CreateCallInvoker(), options),
        };
        await ExpectFailureAsync(() => new A2AClient(transport, options, TestAgentHost.DefaultCard()).GetTaskAsync("t-noid"));

        var span = await spans.WaitForAsync(a => a.Source.Name == A2ATelemetry.ServerSourceName);
        Assert.Single(spans.Server);
        Assert.Equal(nameof(A2AErrorCode.Unauthenticated), Tag(span, A2ATelemetry.Tags.ErrorCode));
        await counters.WaitForAsync(A2ATelemetry.Metrics.Requests, 1);
        Assert.Equal(1, counters.Sum(A2ATelemetry.Metrics.Requests));
        var security = Assert.Single(counters.Of(A2ATelemetry.Metrics.SecurityEvents));
        Assert.Equal(A2ATelemetry.SecurityEventNames.Unauthenticated, Tag(security.Tags, A2ATelemetry.Tags.SecurityEvent));
        Assert.Equal(1200, Assert.Single(log.Security).Id);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task A_call_that_reaches_the_pipeline_is_counted_once(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(grpc: true);
        using var spans = new Spans();
        using var counters = new Counters();

        await ExpectFailureAsync(() => host.Client(binding).GetTaskAsync("t-missing"));

        var span = await spans.WaitForAsync(a => a.Source.Name == A2ATelemetry.ServerSourceName);
        Assert.Equal(A2AProtocol.Methods.GetTask, span.OperationName);
        Assert.Equal(nameof(A2AErrorCode.TaskNotFound), Tag(span, A2ATelemetry.Tags.ErrorCode));
        await counters.WaitForAsync(A2ATelemetry.Metrics.Requests, 1);
        await Task.Delay(100); // a second count would come right after the first
        Assert.Single(spans.Server);
        var request = Assert.Single(counters.Of(A2ATelemetry.Metrics.Requests));
        Assert.Equal("TASK_NOT_FOUND", Tag(request.Tags, A2ATelemetry.Tags.ErrorType));
        Assert.Empty(counters.Of(A2ATelemetry.Metrics.SecurityEvents));
    }

    [Fact]
    public async Task Unknown_json_rpc_method_is_counted_under_the_fixed_fallback_name()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var spans = new Spans();
        using var counters = new Counters();

        var http = host.Http("alice");
        http.DefaultRequestHeaders.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        using var response = await http.PostAsync("/a2a", new StringContent("""{"jsonrpc":"2.0","id":1,"method":"NoSuchMethod-4711","params":{}}""",
            System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var span = await spans.WaitForAsync(a => a.Source.Name == A2ATelemetry.ServerSourceName);
        Assert.Equal("_OTHER", span.OperationName);
        Assert.Equal(nameof(A2AErrorCode.MethodNotFound), Tag(span, A2ATelemetry.Tags.ErrorCode));
        await counters.WaitForAsync(A2ATelemetry.Metrics.Requests, 1);
        Assert.Equal("_OTHER", Tag(Assert.Single(counters.Of(A2ATelemetry.Metrics.Requests)).Tags, A2ATelemetry.Tags.Operation));
    }

    [Fact]
    public async Task A_call_the_rate_limit_rejects_is_traced_and_counted_without_a_security_event()
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddA2ARateLimiting(o =>
        {
            o.TokenLimit = 1;
            o.TokensPerPeriod = 1;
            o.ReplenishmentPeriod = TimeSpan.FromHours(1);
        }));
        using var spans = new Spans();
        using var counters = new Counters();

        await GetMissingTaskAsync(host, "t-limit-1");
        var http = host.Http("alice");
        http.DefaultRequestHeaders.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        using var limited = await http.GetAsync("/a2a/tasks/t-limit-2");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        var span = await spans.WaitForAsync(a => Tag(a, A2ATelemetry.Tags.ErrorCode) == nameof(A2AErrorCode.RateLimited));
        Assert.Equal(A2AProtocol.Methods.GetTask, span.OperationName);
        await counters.WaitForAsync(A2ATelemetry.Metrics.Requests, 2);
        Assert.Equal(["RATE_LIMITED", "TASK_NOT_FOUND"],
            counters.Of(A2ATelemetry.Metrics.Requests).Select(m => (string)Tag(m.Tags, A2ATelemetry.Tags.ErrorType)!).Order());
        Assert.Empty(counters.Of(A2ATelemetry.Metrics.SecurityEvents));
    }

    // ---------------------------------------------------------------- #2: span kind and sampling under ASP.NET Core

    private const string AspNetCoreSource = "Microsoft.AspNetCore";

    /// <summary>A REST GetTask of a task that does not exist (the span still ends with its error), with an optional traceparent.</summary>
    private static async Task GetMissingTaskAsync(TestAgentHost host, string taskId, string? traceParent = null)
    {
        var http = host.Http("alice");
        http.DefaultRequestHeaders.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        if (traceParent is not null) http.DefaultRequestHeaders.Add(A2ATelemetry.TraceParentHeader, traceParent);
        using var response = await http.GetAsync($"/a2a/tasks/{taskId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string TraceParent(ActivityTraceId trace, ActivitySpanId span, bool sampled) =>
        $"00-{trace.ToHexString()}-{span.ToHexString()}-{(sampled ? "01" : "00")}";

    [Fact]
    public async Task Without_an_aspnetcore_listener_an_untraced_call_gets_a_sampled_server_root_span()
    {
        // Logging on: ASP.NET Core then creates its request activity although nobody listens to it.
        await using var host = await TestAgentHost.StartAsync(services: Logging(new CaptureProvider()));
        using var spans = new Spans(ParentBased);

        await GetMissingTaskAsync(host, "t-root");

        var span = await spans.WaitForAsync(a => Tag(a, A2ATelemetry.Tags.TaskId) == "t-root");
        Assert.Equal(ActivityKind.Server, span.Kind);
        Assert.True(span.Recorded);
        Assert.Equal(default, span.ParentSpanId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Without_an_aspnetcore_listener_the_server_span_continues_the_callers_traceparent(bool sampled)
    {
        await using var host = await TestAgentHost.StartAsync(services: Logging(new CaptureProvider()));
        using var spans = new Spans(ParentBased);
        var trace = ActivityTraceId.CreateRandom();
        var parent = ActivitySpanId.CreateRandom();

        await GetMissingTaskAsync(host, "t-remote-" + sampled, TraceParent(trace, parent, sampled));

        if (!sampled)
        {
            // ParentBased follows the caller's decision: nothing is recorded.
            await Task.Delay(200);
            Assert.DoesNotContain(spans.Stopped, a => a.TraceId == trace);
            return;
        }

        var span = await spans.WaitForAsync(a => Tag(a, A2ATelemetry.Tags.TaskId) == "t-remote-True");
        Assert.Equal(ActivityKind.Server, span.Kind);
        Assert.Equal(trace, span.TraceId);
        Assert.Equal(parent, span.ParentSpanId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task With_an_aspnetcore_listener_the_span_is_internal_under_the_request_span(bool withTraceParent)
    {
        await using var host = await TestAgentHost.StartAsync(services: Logging(new CaptureProvider()));
        using var spans = new Spans(ParentBased, A2ATelemetry.ServerSourceName, AspNetCoreSource);
        var trace = ActivityTraceId.CreateRandom();
        var taskId = "t-nested-" + withTraceParent;

        await GetMissingTaskAsync(host, taskId,
            withTraceParent ? TraceParent(trace, ActivitySpanId.CreateRandom(), sampled: true) : null);

        var span = await spans.WaitForAsync(a => Tag(a, A2ATelemetry.Tags.TaskId) == taskId);
        var request = await spans.WaitForAsync(a => a.Source.Name == AspNetCoreSource && a.SpanId == span.ParentSpanId);
        Assert.Equal(ActivityKind.Internal, span.Kind);
        Assert.Equal(ActivityKind.Server, request.Kind);
        Assert.Equal(request.TraceId, span.TraceId);
        if (withTraceParent) Assert.Equal(trace, span.TraceId);
    }
}
