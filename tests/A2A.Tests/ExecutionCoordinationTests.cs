using A2A.Client;
using A2A.Server;
using A2A.Server.Coordination;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>
/// An in-memory coordinator with failures on demand: the first register can fail, and the running check can throw.
/// </summary>
public sealed class FaultyCoordinator(TimeProvider? time = null) : ExecutionCoordinator
{
    private readonly InMemoryExecutionCoordinator _inner = new(new CoordinationOptions(), time);
    private int _failRegisters;
    private int _registerCalls;

    /// <summary>The next <paramref name="count"/> RegisterAsync calls throw.</summary>
    public void FailNextRegisters(int count) => Volatile.Write(ref _failRegisters, count);

    /// <summary>IsRunningAsync throws while true.</summary>
    public bool ThrowOnIsRunning { get; set; }

    public int RegisterCalls => Volatile.Read(ref _registerCalls);

    public override ValueTask RegisterAsync(TaskKey task, string executionId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _registerCalls);
        if (Interlocked.Decrement(ref _failRegisters) >= 0) throw new InvalidOperationException("coordinator unavailable");
        return _inner.RegisterAsync(task, executionId, ct);
    }

    public override ValueTask HeartbeatAsync(TaskKey task, string executionId, CancellationToken ct = default) => _inner.HeartbeatAsync(task, executionId, ct);
    public override ValueTask UnregisterAsync(TaskKey task, string executionId, CancellationToken ct = default) => _inner.UnregisterAsync(task, executionId, ct);

    public override ValueTask<bool> IsRunningAsync(TaskKey task, CancellationToken ct = default) =>
        ThrowOnIsRunning ? throw new InvalidOperationException("coordinator unavailable") : _inner.IsRunningAsync(task, ct);

    /// <summary>The running check of the wrapped coordinator, never throwing (for assertions).</summary>
    public ValueTask<bool> IsRegisteredAsync(TaskKey task) => _inner.IsRunningAsync(task);

    public override ValueTask RequestCancelAsync(TaskKey task, CancellationToken ct = default) => _inner.RequestCancelAsync(task, ct);
    public override IAsyncEnumerable<TaskKey> CancelRequestsAsync(CancellationToken ct) => _inner.CancelRequestsAsync(ct);
    public override ValueTask<IReadOnlyList<(TaskKey Task, string ExecutionId)>> ListExpiredAsync(int max, CancellationToken ct = default) => _inner.ListExpiredAsync(max, ct);
    public override ValueTask<bool> TryAcquireSweepLeaseAsync(string name, TimeSpan duration, CancellationToken ct = default) => _inner.TryAcquireSweepLeaseAsync(name, duration, ct);
}

/// <summary>How the request handler uses the <see cref="ExecutionCoordinator"/> and survives its failures.</summary>
public class ExecutionCoordinationTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting until {what}.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Failed_register_is_retried_on_the_next_heartbeat()
    {
        var clock = new FakeTimeProvider();
        var coordinator = new FaultyCoordinator(clock);
        coordinator.FailNextRegisters(1);
        await using var host = await TestAgentHost.StartAsync(services: s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            s.AddSingleton<ExecutionCoordinator>(coordinator);
        });
        var text = $"gate {Guid.NewGuid():N}";
        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync(Message.FromUser(text),
            new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();
        var key = new TaskKey("", "alice", task.Id);
        Assert.Equal(1, coordinator.RegisterCalls);
        Assert.False(await coordinator.IsRegisteredAsync(key)); // the register failed

        clock.Advance(new CoordinationOptions().Heartbeat);
        await EventuallyAsync(async () => await coordinator.IsRegisteredAsync(key), "the heartbeat registered the execution");
        Assert.Equal(2, coordinator.RegisterCalls);

        ScenarioAgent.Gate(text).TrySetResult();
        await host.WaitIdleAsync();
        Assert.False(await coordinator.IsRegisteredAsync(key));
    }

    [Fact]
    public async Task CancelTask_answers_when_the_coordinator_fails()
    {
        var coordinator = new FaultyCoordinator();
        await using var host = await TestAgentHost.StartAsync(o => o.CancelGracePeriod = TimeSpan.FromMilliseconds(300),
            services: s => s.AddSingleton<ExecutionCoordinator>(coordinator));
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync("input")).RequireTask();
        await host.WaitIdleAsync();

        coordinator.ThrowOnIsRunning = true;
        var canceled = await client.CancelTaskAsync(task.Id);
        Assert.Equal(TaskState.Canceled, canceled.Status.State);
    }

    [Fact]
    public async Task One_throwing_cancellation_callback_does_not_stop_the_others()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.FollowUpPolicy = FollowUpPolicy.AllowSteering);
        var client = host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { MaxRetries = 0 });
        var task = (await client.SendMessageAsync(Message.FromUser("throw-on-cancel first"),
            new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();
        var steer = new Message { MessageId = Message.NewId(), Role = Role.User, TaskId = task.Id, Parts = [Part.FromText("throw-on-cancel second")] };
        await client.SendMessageAsync(steer, new SendMessageConfiguration { ReturnImmediately = true });

        var canceled = await client.CancelTaskAsync(task.Id);

        Assert.Equal(TaskState.Canceled, canceled.Status.State);
        await host.Handler.WhenIdleAsync().WaitAsync(Wait); // both executions stopped
    }

    [Fact]
    public async Task Event_commit_survives_ten_version_conflicts()
    {
        var store = new FlakyTaskStore();
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddTaskStore(store));
        var client = host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { MaxRetries = 0 });
        store.FailNextUpdates(10); // the first event after creation conflicts ten times in a row
        var task = (await client.SendMessageAsync("task")).RequireTask();
        Assert.Equal(TaskState.Completed, task.Status.State);
    }
}
