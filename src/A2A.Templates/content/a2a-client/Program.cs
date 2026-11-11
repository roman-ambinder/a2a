using A2A.Client;

// Usage: dotnet run -- <agent URL> [text]. The default is an agent made with "dotnet new a2a-agent", run locally.
var agentUrl = new Uri(args.Length > 0 ? args[0] : "http://localhost:5000/");
var text = args.Length > 1 ? args[1] : "hello";

// Fetches the Agent Card at /.well-known/agent-card.json and picks the binding the card lists first.
// For an agent that needs credentials, pass credential providers instead of an HttpClient, for example
// [new ApiKeyCredentialProvider(key)] (namespace A2A.Client.Credentials).
using var http = new HttpClient();
var client = await A2AClientFactory.CreateAsync(agentUrl, http);
Console.WriteLine($"Connected to '{client.AgentCard!.Name}': {client.AgentCard.Description}");

try
{
    // One answer: the text of the reply, or of the task's artifacts once it completed.
    Console.WriteLine($"answer: {await client.AskAsync(text)}");

    // The same, streamed chunk by chunk.
    Console.Write("stream: ");
    await foreach (var chunk in client.StreamTextAsync(text))
        Console.Write(chunk);
    Console.WriteLine();
}
catch (TaskNotCompletedException e)
{
    // The task failed, or the agent needs input: e.Task holds its id and context to continue.
    Console.WriteLine($"The task ended in state {e.State}: {e.Task.Status.Message?.GetText()}");
}
