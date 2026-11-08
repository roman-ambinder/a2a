using System.Text.Json;
using A2A.Client;
using A2A.Delegation;
using A2A.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests;

public class ActorChainServerTests
{
    public static TheoryData<string> Bindings => BindingTests.Bindings;

    private sealed class CountingAgent(bool interrupt = false) : IAgentExecutor
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (interrupt) await updater.RequireInputAsync("more please", ct); // stays open for follow-ups
            else await updater.CompleteAsync("done", ct);
        }
    }

    private static JsonElement J(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string Hops(int n) =>
        $$"""{"origin":{"sub":"user:alice"},"actors":[{{string.Join(",", Enumerable.Range(1, n).Select(i => $$"""{"sub":"agent:{{i}}"}"""))}}]}""";

    private static Message Msg(string? chainJson, string id = "m1", string? taskId = null) => new()
    {
        MessageId = id,
        Role = Role.User,
        TaskId = taskId,
        Parts = [Part.FromText("work")],
        Metadata = chainJson is null ? null : new() { [ActorChain.MetadataKey] = J(chainJson) },
    };

    private static async Task<(TestAgentHost Host, CountingAgent Agent)> StartAsync(Action<A2AServerOptions>? configure = null, bool interrupt = false)
    {
        var agent = new CountingAgent(interrupt);
        var host = await TestAgentHost.StartAsync(configure, services: s => s.Replace(ServiceDescriptor.Singleton<IAgentExecutor>(agent)));
        return (host, agent);
    }

    private static async Task<AgentTask> Stored(TestAgentHost host, string binding, string taskId) =>
        await host.Client(binding).GetTaskAsync(taskId);

    [Theory, MemberData(nameof(Bindings))]
    public async Task Chain_at_the_limit_is_accepted(string binding)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        var task = (await host.Client(binding).SendMessageAsync(Msg(Hops(8)))).RequireTask();
        await host.WaitIdleAsync();
        Assert.Equal(1, agent.Calls);
        Assert.NotNull(task.Id);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Chain_over_the_limit_is_refused_before_the_agent_runs(string binding)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding).SendMessageAsync(Msg(Hops(9))));
        Assert.Equal(A2AErrorCode.InvalidRequest, ex.Code);
        await host.WaitIdleAsync();
        Assert.Equal(0, agent.Calls);
        var list = await host.Client(binding).ListTasksAsync(new ListTasksRequest());
        Assert.Empty(list.Tasks);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Streaming_chain_over_the_limit_is_refused(string binding)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var _ in host.Client(binding).SendStreamingMessageAsync(Msg(Hops(9)))) { }
        });
        Assert.Equal(A2AErrorCode.InvalidRequest, ex.Code);
        await host.WaitIdleAsync();
        Assert.Equal(0, agent.Calls);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Limit_is_configurable(string binding)
    {
        var (host, agent) = await StartAsync(o => o.Limits.MaxActorChainHops = 2);
        await using var _ = host;
        await host.Client(binding).SendMessageAsync(Msg(Hops(2), "m1"));
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding).SendMessageAsync(Msg(Hops(3), "m2")));
        Assert.Equal(A2AErrorCode.InvalidRequest, ex.Code);
        await host.WaitIdleAsync();
        Assert.Equal(1, agent.Calls);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Message_without_a_chain_is_not_affected(string binding)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        var task = (await host.Client(binding).SendMessageAsync(Msg(null))).RequireTask();
        await host.WaitIdleAsync();
        Assert.Equal(1, agent.Calls);
        Assert.False((await Stored(host, binding, task.Id)).Metadata?.ContainsKey(ActorChain.MetadataKey) ?? false);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Private_metadata_under_the_key_is_ignored_and_not_echoed(string binding)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        var i = 0;
        foreach (var value in new[] { "\"text\"", "{\"foo\":1}" })
        {
            var task = (await host.Client(binding).SendMessageAsync(Msg(value, $"m{i++}"))).RequireTask();
            await host.WaitIdleAsync();
            Assert.False((await Stored(host, binding, task.Id)).Metadata?.ContainsKey(ActorChain.MetadataKey) ?? false);
        }
        Assert.Equal(2, agent.Calls);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Malformed_chain_is_refused_when_the_extension_is_activated(string binding)
    {
        var (host, agent) = await StartAsync(o => o.AgentCard!.Capabilities.Extensions!.Add(ActorChain.CreateExtension()));
        await using var _ = host;
        var options = new A2ARequestOptions { Extensions = [ActorChain.ExtensionUri] };
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding).SendMessageAsync(Msg("{\"foo\":1}"), options: options));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal(0, agent.Calls);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Well_formed_chain_is_echoed_on_a_new_task(string binding)
    {
        var (host, _) = await StartAsync();
        await using var _ = host;
        const string chain = """{"origin":{"sub":"user:alice"},"actors":[{"sub":"agent:a","proof_ref":"p-1"}]}""";
        var task = (await host.Client(binding).SendMessageAsync(Msg(chain))).RequireTask();
        await host.WaitIdleAsync();
        var stored = await Stored(host, binding, task.Id);
        Assert.Equal(chain, stored.Metadata![ActorChain.MetadataKey].GetRawText());
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Follow_up_replaces_or_keeps_the_echo(string binding)
    {
        var (host, _) = await StartAsync(interrupt: true);
        await using var _ = host;
        const string first = """{"origin":{"sub":"user:alice"},"actors":[{"sub":"agent:a"}]}""";
        const string second = """{"origin":{"sub":"user:alice"},"actors":[{"sub":"agent:a"},{"sub":"agent:b"}]}""";
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync(Msg(first))).RequireTask();
        await host.WaitIdleAsync();

        await client.SendMessageAsync(Msg(second, "m2", task.Id));
        await host.WaitIdleAsync();
        Assert.Equal(second, (await Stored(host, binding, task.Id)).Metadata![ActorChain.MetadataKey].GetRawText());

        await client.SendMessageAsync(Msg(null, "m3", task.Id));
        await host.WaitIdleAsync();
        Assert.Equal(second, (await Stored(host, binding, task.Id)).Metadata![ActorChain.MetadataKey].GetRawText());
    }
}
