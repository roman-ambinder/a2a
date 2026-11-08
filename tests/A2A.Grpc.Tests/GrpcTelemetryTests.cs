using System.Collections.Concurrent;
using System.Diagnostics;
using A2A.Client;
using A2A.Client.Grpc;
using A2A.Client.Telemetry;
using A2A.Server.Telemetry;

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
            ShouldListenTo = s => s.Name is A2AClientTelemetry.ActivitySourceName or A2AServerTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var client = host.Client();

        await client.SendMessageAsync("hello");
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync("missing"));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);

        var send = Assert.Single(stopped, a => a.OperationName == "a2a.client.SendMessage");
        Assert.Equal(ActivityKind.Client, send.Kind);
        Assert.Equal("SendMessage", send.GetTagItem("a2a.operation"));
        Assert.Equal("ok", send.GetTagItem("a2a.outcome"));

        var get = Assert.Single(stopped, a => a.OperationName == "a2a.client.GetTask");
        Assert.Equal("error:TaskNotFound", get.GetTagItem("a2a.outcome"));
        Assert.Equal(ActivityStatusCode.Error, get.Status);

        // The server side of the same calls. (Joining the client's trace needs a socket handler, which injects traceparent,
        // and ASP.NET Core's request activity, which reads it; the TestServer handler has neither.)
        Assert.Single(stopped, a => a.OperationName == "a2a.server.SendMessage");
        var serverGet = Assert.Single(stopped, a => a.OperationName == "a2a.server.GetTask");
        Assert.Equal("error:TaskNotFound", serverGet.GetTagItem("a2a.outcome"));
    }

    [Fact]
    public async Task Grpc_caller_cancellation_is_canceled()
    {
        await using var host = await GrpcHost.StartAsync();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == A2AClientTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var transport = new GrpcTransport(host.Channel("alice").CreateCallInvoker());
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => transport.GetTaskAsync(new GetTaskRequest { Id = "t-1" }, cancellationToken: canceled.Token));

        var get = Assert.Single(stopped, a => a.OperationName == "a2a.client.GetTask");
        Assert.Equal("canceled", get.GetTagItem("a2a.outcome"));
        Assert.NotEqual(ActivityStatusCode.Error, get.Status);
    }

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
