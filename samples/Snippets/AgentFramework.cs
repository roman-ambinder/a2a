using A2A.Client;
using A2A.Extensions.AgentFramework;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Retention;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// The adapter is experimental (A2AEXP006). Agent Framework's session store (MAAI001) and continuation tokens (MEAI001) are
// evaluation-only upstream; the snippets that use them opt in.
#pragma warning disable A2AEXP006, MAAI001, MEAI001

namespace Snippets.AgentFramework;

internal static class Snippets
{
    private static readonly AgentCard Card = new()
    {
        Name = "Support Agent",
        Description = "Answers support questions.",
        Version = "1.0.0",
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "support", Name = "Support", Description = "Answers questions", Tags = ["support"] }],
    };

    public static void Host(IServiceCollection services, AIAgent agent)
    {
        #region Host
        services.AddA2A(a => a
            .AddAgentCard(Card)
            .AddAIAgent(agent));   // any Agent Framework agent, for example a ChatClientAgent
        #endregion
    }

    public static void HostScoped(IServiceCollection services)
    {
        #region HostScoped
        // Resolved from each execution's scope: the agent can depend on scoped services (a DbContext, ...).
        services.AddScoped<AIAgent>(sp => new ScopedSupportAgent(sp.GetRequiredService<ISupportTickets>()));
        services.AddA2A(a => a
            .AddAgentCard(Card)
            .AddAIAgent(o => o.ConfigureRunOptions = (run, context) =>
                run.AdditionalProperties!["tenant"] = context.Call.Tenant));
        #endregion
    }

    public static void HostStored(IServiceCollection services, AgentSessionStore store)
    {
        #region HostStored
        services.AddSingleton(store);   // any Agent Framework session store
        services.AddA2A(a => a
            .AddAgentCard(Card)
            .AddAIAgent(o =>
            {
                o.SessionMode = AgentSessionMode.Stored;
                o.ToolApprovals = ToolApprovalMode.Bound;
            })
            .AddRetention());   // deletes a context's session with the context
        #endregion
    }

    public static async Task CallAsync(HttpClient http)
    {
        #region Remote
        var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"), http);
        AIAgent support = client.AsAIAgent(new A2AAIAgentOptions { Name = "support", Description = "Answers support questions." });

        var session = await support.CreateSessionAsync();
        var first = await support.RunAsync("My order has not arrived.", session);
        var next = await support.RunAsync("It is order 1234.", session);   // same remote context, or the same task
        #endregion
        _ = (first, next);
    }

    public static async Task BackgroundAsync(AIAgent support)
    {
        #region Background
        var response = await support.RunAsync("Rebuild the search index.",
            options: new AgentRunOptions { AllowBackgroundResponses = true });
        while (response.ContinuationToken is { } token)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            response = await support.RunAsync([], options: new AgentRunOptions { ContinuationToken = token });
        }
        #endregion
    }

    public static async Task<ResponseContinuationToken?> StartReportAsync(AIAgent support, CancellationToken stopping)
    {
        #region Resume
        // Read until the app stops, keeping the latest token (store it if the app may restart).
        ResponseContinuationToken? last = null;
        await foreach (var update in support.RunStreamingAsync("Write the report.",
                           options: new AgentRunOptions { AllowBackgroundResponses = true }, cancellationToken: stopping)
                           .WithCancellation(stopping))
        {
            Console.Write(update.Text);
            last = update.ContinuationToken;   // null on the last update: the reply is complete
        }

        return last;
        #endregion
    }

    public static async Task ContinueReportAsync(AIAgent support, ResponseContinuationToken last)
    {
        #region ResumeLater
        // Later, maybe in another process: continue after the last part that was read.
        await foreach (var update in support.RunStreamingAsync([], options: new AgentRunOptions { ContinuationToken = last }))
            Console.Write(update.Text);
        #endregion
    }

    public sealed class Cleanup : IA2AContextLifecycle
    {
        #region Lifecycle
        // Any per-context state kept outside the task store can go with its context.
        public ValueTask OnContextDeletedAsync(A2A.Server.Storage.ContextRecord context, CancellationToken cancellationToken)
        {
            Console.WriteLine($"context {context.ContextId} of {context.Owner} was deleted");
            return ValueTask.CompletedTask;
        }
        #endregion
    }

    public interface ISupportTickets;

    private sealed class ScopedSupportAgent(ISupportTickets tickets) : DelegatingAIAgent(new PlaceholderAgent())
    {
        public ISupportTickets Tickets { get; } = tickets;
    }

    private sealed class PlaceholderAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(AgentSession session, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(System.Text.Json.JsonElement serializedState, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
