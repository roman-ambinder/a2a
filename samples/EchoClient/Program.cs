using A2A;
using A2A.Client;
using A2A.Json;
using A2A.Signing;
using Microsoft.Extensions.DependencyInjection;

var agentUrl = new Uri(args.Length > 0 ? args[0] : "http://localhost:5100/");
var text = args.Length > 1 ? args[1] : "hello from the A2A .NET SDK";
var apiKey = args.Length > 2
    ? args[2]
    : Environment.GetEnvironmentVariable("ECHO_AGENT_API_KEY")
      ?? throw new InvalidOperationException("Pass the API key as the third argument or set ECHO_AGENT_API_KEY.");
var jwksFile = Environment.GetEnvironmentVariable("ECHO_AGENT_JWKS_FILE");

// The agent requires an API key. Its card names the header (X-Api-Key); AddApiKey sends the key there, only to the
// agent's origin, and only over HTTPS or loopback.
var services = new ServiceCollection();
services.AddA2AClient("echo", o =>
{
    o.AgentUrl = agentUrl;
    // When the agent signs its card, pin the agent's JWKS for its origin. A card that does not verify is then refused,
    // and a card with a different key is never trusted just because it was served from the same host.
    if (jwksFile is null) return;
    var jwks = A2AJson.Deserialize<JwkSet>(File.ReadAllText(jwksFile));
    o.Resolver.Trust.TrustedKeys[CardTrustPolicy.OriginOf(agentUrl)] = jwks;
}).AddApiKey(apiKey);
await using var provider = services.BuildServiceProvider();
var client = await provider.GetRequiredService<IA2AClientFactory>().GetClientAsync("echo");
Console.WriteLine($"Connected to '{client.AgentCard!.Name}': {client.AgentCard.Description}");
Console.WriteLine(jwksFile is null
    ? "Card signature: not checked (no JWKS pinned)"
    : "Card signature: verified against the pinned JWKS");

// 1. Simple request/response.
var pong = await client.SendMessageAsync("ping");
Console.WriteLine($"ping -> {(pong as MessageResponse)?.Message.GetText()}");

// 2. Streaming.
Console.Write("stream -> ");
#region Send
await foreach (var evt in client.SendStreamingMessageAsync(Message.FromUser(text)))
{
    switch (evt)
    {
        case ArtifactUpdateEvent { ArtifactUpdate: var a }:
            Console.Write(string.Concat(a.Artifact.Parts.OfType<TextPart>().Select(p => p.Text)));
            break;
        case StatusUpdateEvent { StatusUpdate: var s } when s.Status.State.IsTerminal():
            Console.WriteLine($"  [{s.Status.State}]");
            break;
    }
}
#endregion

// 3. Start a task without waiting, then follow it. ObserveTaskAsync reconnects with Last-Event-ID if the connection
//    drops, so no event is lost or repeated.
#region Observe
var started =
    await client.SendMessageAsync(Message.FromUser(text), new SendMessageConfiguration { ReturnImmediately = true });
if (started is TaskResponse { Task: var task })
{
    Console.Write($"observe {task.Id} -> ");
    await foreach (var evt in client.ObserveTaskAsync(task.Id))
    {
        switch (evt)
        {
            case ArtifactUpdateEvent { ArtifactUpdate: var a }:
                Console.Write(string.Concat(a.Artifact.Parts.OfType<TextPart>().Select(p => p.Text)));
                break;
            case StatusUpdateEvent { StatusUpdate: var s } when s.Status.State.IsTerminal():
                Console.WriteLine($"  [{s.Status.State}]");
                break;
            case TaskEvent { Task: var t } when t.Status.State.IsTerminal():
                Console.WriteLine($"  [{t.Status.State}]");
                break;
        }
    }
}
#endregion

// 4. List my tasks.
var tasks = await client.ListTasksAsync(new ListTasksRequest { PageSize = 5 });
Console.WriteLine($"tasks visible to me: {tasks.TotalSize}");

