using A2A.Client;
using A2A.Deletion;
using A2A.Server;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Grpc.Tests;

/// <summary>The task-deletion extension operation (<c>AddTaskDeletion</c>) over gRPC, through the extension service.</summary>
public sealed class GrpcTaskDeletionTests
{
    private static Task<GrpcHost> StartAsync(bool register = true) =>
        GrpcHost.StartAsync(mapExtensions: true, server: b =>
        {
            if (register) b.AddTaskDeletion();
        });

    private static async Task<AgentTask> CompletedTaskAsync(GrpcHost host)
    {
        var task = (await host.Client().SendMessageAsync("task")).RequireTask();
        Assert.Equal(TaskState.Completed, task.Status.State);
        await host.Services.GetRequiredService<A2ARequestHandler>().WhenIdleAsync();
        return task;
    }

    [Fact]
    public async Task The_owner_deletes_a_completed_task_over_gRPC()
    {
        await using var host = await StartAsync();
        var task = await CompletedTaskAsync(host);

        await host.Client().DeleteTaskAsync(task.Id);

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client().GetTaskAsync(task.Id));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
    }

    [Fact]
    public async Task Another_caller_gets_TaskNotFound_and_a_running_task_is_refused_over_gRPC()
    {
        await using var host = await StartAsync();
        var task = await CompletedTaskAsync(host);

        var other = await Assert.ThrowsAsync<A2AException>(() => host.Client("bob").DeleteTaskAsync(task.Id));
        Assert.Equal(A2AErrorCode.TaskNotFound, other.Code);
        Assert.Equal(task.Id, (await host.Client().GetTaskAsync(task.Id)).Id);

        var waiting = (await host.Client().SendMessageAsync("input")).RequireTask();
        var refused = await Assert.ThrowsAsync<A2AException>(() => host.Client().DeleteTaskAsync(waiting.Id));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, refused.Code);
    }

    [Fact]
    public async Task Without_AddTaskDeletion_the_operation_is_unknown_over_gRPC()
    {
        await using var host = await StartAsync(register: false);
        var task = await CompletedTaskAsync(host);

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client().DeleteTaskAsync(task.Id));

        Assert.Equal(A2AErrorCode.MethodNotFound, ex.Code);
        Assert.DoesNotContain(host.Services.GetRequiredService<A2ARequestHandler>().AgentCard.Capabilities.Extensions ?? [],
            e => e.Uri == TaskDeletion.ExtensionUri);
    }
}
