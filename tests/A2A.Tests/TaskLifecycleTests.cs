using A2A.Server;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using static A2A.Tests.TaskStoreTests;

namespace A2A.Tests;

/// <summary>Wraps a real store and throws <see cref="ConcurrencyException"/> from the next N updates.</summary>
public sealed class FlakyTaskStore : TaskStore
{
    private readonly InMemoryTaskStore _inner = new();
    private int _failures;

    public void FailNextUpdates(int count) => Volatile.Write(ref _failures, count);

    public override ValueTask<TaskRecord?> GetAsync(TaskKey key, CancellationToken ct = default) => _inner.GetAsync(key, ct);
    public override ValueTask<TaskRecord> CreateAsync(TaskRecord record, CancellationToken ct = default) => _inner.CreateAsync(record, ct);

    public override ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion, CancellationToken ct = default)
    {
        if (Interlocked.Decrement(ref _failures) >= 0) throw new ConcurrencyException(record.Key, expectedVersion);
        return _inner.UpdateAsync(record, expectedVersion, ct);
    }

    public override ValueTask<TaskPage> ListAsync(TaskQuery query, CancellationToken ct = default) => _inner.ListAsync(query, ct);
    public override ValueTask<ContextRecord?> GetContextAsync(string tenant, string contextId, CancellationToken ct = default) => _inner.GetContextAsync(tenant, contextId, ct);
    public override ValueTask<ContextRecord> GetOrCreateContextAsync(ContextRecord record, CancellationToken ct = default) => _inner.GetOrCreateContextAsync(record, ct);
}

public class TaskLifecycleTests
{
    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Unknown_client_context_id_is_rejected_by_default(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding).SendMessageAsync("task", "nope"));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Contains("contextId not found", ex.Message);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Unknown_client_context_id_adopted_when_policy_allows(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.ClientContextIdPolicy = ClientContextIdPolicy.AdoptIfUnknown);
        var task = (await host.Client(binding).SendMessageAsync("task", "c-client")).Task!;
        Assert.Equal("c-client", task.ContextId);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Foreign_context_is_reported_as_not_found(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var task = (await host.Client(binding, "alice").SendMessageAsync("task")).Task!;
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding, "bob").SendMessageAsync("task", task.ContextId));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Contains("contextId not found", ex.Message);
        // The owner can still use it.
        Assert.NotNull((await host.Client(binding, "alice").SendMessageAsync("task", task.ContextId)).Task);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Direct_reply_creates_context_for_later_messages(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var reply = (await host.Client(binding, "alice").SendMessageAsync("hello")).Message!;
        Assert.NotNull((await host.Client(binding, "alice").SendMessageAsync("task", reply.ContextId)).Task);
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding, "bob").SendMessageAsync("task", reply.ContextId));
        Assert.Contains("contextId not found", ex.Message);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Follow_up_to_working_task_rejected_under_strict_policy(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).Task!;
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("more", null, task.Id));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
        Assert.Contains("does not accept messages in state", ex.Message);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Follow_up_to_working_task_accepted_with_steering(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.FollowUpPolicy = FollowUpPolicy.AllowSteering);
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).Task!;
        var resp = await client.SendMessageAsync("more", null, task.Id);
        Assert.NotNull(resp.Task);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Follow_up_to_auth_required_task_accepted(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync("auth")).Task!;
        Assert.Equal(TaskState.AuthRequired, task.Status.State);
        try
        {
            var resp = await client.SendMessageAsync("credentials", null, task.Id);
            Assert.NotNull(resp.Task);
        }
        finally
        {
            ScenarioAgent.AuthSignals.GetOrAdd(task.Id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            await host.WaitIdleAsync();
        }
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Terminal_task_rejects_follow_up_with_reference_hint(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync("task")).Task!;
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("again", null, task.Id));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
        Assert.Contains("referenceTaskIds", ex.Message);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Event_apply_retries_after_version_conflict(string binding)
    {
        var store = new FlakyTaskStore();
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddSingleton<TaskStore>(store));
        store.FailNextUpdates(1); // the first event's update conflicts once
        var task = (await host.Client(binding).SendMessageAsync("task")).Task!;
        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Single(task.Artifacts!);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Follow_up_message_retries_after_version_conflict(string binding)
    {
        var store = new FlakyTaskStore();
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddSingleton<TaskStore>(store));
        var client = host.Client(binding);
        var first = (await client.SendMessageAsync("input")).Task!;
        await host.WaitIdleAsync();
        store.FailNextUpdates(2); // the follow-up append conflicts twice
        var second = (await client.SendMessageAsync("more", first.ContextId, first.Id)).Task!;
        Assert.Equal(TaskState.Completed, second.Status.State);
        Assert.Equal("got: more", second.Status.Message!.GetText());
    }

    [Fact]
    public void Terminal_states_cannot_transition()
    {
        foreach (var t in new[] { TaskState.Completed, TaskState.Failed, TaskState.Canceled, TaskState.Rejected })
            Assert.False(TaskStateMachine.CanTransition(t, TaskState.Working));
        Assert.True(TaskStateMachine.CanTransition(TaskState.Working, TaskState.Completed));
        Assert.True(TaskStateMachine.CanTransition(TaskState.InputRequired, TaskState.Working));
    }

    [Fact]
    public void Follow_up_acceptance_follows_policy()
    {
        Assert.True(TaskStateMachine.AcceptsFollowUp(TaskState.InputRequired, FollowUpPolicy.InterruptedOnly));
        Assert.True(TaskStateMachine.AcceptsFollowUp(TaskState.AuthRequired, FollowUpPolicy.InterruptedOnly));
        Assert.False(TaskStateMachine.AcceptsFollowUp(TaskState.Working, FollowUpPolicy.InterruptedOnly));
        Assert.False(TaskStateMachine.AcceptsFollowUp(TaskState.Submitted, FollowUpPolicy.InterruptedOnly));
        Assert.True(TaskStateMachine.AcceptsFollowUp(TaskState.Working, FollowUpPolicy.AllowSteering));
        Assert.True(TaskStateMachine.AcceptsFollowUp(TaskState.Submitted, FollowUpPolicy.AllowSteering));
        Assert.False(TaskStateMachine.AcceptsFollowUp(TaskState.Completed, FollowUpPolicy.AllowSteering));
    }

    [Fact]
    public async Task Terminal_state_first_writer_wins()
    {
        Assert.False(TaskStateMachine.CanTransition(TaskState.Completed, TaskState.Canceled));
        var s = new InMemoryTaskStore();
        var v1 = await s.CreateAsync(NewRecord("t", "a", "id1"));
        var v2 = await s.UpdateAsync(WithState(v1, TaskState.Working), 1);
        var a = await s.UpdateAsync(WithState(v2, TaskState.Completed), 2);
        await Assert.ThrowsAsync<ConcurrencyException>(() => s.UpdateAsync(WithState(v2, TaskState.Canceled), 2).AsTask());
        Assert.Equal(TaskState.Completed, (await s.GetAsync(a.Key))!.Task.Status.State);
    }
}
