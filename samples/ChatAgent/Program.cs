using System.Runtime.CompilerServices;
using System.Text.Json;
using A2A;
using A2A.Client;
using A2A.Extensions.AI;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;

// Hosts a chat client as an A2A agent, then calls it as an AI tool from the same process.
// No API keys, no external calls: the "model" below is a few string rules and the server listens on localhost only.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0"); // loopback only, free port
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// The chat client behind the agent. To use a real model, register any IChatClient here instead, for example
// (package Microsoft.Extensions.AI.OpenAI or any other provider that implements IChatClient):
//   builder.Services.AddSingleton<IChatClient>(new OpenAIClient(apiKey).GetChatClient("gpt-4o-mini").AsIChatClient());
// Add .AsBuilder().UseFunctionInvocation().Build() when the model should call tools of its own.
builder.Services.AddSingleton<IChatClient>(new RuleBasedChatClient());

var card = new AgentCard
{
    Name = "Chat Agent",
    Description = "A tiny rule-based chat agent hosted with A2A.Extensions.AI.",
    Version = "1.0.0",
    Capabilities = new AgentCapabilities { Streaming = true },
    DefaultInputModes = ["text/plain"],
    DefaultOutputModes = ["text/plain"],
    Skills = [new AgentSkill { Id = "chat", Name = "Chat", Description = "Answers simple questions", Tags = ["chat"] }]
};

// AllowAnonymous is fine for a sample that only listens on loopback. A real agent needs authentication
// (see samples/EchoAgent) and MapA2A(...).RequireAuthorization().
builder.Services.AddA2A(a => a
        .AddAgentCard(card)
        .AddChatClientAgent(o => o.Instructions = "You are a helpful, short-spoken agent."))
    .Configure(o => o.AllowAnonymous = true);

var app = builder.Build();
app.MapA2A("/a2a");
await app.StartAsync();

var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
Console.WriteLine($"Agent listening on {address}/a2a");

// Client side: wrap the remote agent as an AI function. A chat client with function invocation could now call it as a tool.
// The endpoint is known (this process hosts it), so there is no card to fetch.
using var http = new HttpClient();
var client = A2AClientFactory.CreateForEndpoint(new Uri($"{address}/a2a"), http);
var tool = client.AsAIFunction("ask_chat_agent", "Asks the chat agent a question and returns its answer.");

var result = await tool.InvokeAsync(new AIFunctionArguments { ["message"] = "What time is it?" });
var json = (JsonElement)result!;
Console.WriteLine($"state:   {json.GetProperty("state").GetString()}");
Console.WriteLine($"answer:  {json.GetProperty("remoteContent").GetString()}");
Console.WriteLine($"notice:  {json.GetProperty("notice").GetString()}");

await app.StopAsync();

/// <summary>A stand-in for a real model. It answers from simple rules and streams the answer word by word.</summary>
internal sealed class RuleBasedChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer(messages))));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var words = Answer(messages).Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, (i == 0 ? "" : " ") + words[i]);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return null;
    }

    public void Dispose()
    {
    }

    private static string Answer(IEnumerable<ChatMessage> messages)
    {
        var question = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        if (question.Contains("time", StringComparison.OrdinalIgnoreCase))
            return $"It is {DateTimeOffset.UtcNow:HH:mm} UTC.";
        return question.Contains("hello", StringComparison.OrdinalIgnoreCase) ? "Hello! Ask me what time it is." : $"You said: {question}";
    }
}