using System.Text.Json.Serialization;
using A2A;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Json;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.TypedSkills;
using A2A.Skills;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

// An agent with one typed skill and a plain fallback executor. A client calls the skill with .NET types.
// No API keys, no external calls: the server listens on localhost only.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0"); // loopback only, free port
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// No skills on the card: AddTypedSkills adds the "book" skill, its schemas and the extension entries.
var card = new AgentCard
{
    Name = "Booking Agent",
    Description = "Books a hotel room. Also chats, as a plain agent.",
    Version = "1.0.0",
    DefaultInputModes = ["text/plain"],
    DefaultOutputModes = ["text/plain"],
};

// AllowAnonymous is fine for a sample that only listens on loopback. A real agent needs authentication
// (see samples/EchoAgent) and MapA2A(...).RequireAuthorization().
builder.Services.AddA2A(a => a
        .AddAgentCard(card)
        .AddTypedSkills(skills => skills
            .Map("book", BookingJson.Default.BookingRequest, BookingJson.Default.BookingResult,
                (context, cancellationToken) => Task.FromResult(new BookingResult
                {
                    Reference = $"BK-{context.Input.City.ToUpperInvariant()}-{context.Input.Nights}",
                    Total = 89.5m * context.Input.Nights,
                }),
                o =>
                {
                    o.Name = "Book a room";
                    o.Description = "Books a hotel room.";
                    o.Tags.Add("booking");
                    // Rules the type cannot say. Describe the problem; never copy the caller's value into the text.
                    o.Validate = request => request.Nights is < 1 or > 30
                        ? [new TypedSkillValidationError("$.nights", "Nights must be between 1 and 30.")]
                        : [];
                })
            .UseFallback<ChatAgent>()))
    .Configure<A2AServerOptions>(o => o.AllowAnonymous = true);

var app = builder.Build();
app.UseA2A();
app.MapA2A("/a2a");
await app.StartAsync();

var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
Console.WriteLine($"Agent listening on {address}/a2a");

var options = new A2AClientOptions();
using var http = new HttpClient();

// The card carries the schemas. A client can read them to build a form, or to check its own input.
var cardJson = await http.GetStringAsync($"{address}/.well-known/agent-card.json");
var served = A2AJson.Deserialize<AgentCard>(cardJson);
if (TypedSkillExtension.TryGetSchemas(served, "book", out var inputSchema, out _))
{
    var required = inputSchema.TryGetProperty("required", out var r) ? string.Join(", ", r.EnumerateArray().Select(e => e.GetString())) : "none";
    Console.WriteLine($"card: skill 'book' takes {string.Join(", ", inputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name))}; required: {required}");
}

var client = new A2AClient(new JsonRpcTransport(http, new Uri($"{address}/a2a"), options), options, served);

Console.WriteLine();
Console.WriteLine("--- typed call");
var result = await client.CallSkillAsync("book", new BookingRequest { City = "Oslo", Nights = 3 },
    BookingJson.Default.BookingRequest, BookingJson.Default.BookingResult);
Console.WriteLine($"result: reference {result.Reference}, total {result.Total}");

Console.WriteLine();
Console.WriteLine("--- bad input (nights = 99)");
try
{
    await client.CallSkillAsync("book", new BookingRequest { City = "Oslo", Nights = 99 },
        BookingJson.Default.BookingRequest, BookingJson.Default.BookingResult);
}
catch (A2AException ex)
{
    // The agent refused before it ran: nothing was stored, and the handler never started.
    Console.WriteLine($"refused: {ex.Code}: {ex.Message}");
    foreach (var detail in ex.Details ?? [])
        if (detail.TryGetProperty("fieldViolations", out var violations))
            foreach (var violation in violations.EnumerateArray())
                Console.WriteLine($"  {violation.GetProperty("field").GetString()}: {violation.GetProperty("description").GetString()}");
}

Console.WriteLine();
Console.WriteLine("--- plain message (goes to the fallback)");
var reply = await client.SendMessageAsync("Hello there");
Console.WriteLine($"fallback says: {reply switch { TaskResponse t => t.Task.Status.Message!.GetText(), MessageResponse m => m.Message.GetText(), _ => "?" }}");

await app.StopAsync();

public sealed class BookingRequest
{
    // Mark required properties with 'required': the schema lists them, and a missing one is refused before the handler runs.
    public required string City { get; init; }
    public required int Nights { get; init; }
}

public sealed class BookingResult
{
    public required string Reference { get; init; }
    public required decimal Total { get; init; }
}

// Source-generated JSON: no reflection, safe for AOT and trimming.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BookingRequest))]
[JsonSerializable(typeof(BookingResult))]
internal sealed partial class BookingJson : JsonSerializerContext
{
}

/// <summary>A plain executor for everything that does not name the typed skill.</summary>
internal sealed class ChatAgent : IAgentExecutor
{
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
        updater.CompleteAsync($"You said: {context.UserText}", cancellationToken).AsTask();
}
