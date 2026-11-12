using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace A2A.Extensions.AgentFramework.Tests;

/// <summary>The test doubles work, and the package keeps its public types in its own namespace.</summary>
public class SkeletonTests
{
    [Fact]
    public async Task Fake_agent_streams_its_script_and_records_the_call()
    {
        var agent = FakeAIAgent.Replying("Hello ", "world");
        var session = await agent.CreateSessionAsync();
        var options = new AgentRunOptions { AdditionalProperties = new() { [A2AAgentKeys.TaskId] = "t1" } };

        var text = "";
        await foreach (var update in agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hi")], session, options))
            text += update.Text;

        Assert.Equal("Hello world", text);
        var call = Assert.Single(agent.Calls);
        Assert.Equal("hi", Assert.Single(call.Messages).Text);
        Assert.Same(session, call.Session);
        Assert.Equal("t1", call.Options?.AdditionalProperties?[A2AAgentKeys.TaskId]);
        Assert.Equal(1, agent.SessionsCreated);
        Assert.Equal("hi", Assert.Single(Assert.IsType<FakeSession>(session).Received).Text);
    }

    [Fact]
    public async Task Fake_session_store_round_trips_and_counts()
    {
        var agent = FakeAIAgent.Replying("x");
        var store = new FakeSessionStore();
        var key = new AgentSessionStoreKey("ctx").WithPartition("a2a.owner", "alice");

        var created = await store.GetOrCreateSessionAsync(agent, key);
        await store.SaveSessionAsync(agent, key, created);
        Assert.Same(created, await store.GetSessionAsync(agent, key));
        Assert.Null(await store.GetSessionAsync(agent, new AgentSessionStoreKey("ctx").WithPartition("a2a.owner", "bob")));

        await store.DeleteSessionAsync(agent, key);
        Assert.Null(await store.GetSessionAsync(agent, key));
        Assert.Equal((1, 1), (store.Saves, store.Deletes));
    }

    [Fact]
    public void Agent_keys_match_the_chat_keys_where_they_mean_the_same()
    {
        Assert.Equal(A2A.Extensions.AI.A2AChatKeys.TaskId, A2AAgentKeys.TaskId);
        Assert.Equal(A2A.Extensions.AI.A2AChatKeys.TaskState, A2AAgentKeys.TaskState);
    }

    [Fact]
    public void Every_public_type_lives_in_the_package_namespace()
    {
        // Kept here, not in A2A.Tests' NamespaceRulesTests: referencing this package there would bring the M.E.AI and
        // Agent Framework packages into A2A.Tests, which the Extensions.AI design kept out.
        var assembly = typeof(A2AAgentKeys).Assembly;
        var types = assembly.GetExportedTypes();
        Assert.NotEmpty(types);
        Assert.All(types, t => Assert.True(
            t.Namespace == "A2A.Extensions.AgentFramework" || t.Namespace!.StartsWith("A2A.Extensions.AgentFramework.", StringComparison.Ordinal),
            $"{t.FullName} is outside A2A.Extensions.AgentFramework"));
    }
}
