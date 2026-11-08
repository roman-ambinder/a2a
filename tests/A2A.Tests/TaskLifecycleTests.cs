using A2A.Server;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using static A2A.Tests.TaskStoreTests;

namespace A2A.Tests;

/// <summary>Wraps a real store and throws <see cref="ConcurrencyException"/> from the next N updates or commits of an existing task.</summary>
public sealed class FlakyTaskStore : DelegatingTaskStore
{
    private int _failures;
    private int _cancelBeforeConflict;

    /// <summary>The next update first cancels the stored task behind the caller's back, then conflicts.</summary>
    public void CancelThenConflictOnNextUpdate() => Volatile.Write(ref _cancelBeforeConflict, 1);

    public void FailNextUpdates(int count) => Volatile.Write(ref _failures, count);

    public override async ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion, CancellationToken ct = default)
    {
        await MaybeConflictAsync(record.Key, expectedVersion, ct);
        return await Inner.UpdateAsync(record, expectedVersion, ct);
    }

    public override async ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default)
    {
        if (!commit.IsCreate) await MaybeConflictAsync(commit.Record.Key, commit.ExpectedVersion, ct);
        return await Inner.CommitAsync(commit, ct);
    }

    private async ValueTask MaybeConflictAsync(TaskKey key, long expectedVersion, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _cancelBeforeConflict, 0) == 1)
        {
            var stored = (await Inner.GetAsync(key, ct))!;
            await Inner.UpdateAsync(WithState(stored, TaskState.Canceled), stored.Version, ct);
            throw new ConcurrencyException(key, expectedVersion);
        }
        if (Interlocked.Decrement(ref _failures) >= 0) throw new ConcurrencyException(key, expectedVersion);
    }
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
        var task = (await host.Client(binding).SendMessageAsync("task", "c-client")).RequireTask();
        Assert.Equal("c-client", task.ContextId);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Foreign_context_is_reported_as_not_found(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var task = (await host.Client(binding, "alice").SendMessageAsync("task")).RequireTask();
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding, "bob").SendMessageAsync("task", task.ContextId));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Contains("contextId not found", ex.Message);
        // The owner can still use it.
        Assert.IsType<TaskResponse>(await host.Client(binding, "alice").SendMessageAsync("task", task.ContextId));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Direct_reply_creates_context_for_later_messages(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var reply = (await host.Client(binding, "alice").SendMessageAsync("hello")).RequireMessage();
        Assert.IsType<TaskResponse>(await host.Client(binding, "alice").SendMessageAsync("task", reply.ContextId));
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding, "bob").SendMessageAsync("task", reply.ContextId));
        Assert.Contains("contextId not found", ex.Message);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Follow_up_to_working_task_rejected_under_strict_policy(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();
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
        var task = (await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();
        var resp = await client.SendMessageAsync("more", null, task.Id);
        Assert.IsType<TaskResponse>(resp);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Follow_up_to_auth_required_task_accepted(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync("auth")).RequireTask();
        Assert.Equal(TaskState.AuthRequired, task.Status.State);
        try
        {
            var resp = await client.SendMessageAsync("credentials", null, task.Id);
            Assert.IsType<TaskResponse>(resp);
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
        var task = (await client.SendMessageAsync("task")).RequireTask();
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("again", null, task.Id));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
        Assert.Contains("referenceTaskIds", ex.Message);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Event_apply_retries_after_version_conflict(string binding)
    {
        var store = new FlakyTaskStore();
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddTaskStore(store));
        store.FailNextUpdates(3); // the first event's update conflicts three times, the last allowed retry lands
        var task = (await host.Client(binding).SendMessageAsync("task")).RequireTask();
        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Single(task.Artifacts!);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Follow_up_message_retries_after_version_conflict(string binding)
    {
        var store = new FlakyTaskStore();
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddTaskStore(store));
        var client = host.Client(binding);
        var first = (await client.SendMessageAsync("input")).RequireTask();
        await host.WaitIdleAsync();
        store.FailNextUpdates(2); // the follow-up append conflicts twice
        var second = (await client.SendMessageAsync("more", first.ContextId, first.Id)).RequireTask();
        Assert.Equal(TaskState.Completed, second.Status.State);
        Assert.Equal("got: more", second.Status.Message!.GetText());
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Follow_up_message_gives_up_after_too_many_conflicts_with_generic_error(string binding)
    {
        var store = new FlakyTaskStore();
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddTaskStore(store));
        var client = host.Client(binding);
        var first = (await client.SendMessageAsync("input")).RequireTask();
        await host.WaitIdleAsync();
        store.FailNextUpdates(33); // initial attempt + 10 server retries conflict, for each of the client's 3 attempts (Internal is retryable)
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("more", first.ContextId, first.Id));
        Assert.Equal(A2AErrorCode.Internal, ex.Code);
        Assert.DoesNotContain("ConcurrencyException", ex.Message);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Event_is_dropped_when_reloaded_task_is_already_terminal(string binding)
    {
        var store = new FlakyTaskStore();
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddTaskStore(store));
        store.CancelThenConflictOnNextUpdate();
        var task = (await host.Client(binding).SendMessageAsync("task")).RequireTask();
        await host.WaitIdleAsync();
        var stored = await host.Client(binding).GetTaskAsync(task.Id);
        Assert.Equal(TaskState.Canceled, stored.Status.State);
        Assert.Null(stored.Artifacts);
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
        var s = new InMemoryTaskStore(new InMemoryEventStore(new StreamingOptions()));
        var v1 = await s.CreateAsync(NewRecord("t", "a", "id1"));
        var v2 = await s.UpdateAsync(WithState(v1, TaskState.Working), 1);
        var a = await s.UpdateAsync(WithState(v2, TaskState.Completed), 2);
        await Assert.ThrowsAsync<ConcurrencyException>(() => s.UpdateAsync(WithState(v2, TaskState.Canceled), 2).AsTask());
        Assert.Equal(TaskState.Completed, (await s.GetAsync(a.Key))!.Task.Status.State);
    }
}
