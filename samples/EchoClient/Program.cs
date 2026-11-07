using A2A;
using A2A.Client;
using Microsoft.Extensions.DependencyInjection;

var agentUrl = new Uri(args.Length > 0 ? args[0] : "http://localhost:5100/");
var text = args.Length > 1 ? args[1] : "hello from the A2A .NET SDK";
var apiKey = args.Length > 2 ? args[2] : Environment.GetEnvironmentVariable("ECHO_AGENT_API_KEY")
    ?? throw new InvalidOperationException("Pass the API key as the third argument or set ECHO_AGENT_API_KEY.");

// The agent requires the key in the X-Api-Key header on every A2A call.
var services = new ServiceCollection();
services.AddA2AClient("echo", o => o.AgentUrl = agentUrl)
    .AddHttpMessageHandler(() => new ApiKeyHandler(apiKey));
await using var provider = services.BuildServiceProvider();
var client = await provider.GetRequiredService<IA2AClientFactory>().GetClientAsync("echo");
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

/// <summary>Adds the <c>X-Api-Key</c> header to every request.</summary>
internal sealed class ApiKeyHandler(string apiKey) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove("X-Api-Key");
        request.Headers.Add("X-Api-Key", apiKey);
        return base.SendAsync(request, cancellationToken);
    }
}
