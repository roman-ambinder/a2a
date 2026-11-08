// .NET agent with the same scenarios as interop/python/py_server.py.
// Usage: dotnet run -- <port>
using A2A;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Grpc;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var port = args.Length > 0 ? int.Parse(args[0]) : 5300;
var baseUrl = $"http://127.0.0.1:{port}";

var builder = WebApplication.CreateBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
// HTTP/1.1 for JSON-RPC + REST on <port>, cleartext HTTP/2 for gRPC on <port+1>.
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenLocalhost(port, o => o.Protocols = HttpProtocols.Http1);
    k.ListenLocalhost(port + 1, o => o.Protocols = HttpProtocols.Http2);
});
builder.Services.AddA2AGrpc();
builder.Services.AddA2AServer(o =>
{
    // Interop test peer: no authentication, and no per-caller rate limit (all checks come from one address).
    o.AllowAnonymous = true;
    o.AgentCard = new AgentCard
    {
    Name = ".NET Scenario Agent",
    Description = "A2A .NET SDK agent for interop tests",
    Version = "1.0.0",
    SupportedInterfaces =
    [
        new AgentInterface { Url = $"{baseUrl}/a2a", ProtocolBinding = ProtocolBindings.JsonRpc },
        new AgentInterface { Url = $"{baseUrl}/a2a", ProtocolBinding = ProtocolBindings.HttpJson },
        new AgentInterface { Url = $"http://127.0.0.1:{port + 1}", ProtocolBinding = ProtocolBindings.Grpc },
    ],
    Capabilities = new AgentCapabilities { Streaming = true, PushNotifications = false },
    DefaultInputModes = ["text/plain"],
    DefaultOutputModes = ["text/plain"],
    Skills = [new AgentSkill { Id = "scenario", Name = "Scenario", Description = "Interop scenarios", Tags = ["test"] }],
    };
}).AddAgentExecutor<ScenarioAgent>();
builder.Services.AddA2ARateLimiting(o => o.Enabled = false);

var app = builder.Build();
app.MapAgentCard();
app.MapA2A("/a2a");
app.MapA2AGrpc();
app.Run();

internal sealed class ScenarioAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
    {
        var text = context.UserText;
        if (context.Task?.Status.State == TaskState.InputRequired)
        {
            await updater.CompleteAsync($"got: {text}", ct);
            return;
        }

        switch (text.Split(' ')[0])
        {
            case "hello":
                await updater.ReplyAsync("hi there", ct);
                break;
            case "task":
                await updater.StartWorkAsync("working", ct);
                await updater.AddArtifactAsync([Part.FromText("part1")], artifactId: "a1", name: "result", cancellationToken: ct);
                await updater.AddArtifactAsync([Part.FromText("part2")], artifactId: "a1", append: true, lastChunk: true, cancellationToken: ct);
                await updater.CompleteAsync("done", ct);
                break;
            case "input":
                await updater.RequireInputAsync("need more", ct);
                break;
            case "slow":
                await updater.StartWorkAsync(cancellationToken: ct);
                await Task.Delay(Timeout.Infinite, ct);
                break;
            default:
                await updater.CompleteAsync(text, ct);
                break;
        }
    }
}
