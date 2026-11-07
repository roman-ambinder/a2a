using A2A;
using A2A.Server;
using A2A.Server.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var baseUrl = builder.Configuration["BaseUrl"] ?? "http://localhost:5100";

builder.Services.AddA2AServer<EchoAgent>(o =>
{
    o.AgentCard = new AgentCard
    {
        Name = "Echo Agent",
        Description = "Echoes your message back, streaming it word by word.",
        Version = "1.0.0",
        SupportedInterfaces =
        [
            new AgentInterface { Url = $"{baseUrl}/a2a", ProtocolBinding = ProtocolBindings.JsonRpc },
            new AgentInterface { Url = $"{baseUrl}/a2a", ProtocolBinding = ProtocolBindings.HttpJson },
        ],
        Capabilities = new AgentCapabilities { Streaming = true, PushNotifications = true },
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "echo", Name = "Echo", Description = "Repeats the input", Tags = ["echo"], Examples = ["hello world"] }],
    };
    // Development convenience: accept requests without the A2A-Version header (e.g. curl).
    o.TreatMissingVersionAsCurrent = builder.Environment.IsDevelopment();
});

var app = builder.Build();
app.MapAgentCard();
app.MapA2A("/a2a");
app.Run(baseUrl);

/// <summary>Replies directly to "ping"; otherwise runs a task that streams the text back as an artifact.</summary>
internal sealed class EchoAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        var text = context.UserText;
        if (text.Equals("ping", StringComparison.OrdinalIgnoreCase))
        {
            await updater.ReplyAsync("pong", cancellationToken);
            return;
        }

        await updater.StartWorkAsync("Echoing...", cancellationToken);
        string? artifactId = null;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            await Task.Delay(150, cancellationToken);
            artifactId = await updater.AddArtifactAsync([Part.FromText((i == 0 ? "" : " ") + words[i])], artifactId, name: "echo",
                append: i > 0, lastChunk: i == words.Length - 1, cancellationToken: cancellationToken);
        }
        await updater.CompleteAsync(cancellationToken: cancellationToken);
    }
}
