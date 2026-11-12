using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using A2A;
using A2A.Client;
using A2A.Extensions.AgentFramework;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;

// Hosts a Microsoft Agent Framework agent as an A2A agent, then uses it as an Agent Framework agent again, through A2A,
// from the same process. No API keys, no external calls: the agent below is a few string rules, and the server listens on
// localhost only.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0"); // loopback only, free port
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// The agent. To use a real model, register a ChatClientAgent from the Microsoft.Agents.AI package over any IChatClient:
//   builder.Services.AddSingleton<AIAgent>(new ChatClientAgent(chatClient, instructions: "Answer briefly.", name: "math"));
// The adapter itself needs only Microsoft.Agents.AI.Abstractions.
builder.Services.AddSingleton<AIAgent>(new RuleBasedAgent());

var card = new AgentCard
{
    Name = "Math Agent",
    Description = "A tiny rule-based Agent Framework agent hosted with A2A.Extensions.AgentFramework.",
    Version = "1.0.0",
    Capabilities = new AgentCapabilities { Streaming = true },
    DefaultInputModes = ["text/plain"],
    DefaultOutputModes = ["text/plain"],
    Skills = [new AgentSkill { Id = "math", Name = "Math", Description = "Adds and multiplies", Tags = ["math"] }]
};

// AllowAnonymous is fine for a sample that only listens on loopback. A real agent needs authentication
// (see samples/EchoAgent) and MapA2A(...).RequireAuthorization().
builder.Services.AddA2A(a => a
        .AddAgentCard(card)
        .AddAIAgent(o =>
        {
            o.UseContextMemory = true; // a new task in the same context sees the earlier ones
            o.StreamChunkSize = 16;
        }))
    .Configure(o => o.AllowAnonymous = true);

var app = builder.Build();
app.MapA2A("/a2a");
await app.StartAsync();

var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
Console.WriteLine($"Agent listening on {address}/a2a");

// Client side: the remote A2A agent as an Agent Framework agent. Name and description come from here, never from the card.
using var http = new HttpClient();
var client = await A2AClientFactory.CreateAsync(new Uri(address + "/"), http);
AIAgent math = client.AsAIAgent(new A2AAIAgentOptions { Name = "math", Description = "Adds and multiplies numbers." });
var failures = new List<string>();

// 1. One question, and a follow-up in the same session: the session keeps the remote context.
var session = await math.CreateSessionAsync();
var sum = await math.RunAsync("What is 2 + 3?", session);
Console.WriteLine($"2 + 3:      {sum.Text}");
Check(sum.Text == "5", "2 + 3 should be 5");
var product = await math.RunAsync("And times 4?", session);
Console.WriteLine($"times 4:    {product.Text}");
Check(product.Text == "20", "the follow-up should use the earlier answer");

// 2. Streaming: the reply arrives in chunks.
var story = new StringBuilder();
var chunks = 0;
await foreach (var update in math.RunStreamingAsync("Count to ten."))
{
    story.Append(update.Text);
    if (update.Text.Length > 0) chunks++;
}

Console.WriteLine($"count:      {story} ({chunks} chunks)");
Check(story.ToString() == "one two three four five six seven eight nine ten", "the count should be complete");
Check(chunks > 1, "the count should stream in more than one chunk");

// 3. A background response: start without waiting, then poll with the continuation token.
var job = await math.RunAsync("Slow: what is 6 + 7?", options: new AgentRunOptions { AllowBackgroundResponses = true });
var polls = 0;
while (job.ContinuationToken is { } token && polls++ < 100)
{
    await Task.Delay(50);
    job = await math.RunAsync([], options: new AgentRunOptions { ContinuationToken = token });
}

Console.WriteLine($"background: {job.Text} (after {polls} polls)");
Check(job.Text == "13", "the background job should answer 13");

await app.StopAsync();
if (failures.Count > 0)
{
    foreach (var failure in failures) Console.WriteLine($"FAIL: {failure}");
    return 1;
}

Console.WriteLine("OK: an Agent Framework agent was hosted over A2A and used as an Agent Framework agent");
return 0;

void Check(bool condition, string what)
{
    if (!condition) failures.Add(what);
}

/// <summary>
///     A stand-in for a model-backed agent: it adds or multiplies the numbers in the last user message, and "times N" uses
///     the last number it said before. "Slow" waits a little first.
/// </summary>
internal sealed partial class RuleBasedAgent : AIAgent
{
    private static readonly string[] Words = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];

    public override string? Name => "rule-based math";

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<AgentSession>(new RuleSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(session.StateBag.Serialize());

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<AgentSession>(new RuleSession());

    protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in RunCoreStreamingAsync(messages, session, options, cancellationToken)) updates.Add(update);
        return updates.ToAgentResponse();
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
        AgentSession? session = null, AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var question = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        if (question.StartsWith("Slow", StringComparison.Ordinal)) await Task.Delay(300, cancellationToken);

        if (question.Contains("Count to ten", StringComparison.Ordinal))
        {
            for (var i = 0; i < Words.Length; i++)
                yield return new AgentResponseUpdate(ChatRole.Assistant, (i == 0 ? "" : " ") + Words[i]);
            yield break;
        }

        var numbers = Numbers().Matches(question).Select(m => long.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        string answer;
        if (question.Contains("times", StringComparison.Ordinal) && numbers.Count == 1)
        {
            // The earlier answer comes from the conversation: here, the earlier task of the context (UseContextMemory).
            var earlier = list.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text;
            answer = long.TryParse(earlier, out var last) ? (last * numbers[0]).ToString(System.Globalization.CultureInfo.InvariantCulture) : "I do not know what to multiply.";
        }
        else
        {
            answer = numbers.Count > 0 ? numbers.Sum().ToString(System.Globalization.CultureInfo.InvariantCulture) : "Ask me to add numbers.";
        }

        yield return new AgentResponseUpdate(ChatRole.Assistant, answer);
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();

    private sealed class RuleSession : AgentSession;
}
