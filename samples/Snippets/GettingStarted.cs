#region Usings

using A2A.Client;
using A2A.Server;
using A2A.Server.AspNetCore;
#endregion

namespace Snippets.GettingStarted;

internal static class Samples
{
    public static WebApplication BuildServer(string[] args)
    {
        #region Server
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAuthentication(/* your scheme */);
        builder.Services.AddAuthorization();
        builder.Services.AddA2A(a => a
            .AddAgentExecutor<MyAgent>()
            .AddAgentCard(AgentCard.Create("My Agent", "Says hello.")
                .AddSkill("hello", "Hello", "Says hello back", tags: ["greeting"])));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapA2A("/a2a").RequireAuthorization();
        #endregion

        return app;
    }

    public static async Task CallAgentAsync(HttpClient httpClient)
    {
        #region Client
        var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"), httpClient);
        string answer = await client.AskAsync("hello"); // the reply's text; throws if the task does not complete

        await foreach (var chunk in client.StreamTextAsync("stream this"))
            Console.Write(chunk);
        #endregion

        _ = answer;
    }

    public static async Task ConversationAsync(A2AClient client)
    {
        #region Conversation
        var chat = client.StartConversation();         // keeps the context id and the waiting task's id
        var reply = await chat.SendAsync("book a flight to Paris");
        while (reply.NeedsInput)
        {
            Console.WriteLine(reply.StatusText);          // the agent's question
            reply = await chat.SendAsync(Console.ReadLine() ?? "");
        }

        Console.WriteLine(reply.EnsureCompleted().Text); // throws if the task failed, was rejected or canceled
        #endregion
    }

    public static void TextAgent(WebApplicationBuilder builder)
    {
        #region TextAgent
        builder.Services.AddA2A(a => a
            .AddAgentCard("My Agent", "Says hello.")
            .AddAgentExecutor(ctx => $"You said: {ctx.UserText}")
            .RequirePolicy("callers"));   // who may call, for every A2A endpoint; then app.MapA2A("/a2a") needs nothing more
        #endregion
    }

    public static void FromConfiguration(WebApplicationBuilder builder)
    {
        #region FromConfiguration
        // The card, limits, public URL and rate limit come from the "A2A" section; code after it wins.
        builder.Services.AddA2A(builder.Configuration.GetSection("A2A"), a => a.AddAgentExecutor<MyAgent>());
        #endregion
    }

    public static void Development(WebApplicationBuilder builder)
    {
        #region Development
        builder.Services.AddA2A(a =>
        {
            a.AddAgentExecutor<MyAgent>();
            // In Development: anonymous callers, curl without the A2A-Version header, exception text in errors.
            // In any other environment it does nothing, so the endpoints need their usual authorization.
            a.ConfigureForDevelopment();
        });
        #endregion
    }

    public static void ChangeDefaults(IServiceCollection services)
    {
        #region Defaults
        services.AddA2A(a => a
            .AddAgentExecutor<MyAgent>()
            .Configure(o => o.Limits.MaxRequestBodyBytes = 1024 * 1024)
            .ConfigureRateLimiting(r =>
            {
                r.TokenLimit = 10;
                r.TokensPerPeriod = 30;
            })
            .ConfigureHttp(h => h.PublicBaseUrl = new Uri("https://agent.example.com")));
        #endregion
    }
}

#region Agent
internal sealed class MyAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext ctx, TaskUpdater updater, CancellationToken ct)
    {
        await updater.StartWorkAsync(ct);
        await updater.AddArtifactAsync($"You said: {ctx.UserText}", "answer", ct);
        await updater.CompleteAsync(ct);
    }
}
#endregion
