using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Grpc.Core;
using P = A2a.V1;

namespace A2A.V0_3Compat.Tests;

/// <summary>
/// A v0.3 call refused before the operation pipeline (a wrong A2A-Version, or the authorization middleware) still gets its
/// operation span, under the v1.0 name of the operation, as on the v1.0 bindings. Tests run in parallel, so each one
/// sends its own trace context and looks only at its trace.
/// </summary>
public class RefusedCallTelemetryTests
{
    private sealed class Spans : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly ConcurrentQueue<Activity> _stopped = new();

        public Spans()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == "A2A.Server",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = _stopped.Enqueue,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public ActivityTraceId Trace { get; } = ActivityTraceId.CreateRandom();

        public string TraceParent => $"00-{Trace.ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01";

        public async Task<Activity> SingleAsync()
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!_stopped.Any(a => a.TraceId == Trace) && DateTime.UtcNow < until) await Task.Delay(10);
            await Task.Delay(50); // a second span of the trace would follow at once
            return Assert.Single(_stopped, a => a.TraceId == Trace);
        }

        public void Dispose() => _listener.Dispose();
    }

    private static string? Tag(Activity a, string name) => a.GetTagItem(name)?.ToString();

    [Fact]
    public async Task A_rest_call_with_a_wrong_version_has_a_span_named_after_its_operation()
    {
        using var spans = new Spans();
        await using var host = await V03Host.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/a2a/v1/tasks/t-1");
        request.Headers.Add("A2A-Version", "0.9");
        request.Headers.Add("traceparent", spans.TraceParent);

        using var response = await host.Http().SendAsync(request);

        Assert.False(response.IsSuccessStatusCode);
        var span = await spans.SingleAsync();
        Assert.Equal("GetTask", span.OperationName);
        Assert.Equal(ProtocolBindings.HttpJson, Tag(span, "a2a.binding"));
        Assert.Equal(nameof(A2AErrorCode.VersionNotSupported), Tag(span, "a2a.error.code"));
        Assert.Equal(ActivityKind.Server, span.Kind);
    }

    [Fact]
    public async Task A_json_rpc_call_with_a_wrong_version_has_a_span_named_after_its_v1_operation()
    {
        using var spans = new Spans();
        await using var host = await V03Host.StartAsync();
        var http = host.Http();
        http.DefaultRequestHeaders.Add("traceparent", spans.TraceParent);

        var (response, _) = await V03Host.RpcAsync(http, "tasks/get", new JsonObject { ["id"] = "t-1" }, version: "0.9");

        Assert.True(response.IsSuccessStatusCode || (int)response.StatusCode >= 400);
        var span = await spans.SingleAsync();
        Assert.Equal("GetTask", span.OperationName);
        Assert.Equal(ProtocolBindings.JsonRpc, Tag(span, "a2a.binding"));
        Assert.Equal(nameof(A2AErrorCode.VersionNotSupported), Tag(span, "a2a.error.code"));
    }

    [Fact]
    public async Task A_grpc_call_with_a_wrong_version_has_a_span_named_after_its_v1_operation()
    {
        using var spans = new Spans();
        await using var host = await V03Host.StartAsync(grpc: true);
        var stub = new P.A2AService.A2AServiceClient(host.GrpcChannel());

        await Assert.ThrowsAsync<RpcException>(async () => await stub.TaskSubscription(new P.TaskSubscriptionRequest { Name = "tasks/t-1" },
            new Metadata { { "a2a-version", "0.9" }, { "traceparent", spans.TraceParent } }).ResponseStream.MoveNext());

        var span = await spans.SingleAsync();
        Assert.Equal("SubscribeToTask", span.OperationName);
        Assert.Equal(ProtocolBindings.Grpc, Tag(span, "a2a.binding"));
        Assert.Equal(nameof(A2AErrorCode.VersionNotSupported), Tag(span, "a2a.error.code"));
    }

    [Fact]
    public async Task An_anonymous_grpc_call_refused_by_the_authorization_middleware_has_a_span()
    {
        using var spans = new Spans();
        await using var host = await V03Host.StartAsync(grpc: true);
        var stub = new P.A2AService.A2AServiceClient(host.GrpcChannel(user: null));

        await Assert.ThrowsAsync<RpcException>(async () => await stub.GetAgentCardAsync(new P.GetAgentCardRequest(),
            new Metadata { { "traceparent", spans.TraceParent } }));

        var span = await spans.SingleAsync();
        Assert.Equal("GetExtendedAgentCard", span.OperationName);
        Assert.Equal(nameof(A2AErrorCode.Unauthenticated), Tag(span, "a2a.error.code"));
    }
}
