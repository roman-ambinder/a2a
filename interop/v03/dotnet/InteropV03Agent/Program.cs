// .NET agent with the same scenarios as interop/dotnet/InteropAgent, with the v0.3 compat layer on.
// Serves JSON-RPC and HTTP+JSON at "/" for v1.0 clients, and v0.3 JSON-RPC and HTTP+JSON (under /v1) for v0.3 clients,
// and gRPC (v1.0 lf.a2a.v1.A2AService and v0.3 a2a.v1.A2AService) on <port + 1>. Push notifications are on.
// Usage: dotnet run -- <port>
using A2A;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Grpc;
using A2A.Server.Grpc.V0_3Compat;
using A2A.Server.V0_3Compat;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var port = args.Length > 0 ? int.Parse(args[0]) : 5500;
var baseUrl = $"http://127.0.0.1:{port}";
var grpcUrl = $"http://127.0.0.1:{port + 1}";

var builder = WebApplication.CreateBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenLocalhost(port, o => o.Protocols = HttpProtocols.Http1);
    // gRPC without TLS needs an HTTP/2-only endpoint.
    k.ListenLocalhost(port + 1, o => o.Protocols = HttpProtocols.Http2);
});
builder.Services.AddA2AServer(o =>
{
    // The interop clients are anonymous; production agents call RequireAuthorization() instead.
    o.AllowAnonymous = true;
    // The interop webhook receivers listen on loopback over plain HTTP.
    o.PushNotifications.AllowInsecureUrls = true;
    o.PushNotifications.AllowPrivateNetworks = true;
    o.PushDelivery.PollInterval = TimeSpan.FromMilliseconds(50);
    o.AgentCard = new AgentCard
    {
        Name = ".NET v0.3 Compat Scenario Agent",
        Description = "A2A .NET SDK agent with v0.3 compat for interop tests",
        Version = "1.0.0",
        SupportedInterfaces =
        [
            new AgentInterface { Url = baseUrl, ProtocolBinding = ProtocolBindings.JsonRpc },
            new AgentInterface { Url = baseUrl, ProtocolBinding = ProtocolBindings.HttpJson },
            new AgentInterface { Url = grpcUrl, ProtocolBinding = ProtocolBindings.Grpc },
        ],
        Capabilities = new AgentCapabilities { Streaming = true, PushNotifications = true },
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "scenario", Name = "Scenario", Description = "Interop scenarios", Tags = ["test"] }],
    };
}).AddAgentExecutor<ScenarioAgent>();
builder.Services.AddA2AServerAspNetCore();
builder.Services.AddA2AGrpcV03Compat();
// All interop clients share one address, so the default per-caller budget is too small for a full run.
builder.Services.AddA2ARateLimiting(o =>
{
    o.TokenLimit = 10_000;
    o.TokensPerPeriod = 10_000;
});

var app = builder.Build();
app.UseA2A();
app.MapAgentCard();
app.MapA2A("/", o => o.EnableV03Compat());
app.MapA2AGrpc();
app.MapA2AGrpcV03Compat();
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
