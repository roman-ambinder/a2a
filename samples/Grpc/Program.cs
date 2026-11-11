using System.Net;
using A2A;
using A2A.Client;
using A2A.Client.Grpc;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Grpc;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// One agent served over gRPC and JSON-RPC, and three clients that call it. The agent answers with the binding the
// request came in on, so the sample can check which binding each client chose.
// No API keys, no external calls: the server listens on 127.0.0.1 only.

const int httpPort = 5310; // HTTP/1.1: the Agent Card and JSON-RPC
const int grpcPort = 5311; // cleartext HTTP/2: gRPC needs HTTP/2, and without TLS it gets a port of its own
var cardUrl = new Uri($"http://127.0.0.1:{httpPort}/");

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(IPAddress.Loopback, httpPort, listen => listen.Protocols = HttpProtocols.Http1);
    kestrel.Listen(IPAddress.Loopback, grpcPort, listen => listen.Protocols = HttpProtocols.Http2);
});

var card = new AgentCard
{
    Name = "Binding Agent",
    Description = "Says which binding your request used.",
    Version = "1.0.0",
    // The card's order wins: a client that supports gRPC takes the first entry. A gRPC URL has no path.
    SupportedInterfaces =
    [
        new AgentInterface { Url = $"http://127.0.0.1:{grpcPort}", ProtocolBinding = ProtocolBindings.Grpc },
        new AgentInterface { Url = $"http://127.0.0.1:{httpPort}/a2a", ProtocolBinding = ProtocolBindings.JsonRpc }
    ],
    Capabilities = new AgentCapabilities { Streaming = true },
    DefaultInputModes = ["text/plain"],
    DefaultOutputModes = ["text/plain"],
    Skills = [new AgentSkill { Id = "binding", Name = "Binding", Description = "Names the binding", Tags = ["demo"] }]
};

// AllowAnonymous is fine for a sample that only listens on loopback. A real agent needs authentication
// (see samples/EchoAgent) and RequireAuthorization() on MapA2A and MapA2AGrpc.
builder.Services.AddA2AServer(o =>
    {
        o.AgentCard = card;
        o.AllowAnonymous = true;
    })
    .AddAgentExecutor<BindingAgent>();
builder.Services.AddA2AGrpc(); // also adds the ASP.NET Core part of the server

var app = builder.Build();
app.UseA2A();
app.MapAgentCard();
app.MapA2A("/a2a");
app.MapA2AGrpc(); // lf.a2a.v1.A2AService; no pattern argument
await app.StartAsync();
Console.WriteLine($"Agent card at {cardUrl}.well-known/agent-card.json, gRPC at http://127.0.0.1:{grpcPort}");

using var http = new HttpClient(); // only fetches the card; the gRPC channel has its own options
var failures = new List<string>();

void Expect(string what, string? actual, string expected)
{
    Console.WriteLine($"{what}: {actual}");
    if (actual != expected) failures.Add($"{what}: expected '{expected}', got '{actual}'");
}

// 1. gRPC required: throws UnsupportedOperation if the card has no usable gRPC interface.
var grpcOnly = await A2AGrpcClientFactory.CreateAsync(cardUrl, http);
Expect("A2AGrpcClientFactory", await AskAsync(grpcOnly), ProtocolBindings.Grpc);

// 2. gRPC allowed: UseGrpc() lets gRPC join the choice, and the card lists it first.
var cardChooses = await A2AClientFactory.CreateAsync(cardUrl, http, new A2AClientOptions().UseGrpc());
Expect("A2AClientFactory + UseGrpc()", await AskAsync(cardChooses), ProtocolBindings.Grpc);

// 3. Without UseGrpc() the factory picks from JSON-RPC and HTTP+JSON only.
var noGrpc = await A2AClientFactory.CreateAsync(cardUrl, http);
Expect("A2AClientFactory", await AskAsync(noGrpc), ProtocolBindings.JsonRpc);

// 4. Streaming works over gRPC too. gRPC streams carry no event ids, so a lost stream cannot resume with
//    Last-Event-ID: subscribe again and you get a task snapshot.
TaskState? last = null;
await foreach (var evt in grpcOnly.SendStreamingMessageAsync(Message.FromUser("stream")))
{
    if (evt is ArtifactUpdateEvent { ArtifactUpdate: var a })
        Console.WriteLine($"  chunk: {a.Artifact.GetText()}");
    if (evt is StatusUpdateEvent { StatusUpdate: var s }) last = s.Status.State;
}

Expect("gRPC stream ends as", last?.ToString(), nameof(TaskState.Completed));

await app.StopAsync();

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
    return 1;
}

Console.WriteLine("OK: the gRPC clients used gRPC, the plain client used JSON-RPC, and a gRPC stream completed.");
return 0;

static async Task<string?> AskAsync(A2AClient client)
{
    var reply = await client.SendMessageAsync("which binding?");
    return (reply as MessageResponse)?.Message.GetText();
}

/// <summary>Replies with the binding of the request. "stream" runs a short task with two artifact chunks.</summary>
internal sealed class BindingAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater,
        CancellationToken cancellationToken)
    {
        if (context.UserText != "stream")
        {
            // Call.Binding is JSONRPC, HTTP+JSON or GRPC. The agent code is the same for all three.
            await updater.ReplyAsync(context.Call.Binding ?? "unknown", cancellationToken);
            return;
        }

        await updater.StartWorkAsync(cancellationToken: cancellationToken);
        var id = await updater.AddArtifactAsync([Part.FromText("over ")], name: "answer",
            cancellationToken: cancellationToken);
        await updater.AddArtifactAsync([Part.FromText(context.Call.Binding ?? "unknown")], id, append: true,
            lastChunk: true, cancellationToken: cancellationToken);
        await updater.CompleteAsync(cancellationToken: cancellationToken);
    }
}
