using System.Text.Json;
using System.Text.Json.Serialization;
using A2A.Client;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.TypedSkills;
using A2A.Skills;

namespace Snippets.TypedSkills;

public sealed class BookingRequest
{
    // 'required' makes the schema list the property, and a missing one is refused before your handler runs.
    public required string City { get; init; }
    public required int Nights { get; init; }
}

public sealed class BookingResult
{
    public required string Reference { get; init; }
}

// Source-generated JSON: no reflection, safe for AOT and trimming.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BookingRequest))]
[JsonSerializable(typeof(BookingResult))]
internal sealed partial class BookingJson : JsonSerializerContext
{
}

internal sealed class ChatAgent : IAgentExecutor
{
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
        updater.CompleteAsync($"You said: {context.UserText}", cancellationToken).AsTask();
}

internal static class Snippets
{
    public static void Register(IServiceCollection services, AgentCard card)
    {
        #region Server
        services.AddA2A(a => a
            .AddAgentCard(card)
            .AddTypedSkills(skills => skills
                .Map("book", BookingJson.Default.BookingRequest, BookingJson.Default.BookingResult,
                    async (context, cancellationToken) =>
                    {
                        await context.ReportProgressAsync("Looking for a room...", cancellationToken);
                        return new BookingResult { Reference = $"BK-{context.Input.City}-{context.Input.Nights}" };
                    },
                    o =>
                    {
                        o.Name = "Book a room";
                        o.Description = "Books a hotel room.";
                        o.Tags.Add("booking");
                    })
                .UseFallback<ChatAgent>()));
        #endregion
    }

    public static void Validate(TypedSkillSet skills)
    {
        #region Validate
        skills.Map("book", BookingJson.Default.BookingRequest, BookingJson.Default.BookingResult,
            (context, cancellationToken) => Task.FromResult(new BookingResult { Reference = "BK-1" }),
            o => o.Validate = request => request.Nights is < 1 or > 30
                // Say what is wrong. Never copy the caller's value into the text.
                ? [new TypedSkillValidationError("$.nights", "Nights must be between 1 and 30.")]
                : []);
        #endregion
    }

    public static void Fail(TypedSkillSet skills)
    {
        #region Fail
        skills.Map("book", BookingJson.Default.BookingRequest, BookingJson.Default.BookingResult,
            (context, cancellationToken) => context.Input.City == "Atlantis"
                // The caller sees this text. Any other exception gives the caller generic text.
                ? throw new TypedSkillFailedException("No rooms in that city.")
                : Task.FromResult(new BookingResult { Reference = "BK-1" }));
        #endregion
    }

    public static async Task CallAsync(A2AClient a2a)
    {
        #region Client
        try
        {
            var result = await a2a.CallSkillAsync("book", new BookingRequest { City = "Oslo", Nights = 3 },
                BookingJson.Default.BookingRequest, BookingJson.Default.BookingResult);
            Console.WriteLine(result.Reference);
        }
        catch (TypedSkillCallException ex)
        {
            Console.WriteLine($"The task ended as {ex.State}.");
        }
        catch (A2AException ex) when (ex.Code == A2AErrorCode.InvalidParams)
        {
            Console.WriteLine("The agent refused the input.");
        }
        #endregion
    }

    public static void ReadSchema(AgentCard card)
    {
        #region Schema
        if (TypedSkillExtension.TryGetSchemas(card, "book", out JsonElement inputSchema, out _))
            Console.WriteLine(inputSchema.GetRawText());
        #endregion
    }
}
