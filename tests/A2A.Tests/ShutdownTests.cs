using System.Security.Claims;
using A2A.Server;
using A2A.Server.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests;

/// <summary>
/// A host stop is not a cancel: running tasks stay non-terminal when only the engine's shutdown stops their agent, and the
/// drain service waits for agents to finish first.
/// </summary>
public sealed class ShutdownTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static ServerCallContext Call() => new()
    {
        Principal = "alice",
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "Test")),
    };

    /// <summary>Works until its token fires (and never finishes by itself), or until <see cref="Release"/> is set.</summary>
    private sealed class Worker
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAgentExecutor Executor => new DelegateAgentExecutor(async (context, u, ct) =>
        {
            await u.StartWorkAsync("working", ct);
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            await u.CompleteAsync("done", CancellationToken.None);
        });
    }

    private static Task<TestAgentHost> StartAsync(Worker worker) =>
        TestAgentHost.StartAsync(services: s => s.Replace(ServiceDescriptor.Singleton(worker.Executor)));

    private static async Task<AgentTask> SendAsync(TestAgentHost host, string text = "go") =>
        (await host.Handler.SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser(text),
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        }, Call()).WaitAsync(Wait)).RequireTask();

    private static Task<AgentTask> GetAsync(TestAgentHost host, string id) =>
        host.Handler.GetTaskAsync(new GetTaskRequest { Id = id }, Call());

    [Fact]
    public async Task Shutdown_leaves_a_running_task_non_terminal()
    {
        var worker = new Worker();
        await using var host = await StartAsync(worker);
        var task = await SendAsync(host);
        await worker.Started.Task.WaitAsync(Wait);

        host.Engine.Shutdown();
        await host.Engine.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal(TaskState.Working, (await GetAsync(host, task.Id)).Status.State);
    }

    [Fact]
    public async Task Client_cancel_still_ends_canceled()
    {
        var worker = new Worker();
        await using var host = await StartAsync(worker);
        var task = await SendAsync(host);
        await worker.Started.Task.WaitAsync(Wait);

        await host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = task.Id }, Call()).WaitAsync(Wait);
        await host.Engine.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal(TaskState.Canceled, (await GetAsync(host, task.Id)).Status.State);
    }

    [Fact]
    public async Task Client_cancel_during_shutdown_still_ends_canceled()
    {
        var worker = new Worker();
        await using var host = await StartAsync(worker);
        var task = await SendAsync(host);
        await worker.Started.Task.WaitAsync(Wait);

        host.Engine.Shutdown();
        await host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = task.Id }, Call()).WaitAsync(Wait);
        await host.Engine.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal(TaskState.Canceled, (await GetAsync(host, task.Id)).Status.State);
    }

    [Fact]
    public async Task Shutdown_leaves_a_continuation_non_terminal()
    {
        var worker = new Worker();
        await using var host = await TestAgentHost.StartAsync(services: s => s.Replace(ServiceDescriptor.Singleton<IAgentExecutor>(
            new DelegateAgentExecutor(async (context, u, ct) =>
            {
                if (context.IsNewTask) { await u.RequireInputAsync("more?", ct); return; }
                await u.StartWorkAsync("working", ct);
                worker.Started.TrySetResult();
                await worker.Release.Task.WaitAsync(ct);
            }))));
        var first = (await host.Handler.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("go") }, Call()).WaitAsync(Wait)).RequireTask();
        Assert.Equal(TaskState.InputRequired, first.Status.State);
        await host.Handler.SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser("more", taskId: first.Id),
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        }, Call()).WaitAsync(Wait);
        await worker.Started.Task.WaitAsync(Wait);

        host.Engine.Shutdown();
        await host.Engine.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal(TaskState.Working, (await GetAsync(host, first.Id)).Status.State);
    }

    [Fact]
    public async Task Drain_waits_for_an_agent_that_finishes_in_time()
    {
        var worker = new Worker();
        var clock = new FakeTimeProvider();
        await using var host = await StartAsync(worker);
        var task = await SendAsync(host);
        await worker.Started.Task.WaitAsync(Wait);
        var drain = new TaskExecutionDrainService(host.Engine, new A2AServerOptions(), null, clock);

        var stopping = drain.StopAsync(CancellationToken.None);
        Assert.False(stopping.IsCompleted);
        worker.Release.SetResult();
        await stopping.WaitAsync(Wait);

        Assert.Equal(TaskState.Completed, (await GetAsync(host, task.Id)).Status.State);
    }

    [Fact]
    public async Task Drain_gives_up_at_the_timeout_and_leaves_the_task_running()
    {
        var worker = new Worker();
        var clock = new FakeTimeProvider();
        await using var host = await StartAsync(worker);
        var task = await SendAsync(host);
        await worker.Started.Task.WaitAsync(Wait);
        var drain = new TaskExecutionDrainService(host.Engine, new A2AServerOptions { ShutdownDrainTimeout = TimeSpan.FromSeconds(20) }, null, clock);

        var stopping = drain.StopAsync(CancellationToken.None);
        Assert.False(stopping.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(19));
        Assert.False(stopping.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await stopping.WaitAsync(Wait);

        Assert.Equal(TaskState.Working, (await GetAsync(host, task.Id)).Status.State);
        host.Engine.Shutdown();
        await host.Engine.WhenIdleAsync().WaitAsync(Wait);
        Assert.Equal(TaskState.Working, (await GetAsync(host, task.Id)).Status.State);
    }

    [Fact]
    public async Task Drain_with_zero_timeout_does_not_wait()
    {
        var worker = new Worker();
        await using var host = await StartAsync(worker);
        await SendAsync(host);
        await worker.Started.Task.WaitAsync(Wait);
        var drain = new TaskExecutionDrainService(host.Engine, new A2AServerOptions { ShutdownDrainTimeout = TimeSpan.Zero }, null, null);

        await drain.StopAsync(CancellationToken.None).WaitAsync(Wait);

        host.Engine.Shutdown();
        await host.Engine.WhenIdleAsync().WaitAsync(Wait);
    }

    [Fact]
    public async Task Drain_honours_the_stop_token()
    {
        var worker = new Worker();
        await using var host = await StartAsync(worker);
        await SendAsync(host);
        await worker.Started.Task.WaitAsync(Wait);
        var drain = new TaskExecutionDrainService(host.Engine, new A2AServerOptions(), null, new FakeTimeProvider());
        using var stop = new CancellationTokenSource();

        var stopping = drain.StopAsync(stop.Token);
        Assert.False(stopping.IsCompleted);
        stop.Cancel();
        await stopping.WaitAsync(Wait);

        host.Engine.Shutdown();
        await host.Engine.WhenIdleAsync().WaitAsync(Wait);
    }

    [Fact]
    public async Task Drain_service_is_registered()
    {
        await using var host = await TestAgentHost.StartAsync();
        Assert.Contains(host.App.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), s => s is TaskExecutionDrainService);
    }
}
