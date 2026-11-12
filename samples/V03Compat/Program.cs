using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using A2A;
using System.Net;
using A2A.Client;
using A2A.Client.Grpc.V0_3Compat;
using A2A.Client.V0_3Compat;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Grpc;
using A2A.Server.Grpc.V0_3Compat;
using A2A.Server.V0_3Compat;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// A v1.0 agent that also answers v0.3 clients, and the client side that calls v0.3 agents.
// The server counts each request it serves on the v0.3 surface (counter a2a.compat.v0_3.requests on the "A2A" meter);
// the sample reads that counter to check which protocol each call used.
// No API keys, no external calls: the server listens on 127.0.0.1 only.

const string url = "http://127.0.0.1:5320/";
// gRPC without TLS needs an HTTP/2-only endpoint (prior knowledge): a second port.
const string grpcUrl = "http://127.0.0.1:5321";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(IPAddress.Loopback, 5320, o => o.Protocols = HttpProtocols.Http1);
    k.Listen(IPAddress.Loopback, 5321, o => o.Protocols = HttpProtocols.Http2);
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var card = new AgentCard
{
    Name = "Two-version Agent",
    Description = "A v1.0 agent that older clients can reach too.",
    Version = "1.0.0",
    // The blended card lists the v0.3 members (url, preferredTransport, additionalInterfaces) from these interfaces.
    // GRPC is listed for v0.3 too, because AddA2AGrpcV03Compat() serves v0.3 gRPC next to v1.0 gRPC.
    SupportedInterfaces =
    [
        new AgentInterface { Url = url, ProtocolBinding = ProtocolBindings.JsonRpc },
        new AgentInterface { Url = url, ProtocolBinding = ProtocolBindings.HttpJson },
        new AgentInterface { Url = grpcUrl, ProtocolBinding = ProtocolBindings.Grpc }
    ],
    Capabilities = new AgentCapabilities { Streaming = true },
    DefaultInputModes = ["text/plain"],
    DefaultOutputModes = ["text/plain"],
    Skills = [new AgentSkill { Id = "echo", Name = "Echo", Description = "Repeats the input", Tags = ["echo"] }]
};

// AllowAnonymous is fine for a sample that only listens on loopback. A real agent needs authentication
// (see samples/EchoAgent) and RequireAuthorization() on MapA2A; that covers the v0.3 routes too.
builder.Services.AddA2A(a => a.AddAgentExecutor<EchoAgent>().AddAgentCard(card))
    .Configure<A2AServerOptions>(o => o.AllowAnonymous = true);
// v0.3 gRPC (service a2a.v1.A2AService), next to the v1.0 gRPC service.
builder.Services.AddA2AGrpcV03Compat();

var app = builder.Build();
app.UseA2A();
// EnableV03Compat adds the v0.3 JSON-RPC methods on the same URL, the v0.3 HTTP+JSON routes under /v1/...,
// and the v0.3 members on the Agent Card. v1.0 calls are unchanged.
app.MapA2A("/", o => o.EnableV03Compat());
app.MapA2AGrpc();
app.MapA2AGrpcV03Compat();
await app.StartAsync();
Console.WriteLine($"Agent listening on {url}");

using var v03Requests = new V03RequestCounter();
using var http = new HttpClient();
var failures = new List<string>();

// Call it after the request has finished: the arguments are read in order.
void Expect(string what, long servedOnV03, long expected, string? text)
{
    Console.WriteLine($"{what}: reply '{text}', served on v0.3: {servedOnV03}");
    if (servedOnV03 != expected) failures.Add($"{what}: expected {expected} v0.3 request(s), got {servedOnV03}");
    if (text != "echo: hello") failures.Add($"{what}: unexpected reply '{text}'");
}

// 1. A v1.0 client: the normal v1.0 path.
var v1 = await A2AClientFactory.CreateAsync(new Uri(url), http);
var before = v03Requests.Count;
var reply = TextOf(await v1.SendMessageAsync("hello"));
Expect("v1.0 client", v03Requests.Count - before, 0, reply);

// 2. An old v0.3 client, as it looks on the wire: method "message/send", parts with "kind", no A2A-Version header.
before = v03Requests.Count;
const string v03Body = """
                       {"jsonrpc":"2.0","id":1,"method":"message/send","params":{"message":{
                         "kind":"message","messageId":"m-1","role":"user","parts":[{"kind":"text","text":"hello"}]}}}
                       """;
using var response = await http.PostAsync(url, new StringContent(v03Body, Encoding.UTF8, "application/json"));
using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
var result = json.RootElement.GetProperty("result"); // the tagged object: "kind": "message"
Expect($"raw v0.3 request (result kind '{result.GetProperty("kind").GetString()}')", v03Requests.Count - before, 1,
    result.GetProperty("parts")[0].GetProperty("text").GetString());

// 3. The client side, calling an agent that only speaks v0.3. This card stands in for such an agent: its only
//    interface is marked 0.3, so V03CompatClientFactory picks the v0.3 JSON-RPC transport. It points at this
//    agent's v0.3 surface. CreateAsync does the same from a v0.3 card that it fetches and converts.
var v03OnlyCard = new AgentCard
{
    Name = "Old Agent",
    Description = "Speaks v0.3 only.",
    Version = "1.0.0",
    SupportedInterfaces =
        [new AgentInterface { Url = url, ProtocolBinding = ProtocolBindings.JsonRpc, ProtocolVersion = "0.3" }]
};
var v03Client = V03CompatClientFactory.Create(v03OnlyCard, http);
before = v03Requests.Count;
reply = TextOf(await v03Client.SendMessageAsync("hello"));
Expect("V03CompatClientFactory, v0.3-only card", v03Requests.Count - before, 1, reply);

// 4. The same factory on a card that offers v1.0 (this agent's blended card): never downgraded.
var notDowngraded = await V03CompatClientFactory.CreateAsync(new Uri(url), http);
before = v03Requests.Count;
reply = TextOf(await notDowngraded.SendMessageAsync("hello"));
Expect("V03CompatClientFactory, blended card", v03Requests.Count - before, 0, reply);

// 5. The same over v0.3 gRPC: a card whose only interface is v0.3 GRPC. UseGrpcV03() adds the v0.3 gRPC transport.
var v03GrpcCard = new AgentCard
{
    Name = "Old gRPC Agent",
    Description = "Speaks v0.3 over gRPC only.",
    Version = "1.0.0",
    SupportedInterfaces =
        [new AgentInterface { Url = grpcUrl, ProtocolBinding = ProtocolBindings.Grpc, ProtocolVersion = "0.3" }]
};
var v03Grpc = V03CompatClientFactory.Create(v03GrpcCard, http, new A2AClientOptions().UseGrpcV03());
before = v03Requests.Count;
reply = TextOf(await v03Grpc.SendMessageAsync("hello"));
Expect("V03CompatClientFactory, v0.3 gRPC card", v03Requests.Count - before, 1, reply);

await app.StopAsync();

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
    return 1;
}

Console.WriteLine("OK: v1.0 and v0.3 clients got the same answer, and only the v0.3 calls used the v0.3 surface.");
return 0;

static string? TextOf(SendMessageResponse reply) => (reply as MessageResponse)?.Message.GetText();

/// <summary>Replies directly with the text it got.</summary>
internal sealed class EchoAgent : IAgentExecutor
{
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
        updater.ReplyAsync($"echo: {context.UserText}", cancellationToken).AsTask();
}

/// <summary>Adds up the server's <c>a2a.compat.v0_3.requests</c> counter in this process.</summary>
internal sealed class V03RequestCounter : IDisposable
{
    private readonly MeterListener _listener = new();
    private long _count;

    public V03RequestCounter()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument is { Meter.Name: "A2A", Name: "a2a.compat.v0_3.requests" })
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _count, value));
        _listener.Start();
    }

    public long Count => Interlocked.Read(ref _count);

    public void Dispose() => _listener.Dispose();
}
