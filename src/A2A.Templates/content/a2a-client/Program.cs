using A2A.Client;
using A2A.Client.Credentials;

// Usage: dotnet run -- <agent URL> [text]. The default is an agent made with "dotnet new a2a-agent", run locally.
var agentUrl = new Uri(args.Length > 0 ? args[0] : "http://localhost:5000/");
var text = args.Length > 1 ? args[1] : "hello";

// Fetches the Agent Card at /.well-known/agent-card.json and picks the binding the card lists first. An API key in the
// environment variable A2A_API_KEY is sent where the card asks for one, only to the agent's origin, and only over HTTPS
// or to loopback. Other schemes: pass another credential provider, for example OAuth2ClientCredentialsProvider.
var apiKey = Environment.GetEnvironmentVariable("A2A_API_KEY");
using var http = new HttpClient();
var client = string.IsNullOrEmpty(apiKey)
    ? await A2AClientFactory.CreateAsync(agentUrl, http)
    : await A2AClientFactory.CreateAsync(agentUrl, [new ApiKeyCredentialProvider(apiKey)]);
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
