using A2A;
using A2A.Client;

var agentUrl = new Uri(args.Length > 0 ? args[0] : "http://localhost:5100/");
var text = args.Length > 1 ? args[1] : "hello from the A2A .NET SDK";

using var http = new HttpClient();
var client = await A2AClientFactory.CreateAsync(agentUrl, http);
Console.WriteLine($"Connected to '{client.AgentCard!.Name}': {client.AgentCard.Description}");

// 1. Simple request/response.
var pong = await client.SendMessageAsync("ping");
Console.WriteLine($"ping -> {pong.Message?.GetText()}");

// 2. Streaming.
Console.Write("stream -> ");
await foreach (var evt in client.SendStreamingMessageAsync(Message.FromUser(text)))
{
    if (evt.ArtifactUpdate is { } a) Console.Write(string.Concat(a.Artifact.Parts.Select(p => p.Text)));
    if (evt.StatusUpdate is { } s && s.Status.State.IsTerminal()) Console.WriteLine($"  [{s.Status.State}]");
}

// 3. List my tasks.
var tasks = await client.ListTasksAsync(new ListTasksRequest { PageSize = 5 });
Console.WriteLine($"tasks visible to me: {tasks.TotalSize}");
