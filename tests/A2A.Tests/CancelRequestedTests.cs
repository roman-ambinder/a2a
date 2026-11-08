using A2A.Client;
using A2A.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests;

/// <summary>"keep" works and ignores cancellation until released; "ask" waits for input. Neither accepts a CancelTask.</summary>
public sealed class StubbornAgent : IAgentExecutor
{
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
    {
        if (context.UserText == "ask")
        {
            await updater.RequireInputAsync("need more", ct);
            return;
        }
        await updater.StartWorkAsync(cancellationToken: ct);
        await Release.Task; // ignores ct on purpose
        await updater.CompleteAsync("finished anyway", CancellationToken.None);
    }

    public Task CancelAsync(AgentTask task, TaskUpdater updater, CancellationToken cancellationToken) => Task.CompletedTask; // refuses
}

public class CancelRequestedTests
{
    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    private static Task<TestAgentHost> StartAsync(StubbornAgent agent) =>
        TestAgentHost.StartAsync(o => o.CancelGracePeriod = TimeSpan.FromMilliseconds(300),
            services: s => s.Replace(ServiceDescriptor.Singleton<IAgentExecutor>(agent)));

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Slow_cancel_returns_cancel_requested(string binding)
    {
        var agent = new StubbornAgent();
        await using var host = await StartAsync(agent);
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync(Message.FromUser("keep"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();

        var response = await client.CancelTaskAsync(task.Id);

        Assert.Equal(TaskState.Working, response.Status.State);
        Assert.True(response.Metadata![A2AProtocol.CancelRequestedKey].GetBoolean());
        var stored = await client.GetTaskAsync(task.Id);
        Assert.True(stored.Metadata is null || !stored.Metadata.ContainsKey(A2AProtocol.CancelRequestedKey)); // not persisted
        agent.Release.SetResult();
        await host.WaitIdleAsync();
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Refused_cancel_is_not_cancelable(string binding)
    {
        var agent = new StubbornAgent();
        await using var host = await StartAsync(agent);
        var client = host.Client(binding, options: new A2AClientOptions { MaxRetries = 0 });
        var task = (await client.SendMessageAsync("ask")).RequireTask();
        await host.WaitIdleAsync();

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.CancelTaskAsync(task.Id));

        Assert.Equal(A2AErrorCode.TaskNotCancelable, ex.Code);
    }
}
