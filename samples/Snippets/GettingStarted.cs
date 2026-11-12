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
            // Anonymous callers, curl without the A2A-Version header, exception text in errors. Development only:
            // in any other environment the app refuses to start.
            if (builder.Environment.IsDevelopment()) a.UseDevelopmentDefaults();
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
        await updater.StartWorkAsync(cancellationToken: ct);
        await updater.AddArtifactAsync([Part.FromText($"You said: {ctx.UserText}")], name: "answer", cancellationToken: ct);
        await updater.CompleteAsync(cancellationToken: ct);
    }
}
#endregion
