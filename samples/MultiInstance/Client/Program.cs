using A2A;
using A2A.Client;
using Microsoft.Extensions.DependencyInjection;

// Usage: MultiInstanceClient <instance A url> <instance B url> [api key]
// Starts a task on A, then reads, follows and cancels it through B.
var urlA = new Uri(args.Length > 0 ? args[0] : "http://localhost:5101/");
var urlB = new Uri(args.Length > 1 ? args[1] : "http://localhost:5102/");
var apiKey = args.Length > 2 ? args[2] : Environment.GetEnvironmentVariable("DEMO_API_KEY") ?? "demo-key-change-me";

var services = new ServiceCollection();
services.AddA2AClient("a", o => o.AgentUrl = urlA).AddHttpMessageHandler(() => new ApiKeyHandler(apiKey));
services.AddA2AClient("b", o => o.AgentUrl = urlB).AddHttpMessageHandler(() => new ApiKeyHandler(apiKey));
await using var provider = services.BuildServiceProvider();
var factory = provider.GetRequiredService<IA2AClientFactory>();
var a = await factory.GetClientAsync("a");
var b = await factory.GetClientAsync("b");

// 1. Start a task on instance A and return at once.
var started = await a.SendMessageAsync(Message.FromUser("count for me"),
    new SendMessageConfiguration { ReturnImmediately = true });
var task = (started as TaskResponse)?.Task ?? throw new InvalidOperationException("Expected a task.");
Console.WriteLine($"A: started task {task.Id} ({task.Status.State})");

// 2. Read it through instance B: it is in the shared store.
var seen = await b.GetTaskAsync(task.Id);
Console.WriteLine($"B: GetTask -> {seen.Status.State}");

// 3. Follow it through B: live events come over Redis from the instance that runs it.
var steps = 0;
await foreach (var evt in b.SubscribeToTaskAsync(task.Id))
{
    if (evt is ArtifactUpdateEvent { ArtifactUpdate: var u })
    {
        steps++;
        Console.WriteLine(
            $"B: event -> {string.Concat(u.Artifact.Parts.OfType<TextPart>().Select(p => p.Text)).Trim()}");
    }

    if (steps >= 3) break;
}

// 4. Cancel it through B. The cancel request reaches the instance that runs the agent.
var canceled = await b.CancelTaskAsync(task.Id);
Console.WriteLine($"B: CancelTask -> {canceled.Status.State}");

// 5. Read the result through A.
var final = await a.WaitForTaskAsync(task.Id);
Console.WriteLine($"A: GetTask -> {final.Status.State}");
if (final.Status.State != TaskState.Canceled)
{
    Console.Error.WriteLine("Expected CANCELED.");
    return 1;
}

Console.WriteLine("OK: a task started on A was read, followed and cancelled through B.");
return 0;

internal sealed class ApiKeyHandler(string apiKey) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Remove("X-Api-Key");
        request.Headers.Add("X-Api-Key", apiKey);
        return base.SendAsync(request, cancellationToken);
    }
}