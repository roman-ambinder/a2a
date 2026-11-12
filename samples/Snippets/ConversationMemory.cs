using A2A.Extensions.AI;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Storage;
using Microsoft.Extensions.AI;

namespace Snippets.ConversationMemory;

public static class Samples
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

    public static void Enable(IServiceCollection services, IChatClient chatClient)
    {
        services.AddSingleton(chatClient);
        #region Enable
        services.AddA2A(a => a
            .AddAgentCard(Card)
            .AddChatClientAgent(o =>
            {
                o.Instructions = "You are a helpful support agent.";
                o.UseContextMemory = true;
                o.ContextMemoryMaxTasks = 10;        // the newest 10 earlier tasks of the context ...
                o.ContextMemoryMaxBytes = 100_000;   // ... and at most 100,000 bytes of their messages
                o.MaxHistoryMessages = 40;           // earlier and task messages together
            }));
        #endregion
    }

    public static void MapConversations(WebApplication app)
    {
        #region List
        app.MapGet("/my/conversations", async (HttpContext http, TaskStore store, string? pageToken, CancellationToken ct) =>
            {
                // The caller's own tenant and principal, resolved the same way the A2A endpoint does.
                var call = http.CreateA2ACallContext();
                try
                {
                    var page = await store.ListContextsAsync(new ContextQuery(call.Tenant, call.Principal, 20, pageToken), ct);
                    return Results.Ok(new
                    {
                        conversations = page.Records.Select(c => new { c.ContextId, c.CreatedAt, c.Summary }),
                        next = page.NextPageToken, // empty on the last page
                    });
                }
                catch (A2AException) // a bad pageToken
                {
                    return Results.BadRequest();
                }
            })
            .RequireAuthorization();
        #endregion
    }
}

public sealed class SummarizingAgent : IAgentExecutor
{
    #region Custom
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        var memory = context.Conversation; // null only for a context built by hand
        IReadOnlyList<Message> earlier = memory is null
            ? []
            : await memory.ReadEarlierMessagesAsync(maxTasks: 10, maxBytes: 100_000, cancellationToken);

        var reply = $"You said {earlier.Count} things before. Now: {context.UserText}";
        await updater.CompleteAsync(reply, cancellationToken);

        // You write the summary. The server never does.
        if (memory is { CanStoreSummary: true })
        {
            var old = await memory.GetSummaryAsync(cancellationToken);
            await memory.SetSummaryAsync(Summarize(old, context.UserText), cancellationToken);
        }
    }
    #endregion

    private static string Summarize(string? old, string latest) => $"{old} {latest}".Trim();
}
