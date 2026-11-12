using A2A.Client;
using System.Security.Claims;
using A2A.Server;

namespace A2A.Tests;

/// <summary><see cref="A2AServerOptions.ContextAccess"/>: other principals may join a context; tasks stay owner-scoped.</summary>
public class ContextAccessTests
{
    private static ServerCallContext Call(string principal) => new()
    {
        Principal = principal,
        ProtocolVersion = A2AProtocol.Version,
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", principal)], "Test")),
    };

    [Fact]
    public async Task By_default_only_the_owner_may_use_a_context()
    {
        await using var host = await TestAgentHost.StartAsync();
        var task = (await host.Client(ProtocolBindings.HttpJson, "alice").SendMessageAsync("task")).RequireTask();

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(ProtocolBindings.HttpJson, "bob").SendMessageAsync("task", task.ContextId));

        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Contains("contextId not found", ex.Message);
    }

    [Fact]
    public async Task An_allowed_principal_gets_its_own_task_in_the_same_context()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.ContextAccess = (_, _, _) => ValueTask.FromResult(true));
        var alice = host.Client(ProtocolBindings.HttpJson, "alice");
        var bob = host.Client(ProtocolBindings.HttpJson, "bob");
        var aliceTask = (await alice.SendMessageAsync("task")).RequireTask();

        var bobTask = (await bob.SendMessageAsync("task", aliceTask.ContextId)).RequireTask();

        Assert.NotEqual(aliceTask.Id, bobTask.Id);
        Assert.Equal(aliceTask.ContextId, bobTask.ContextId);
        Assert.Equal(TaskState.Completed, bobTask.Status.State);
    }

    [Fact]
    public async Task Sharing_a_context_does_not_share_tasks()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.ContextAccess = (_, _, _) => ValueTask.FromResult(true));
        var alice = host.Client(ProtocolBindings.HttpJson, "alice");
        var bob = host.Client(ProtocolBindings.HttpJson, "bob");
        var aliceTask = (await alice.SendMessageAsync("task")).RequireTask();
        var bobTask = (await bob.SendMessageAsync("task", aliceTask.ContextId)).RequireTask();

        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() => bob.GetTaskAsync(aliceTask.Id))).Code);
        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() => alice.GetTaskAsync(bobTask.Id))).Code);
        Assert.Equal(bobTask.Id, Assert.Single((await bob.ListTasksAsync(new ListTasksRequest { ContextId = aliceTask.ContextId })).Tasks).Id);
        Assert.Equal(aliceTask.Id, Assert.Single((await alice.ListTasksAsync(new ListTasksRequest { ContextId = aliceTask.ContextId })).Tasks).Id);
    }

    [Fact]
    public async Task A_delegate_that_says_no_denies()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.ContextAccess = (_, _, _) => ValueTask.FromResult(false));
        var task = (await host.Client(ProtocolBindings.HttpJson, "alice").SendMessageAsync("task")).RequireTask();

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(ProtocolBindings.HttpJson, "bob").SendMessageAsync("task", task.ContextId));

        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Contains("contextId not found", ex.Message);
    }

    [Fact]
    public async Task The_delegate_is_called_only_for_a_non_owner_of_an_existing_context()
    {
        var seen = new List<(string ContextOwner, string Caller)>();
        await using var host = await TestAgentHost.StartAsync(o => o.ContextAccess = (context, call, _) =>
        {
            lock (seen) seen.Add((context.Owner, call.Principal));
            return ValueTask.FromResult(true);
        });
        var alice = host.Client(ProtocolBindings.HttpJson, "alice");
        var first = (await alice.SendMessageAsync("task")).RequireTask();                     // a new context
        await alice.SendMessageAsync("task", first.ContextId);                                // the owner, existing context
        Assert.Empty(seen);

        await host.Client(ProtocolBindings.HttpJson, "bob").SendMessageAsync("task", first.ContextId);

        Assert.Equal([("alice", "bob")], seen);
    }

    [Fact]
    public async Task An_exception_of_the_delegate_propagates()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.ContextAccess = (_, _, _) => throw new InvalidOperationException("boom"));
        var task = (await host.Client(ProtocolBindings.HttpJson, "alice").SendMessageAsync("task")).RequireTask();
        var request = new SendMessageRequest { Message = Message.FromUser("task", task.ContextId) };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await host.Handler.SendMessageAsync(request, Call("bob")));

        Assert.Equal("boom", ex.Message);
    }
}
