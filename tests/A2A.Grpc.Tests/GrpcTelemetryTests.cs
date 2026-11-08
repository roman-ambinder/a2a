using System.Collections.Concurrent;
using System.Diagnostics;
using A2A.Client;
using A2A.Client.Grpc;
using A2A.Diagnostics;

namespace A2A.Grpc.Tests;

/// <summary>Activity listeners are process-wide: these tests never run next to other tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GrpcTelemetryCollection
{
    public const string Name = "GrpcTelemetry";
}

[Collection(GrpcTelemetryCollection.Name)]
public sealed class GrpcTelemetryTests
{
    [Fact]
    public async Task Grpc_client_span_present()
    {
        await using var host = await GrpcHost.StartAsync();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is A2ATelemetry.ClientSourceName or A2ATelemetry.ServerSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var client = host.Client();

        await client.SendMessageAsync("hello");
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync("missing"));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);

        // Client spans come from A2AClient (main's design: one span per A2AClient operation, retries included).
        var send = Assert.Single(stopped, a => IsSpan(a, A2ATelemetry.ClientSourceName, "a2a.SendMessage"));
        Assert.Equal(ActivityKind.Client, send.Kind);
        Assert.Equal("SendMessage", send.GetTagItem(A2ATelemetry.Tags.Operation));
        Assert.Equal(ProtocolBindings.Grpc, send.GetTagItem(A2ATelemetry.Tags.Binding));
        Assert.NotEqual(ActivityStatusCode.Error, send.Status);

        var get = Assert.Single(stopped, a => IsSpan(a, A2ATelemetry.ClientSourceName, "a2a.GetTask"));
        Assert.Equal(nameof(A2AErrorCode.TaskNotFound), get.GetTagItem(A2ATelemetry.Tags.ErrorCode));
        Assert.Equal(ActivityStatusCode.Error, get.Status);

        // The server side of the same calls; the server reads the traceparent from the gRPC metadata.
        var serverSend = Assert.Single(stopped, a => IsSpan(a, A2ATelemetry.ServerSourceName, "a2a.SendMessage"));
        Assert.Equal(send.TraceId, serverSend.TraceId);
        var serverGet = Assert.Single(stopped, a => IsSpan(a, A2ATelemetry.ServerSourceName, "a2a.GetTask"));
        Assert.Equal(nameof(A2AErrorCode.TaskNotFound), serverGet.GetTagItem(A2ATelemetry.Tags.ErrorCode));
        Assert.Equal(ActivityStatusCode.Error, serverGet.Status);
    }

    [Fact]
    public async Task Grpc_caller_cancellation_is_canceled()
    {
        await using var host = await GrpcHost.StartAsync();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == A2ATelemetry.ClientSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var client = new A2AClient(new GrpcTransport(host.Channel("alice").CreateCallInvoker()));
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetTaskAsync(new GetTaskRequest { Id = "t-1" }, cancellationToken: canceled.Token));

        var get = Assert.Single(stopped, a => a.OperationName == "a2a.GetTask");
        Assert.Equal(true, get.GetTagItem(A2ATelemetry.ErrorTypeCanceledTag));
        Assert.NotEqual(ActivityStatusCode.Error, get.Status);
    }

    private static bool IsSpan(Activity a, string source, string name) => a.Source.Name == source && a.OperationName == name;

    [Fact]
    public async Task Unread_grpc_stream_sends_nothing()
    {
        await using var host = await GrpcHost.StartAsync();
        var transport = new GrpcTransport(host.Channel("alice").CreateCallInvoker());

        var stream = transport.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("task") });
        await Task.Delay(300);
        Assert.Empty((await transport.ListTasksAsync(new ListTasksRequest())).Tasks); // the server never received the message

        await foreach (var _ in stream) { }
        Assert.Single((await transport.ListTasksAsync(new ListTasksRequest())).Tasks);
    }
}
