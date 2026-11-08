using System.Collections.Concurrent;
using A2A.Client;
using A2A.Server;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Grpc.Tests;

public class GrpcFilterTests
{
    [Fact]
    public async Task Filter_observes_every_operation_on_grpc()
    {
        var recorder = new RecordingFilter();
        await using var host = await GrpcHost.StartAsync(server: b =>
        {
            b.Services.AddSingleton(recorder);
            b.AddOperationFilter<RecordingFilter>();
        });
        var client = host.Client();

        // Unary: SendMessage, GetTask, ListTasks.
        var task = Assert.IsType<TaskResponse>(await client.SendMessageAsync("task")).Task;
        await client.GetTaskAsync(task.Id);
        await client.ListTasksAsync(new ListTasksRequest());

        // Stream: SendStreamingMessage.
        await foreach (var _ in client.SendStreamingMessageAsync(Message.FromUser("task"))) { }

        // Push config CRUD on an open task.
        var open = Assert.IsType<TaskResponse>(await client.SendMessageAsync("input")).Task;
        var config = await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = open.Id, Url = "http://127.0.0.1:9/cb" });
        await client.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = open.Id, Id = config.Id! });
        await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = open.Id });
        await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = open.Id, Id = config.Id! });

        // Stream: SubscribeToTask, then CancelTask ends it.
        var slow = Assert.IsType<TaskResponse>(await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).Task;
        var first = new TaskCompletionSource();
        var reader = Task.Run(async () =>
        {
            await foreach (var _ in client.SubscribeToTaskAsync(slow.Id)) first.TrySetResult();
        });
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await client.CancelTaskAsync(slow.Id);
        await reader.WaitAsync(TimeSpan.FromSeconds(10));

        await client.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest());

        var seen = recorder.Seen.ToArray();
        Assert.All(seen, d => Assert.Equal(d.Kind.ToString(), d.Name));
        var expected = Enum.GetValues<A2AOperation>().Where(k => k != A2AOperation.Extension).ToHashSet();
        Assert.Equal(11, expected.Count);
        Assert.Equal(expected, seen.Select(d => d.Kind).ToHashSet());
        Assert.Contains(seen, d => ReferenceEquals(d, A2AOperationDescriptor.For(A2AOperation.GetTask)));
        Assert.Contains(seen, d => ReferenceEquals(d, A2AOperationDescriptor.For(A2AOperation.SubscribeToTask)));
    }

    private sealed class RecordingFilter : IA2AOperationFilter
    {
        public ConcurrentQueue<A2AOperationDescriptor> Seen { get; } = new();

        public ValueTask<object?> InvokeAsync(A2AOperationContext context, object request, A2AOperationDelegate next, CancellationToken cancellationToken)
        {
            Seen.Enqueue(context.Operation);
            return next(context, request, cancellationToken);
        }
    }
}
