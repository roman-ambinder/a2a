using System.Diagnostics;
using System.Runtime.CompilerServices;
using A2A.Client;
using A2A.Server;
using A2A.Server.Retention;
using A2A.Server.Storage;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Extensions.AgentFramework.Tests;

/// <summary><see cref="AgentSessionMode.Stored"/>: sessions per context and caller, kept in an <see cref="AgentSessionStore"/>.</summary>
public class SessionTests
{
    private const string Generic = "The agent failed to process the request.";

    private static AgentTask Task_(SendMessageResponse r) => Assert.IsType<TaskResponse>(r).Task;

    private static Task<AgentHost> StartAsync(AIAgent agent, FakeSessionStore store,
        Action<AIAgentExecutorOptions>? configure = null, Action<IServiceCollection>? services = null) =>
        AgentHost.StartAsync(a => a.AddAIAgent(o =>
        {
            o.SessionMode = AgentSessionMode.Stored;
            configure?.Invoke(o);
        }), s =>
        {
            s.AddSingleton(agent);
            s.AddSingleton<AgentSessionStore>(store);
            services?.Invoke(s);
        });

    private static string Partition(AgentSessionStoreKey key, string name) => key.Partitions![name];

    // ---- 1. only the new message ----

    [Fact]
    public async Task A_later_task_in_the_context_sends_only_the_new_message_and_the_session_holds_both_turns()
    {
        var agent = FakeAIAgent.Replying("ok");
        var store = new FakeSessionStore();
        await using var host = await StartAsync(agent, store);
        var client = host.Client();

        var first = Task_(await client.SendMessageAsync("q1"));
        await client.SendMessageAsync("q2", first.ContextId);

        Assert.Equal(["q1"], agent.Calls[0].Messages.Select(m => m.Text));
        Assert.Equal(["q2"], agent.Calls[1].Messages.Select(m => m.Text));
        Assert.Same(agent.Calls[0].Session, agent.Calls[1].Session);
        Assert.Equal(["q1", "q2"], Assert.IsType<FakeSession>(agent.Calls[1].Session).Received.Select(m => m.Text));
        Assert.Equal(1, agent.SessionsCreated);
        Assert.Equal(2, store.Saves);
    }

    [Fact]
    public async Task A_follow_up_on_an_open_task_sends_only_the_new_message()
    {
        var agent = FakeAIAgent.Replying("ok");
        var store = new FakeSessionStore();
        await using var host = await StartAsync(agent, store, o => o.KeepConversationOpen = true);
        var client = host.Client();

        var first = Task_(await client.SendMessageAsync("q1"));
        var follow = Message.FromUser("q2");
        follow.TaskId = first.Id;
        follow.ContextId = first.ContextId;
        await client.SendMessageAsync(new SendMessageRequest { Message = follow });

        Assert.Equal(["q2"], agent.Calls[1].Messages.Select(m => m.Text));
        Assert.Equal(["q1", "q2"], Assert.IsType<FakeSession>(agent.Calls[1].Session).Received.Select(m => m.Text));
    }

    // ---- 2. keys ----

    [Fact]
    public async Task The_default_tenant_and_an_anonymous_caller_give_a_valid_prefixed_key()
    {
        var agent = FakeAIAgent.Replying("ok");
        var store = new FakeSessionStore();
        await using var host = await StartAsync(agent, store);

        var task = Task_(await host.Client().SendMessageAsync("go"));

        Assert.Equal(TaskState.Completed, task.Status.State);
        var key = Assert.Single(store.SavedKeys);
        Assert.Equal(task.ContextId, key.SessionId);
        Assert.Equal("t:", Partition(key, "a2a.tenant"));
        Assert.StartsWith("o:", Partition(key, "a2a.owner"));
        Assert.True(Partition(key, "a2a.owner").Length > 2);
    }

    [Fact]
    public async Task Two_owners_get_sessions_under_their_own_owner_partition()
    {
        var agent = FakeAIAgent.Replying("ok");
        var store = new FakeSessionStore();
        await using var host = await StartAsync(agent, store);

        var alice = Task_(await host.Client(user: "alice").SendMessageAsync("from alice"));
        var bob = Task_(await host.Client(user: "bob").SendMessageAsync("from bob"));

        Assert.Equal(2, store.Keys.Count);
        var owners = store.SavedKeys.ToDictionary(k => k.SessionId, k => Partition(k, "a2a.owner"));
        Assert.Equal("o:alice", owners[alice.ContextId]);
        Assert.Equal("o:bob", owners[bob.ContextId]);
        Assert.NotSame(agent.Calls[0].Session, agent.Calls[1].Session);
    }

    [Fact]
    public async Task The_same_context_id_in_two_tenants_gives_two_sessions()
    {
        var agent = FakeAIAgent.Replying("ok");
        var store = new FakeSessionStore();
        await using var host = await StartAsync(agent, store);
        // A client may only name a context that exists, so the two contexts are made first (the id is free per tenant).
        var tasks = host.App.Services.GetRequiredService<TaskStore>();
        foreach (var tenant in new[] { "t1", "t2" })
            await tasks.GetOrCreateContextAsync(new ContextRecord(tenant, "alice", "shared-context", DateTimeOffset.UtcNow));

        foreach (var tenant in new[] { "t1", "t2" })
        {
            var message = Message.FromUser("in " + tenant);
            message.ContextId = "shared-context";
            var task = Task_(await host.Client(user: "alice", tenant: tenant).SendMessageAsync(new SendMessageRequest { Message = message }));
            Assert.Equal("shared-context", task.ContextId);
        }

        Assert.Equal(2, store.Keys.Count);
        Assert.Equal(["t:t1", "t:t2"], store.SavedKeys.Select(k => Partition(k, "a2a.tenant")).Order().ToArray());
        Assert.All(store.SavedKeys, k => Assert.Equal("shared-context", k.SessionId));
        Assert.NotSame(agent.Calls[0].Session, agent.Calls[1].Session);
        Assert.Equal(["in t2"], Assert.IsType<FakeSession>(agent.Calls[1].Session).Received.Select(m => m.Text));
    }

    // ---- 3. saved on every outcome ----

    [Fact]
    public async Task The_session_is_saved_after_a_failed_run()
    {
        var store = new FakeSessionStore();
        var agent = new FakeAIAgent((_, _) => Throwing());
        await using var host = await StartAsync(agent, store);

        var task = Task_(await host.Client().SendMessageAsync("go"));

        Assert.Equal(TaskState.Failed, task.Status.State);
        Assert.Equal(1, store.Saves);
        Assert.Single(store.SavedKeys);

        static async IAsyncEnumerable<AgentResponseUpdate> Throwing()
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, "part");
            await Task.Yield();
            throw new IOException("SECRET");
        }
    }

    [Fact]
    public async Task The_session_is_saved_after_a_canceled_run()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeSessionStore();
        var agent = new FakeAIAgent((_, ct) => Blocking(ct));
        await using var host = await StartAsync(agent, store);
        var client = host.Client();

        var request = new SendMessageRequest { Message = Message.FromUser("slow"), Configuration = new SendMessageConfiguration { ReturnImmediately = true } };
        var response = Task_(await client.SendMessageAsync(request));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await client.CancelTaskAsync(response.Id);
        await host.App.Services.GetRequiredService<A2ARequestHandler>().WhenIdleAsync();

        Assert.Equal(TaskState.Canceled, (await client.GetTaskAsync(response.Id)).Status.State);
        Assert.Equal(1, store.Saves);
        Assert.Single(store.SavedKeys);

        async IAsyncEnumerable<AgentResponseUpdate> Blocking([EnumeratorCancellation] CancellationToken ct)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }

    // ---- 4. a hung store ----

    [Fact]
    public async Task A_store_that_hangs_on_save_cannot_hold_the_execution()
    {
        var store = new FakeSessionStore { HangOnSave = true };
        await using var host = await StartAsync(FakeAIAgent.Replying("ok"), store);

        var watch = Stopwatch.StartNew();
        var task = Task_(await host.Client().SendMessageAsync("go"));
        watch.Stop();

        // The save before the final status timed out: the task fails instead of losing the turn quietly. One attempt only.
        Assert.Equal(TaskState.Failed, task.Status.State);
        Assert.Equal(Generic, string.Concat(task.Status.Message!.Parts.OfType<TextPart>().Select(p => p.Text)));
        Assert.Equal(1, store.Saves);
        // The executor bounds a save by 5 s (AIAgentExecutor.SaveTimeout, internal).
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(9));
    }

    // ---- 5. no store ----

    [Fact]
    public async Task Stored_mode_without_a_store_refuses_to_start()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentHost.StartAsync(a => a.AddAIAgent(o => o.SessionMode = AgentSessionMode.Stored),
                s => s.AddSingleton<AIAgent>(FakeAIAgent.Replying("x"))));
        Assert.Equal("SessionMode.Stored needs an AgentSessionStore registered in DI.", error.Message);
    }

    [Fact]
    public async Task Stateless_mode_with_a_store_registered_does_not_use_it()
    {
        var agent = FakeAIAgent.Replying("ok");
        var store = new FakeSessionStore();
        await using var host = await AgentHost.StartAsync(a => a.AddAIAgent(), s =>
        {
            s.AddSingleton<AIAgent>(agent);
            s.AddSingleton<AgentSessionStore>(store);
        });

        await host.Client().SendMessageAsync("go");

        Assert.Equal(0, store.Saves);
        Assert.Equal(1, agent.SessionsCreated);
    }

    // ---- 6. deleted with the context ----

    [Fact]
    public async Task Retention_deleting_the_context_deletes_the_session_with_the_key_the_run_used()
    {
        var store = new FakeSessionStore();
        await using var host = await StartAsync(FakeAIAgent.Replying("ok"), store, services: s => s.AddA2ARetention(o =>
        {
            o.SweepInterval = TimeSpan.FromMilliseconds(100);
            o.TaskRetention = TimeSpan.FromMilliseconds(1);
            o.OrphanContextRetention = TimeSpan.FromMilliseconds(1);
        }));

        var task = Task_(await host.Client(user: "alice").SendMessageAsync("go"));
        var saved = Assert.Single(store.SavedKeys);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (store.Deletes == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);

        Assert.Null(await host.App.Services.GetRequiredService<TaskStore>().GetContextAsync("", task.ContextId));
        Assert.Equal(saved, Assert.Single(store.DeletedKeys));
        Assert.Empty(store.Keys);
    }

    [Fact]
    public async Task The_hook_deletes_the_key_of_the_deleted_contexts_tenant_and_owner()
    {
        var store = new FakeSessionStore();
        await using var host = await StartAsync(FakeAIAgent.Replying("ok"), store);
        var hook = host.App.Services.GetServices<IA2AContextLifecycle>().Single();

        await hook.OnContextDeletedAsync(new ContextRecord("t9", "bob", "ctx-1", DateTimeOffset.UtcNow), CancellationToken.None);

        var key = Assert.Single(store.DeletedKeys);
        Assert.Equal("ctx-1", key.SessionId);
        Assert.Equal("t:t9", Partition(key, "a2a.tenant"));
        Assert.Equal("o:bob", Partition(key, "a2a.owner"));
    }

    [Fact]
    public async Task In_stateless_mode_the_hook_deletes_nothing()
    {
        var store = new FakeSessionStore();
        await using var host = await AgentHost.StartAsync(a => a.AddAIAgent(), s =>
        {
            s.AddSingleton<AIAgent>(FakeAIAgent.Replying("ok"));
            s.AddSingleton<AgentSessionStore>(store);
        });
        var hook = host.App.Services.GetServices<IA2AContextLifecycle>().Single();

        await hook.OnContextDeletedAsync(new ContextRecord("", "bob", "ctx-1", DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal(0, store.Deletes);
    }

    [Fact]
    public void Calling_AddAIAgent_twice_registers_the_hook_and_the_check_once()
    {
        var services = new ServiceCollection();
        services.AddA2AServer().AddAIAgent().AddAIAgent(o => o.SessionMode = AgentSessionMode.Stored);

        Assert.Single(services, d => d.ServiceType == typeof(IA2AContextLifecycle));
        Assert.Single(services, d => d.ServiceType == typeof(AIAgentExecutor).Assembly.GetType("A2A.Extensions.AgentFramework.AIAgentRegistration"));
    }
}
