// The agent a2a-itk runs as this SDK's system under test (see README.md).
// Usage: dotnet run -- --httpPort N --grpcPort M
//   HTTP/1.1 on N: JSON-RPC and HTTP+JSON at /jsonrpc (v1.0, and v0.3 on the same URL), the Agent Card at
//   /.well-known/agent-card.json. Cleartext HTTP/2 on M: gRPC, v1.0 and v0.3.
using System.Globalization;
using A2A;
using A2A.Itk;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Grpc;
using A2A.Server.Grpc.V0_3Compat;
using A2A.Server.V0_3Compat;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var httpPort = Port(args, "--httpPort");
var grpcPort = Port(args, "--grpcPort");

var builder = WebApplication.CreateBuilder();
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenLocalhost(httpPort, o => o.Protocols = HttpProtocols.Http1);
    k.ListenLocalhost(grpcPort, o => o.Protocols = HttpProtocols.Http2);
});

var card = new AgentCard
{
    Name = "A2A .NET SDK (roman-ambinder/a2a) ITK agent",
    Description = "Runs a2a-itk traversal instructions.",
    Version = "1.0.0",
    SupportedInterfaces =
    [
        new AgentInterface { Url = $"http://127.0.0.1:{httpPort}/jsonrpc", ProtocolBinding = ProtocolBindings.JsonRpc },
        new AgentInterface { Url = $"http://127.0.0.1:{httpPort}/jsonrpc", ProtocolBinding = ProtocolBindings.HttpJson },
        // host:port, no scheme, as the other ITK agents list plaintext gRPC.
        new AgentInterface { Url = $"127.0.0.1:{grpcPort}", ProtocolBinding = ProtocolBindings.Grpc },
    ],
    Capabilities = new AgentCapabilities { Streaming = true, PushNotifications = true },
    DefaultInputModes = ["text/plain", ItkAgent.InstructionMediaType],
    DefaultOutputModes = ["text/plain"],
    Skills = [new AgentSkill { Id = "itk", Name = "ITK traversal", Description = "Runs a2a-itk instructions", Tags = ["itk"] }],
};

// One client for every hop: no cookies and no redirects, like AddA2AClient.
builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
    { Timeout = Timeout.InfiniteTimeSpan });
builder.Services.AddA2A(a => a.AddAgentExecutor<ItkAgent>().AddAgentCard(card))
    .Configure(o =>
    {
        // ITK agents call each other anonymously, on loopback only (ListenLocalhost above).
        o.AllowAnonymous = true;
        // ITK's push receiver is plain HTTP on 127.0.0.1.
        o.PushNotifications.AllowInsecureUrls = true;
        o.PushNotifications.AllowPrivateNetworks = true;
    })
    // Every hop comes from 127.0.0.1, so the default per-caller budget is too small for a run.
    .ConfigureRateLimiting(o =>
    {
        o.TokenLimit = 100_000;
        o.TokensPerPeriod = 100_000;
    });
builder.Services.AddA2AGrpc();
builder.Services.AddA2AGrpcV03Compat();

var app = builder.Build();
app.MapA2A("/jsonrpc", o => o.EnableV03Compat());
app.MapA2AGrpc();
app.MapA2AGrpcV03Compat();
app.Run();

static int Port(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        throw new ArgumentException($"Missing {name} <port>");
    return port;
}
