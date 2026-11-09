using System.Text.Json;
using A2A.Server.TypedSkills;
using A2A.Skills;

namespace A2A.Tests.TypedSkills;

public class TypedSkillCardTests
{
    private static Task<BookingResult> Book(TypedSkillContext<BookingRequest> context, CancellationToken ct) =>
        Task.FromResult(new BookingResult { City = context.Input.City, Nights = context.Input.Nights, Reference = "r1" });

    private static Task<TreeNode> Echo(TypedSkillContext<TreeNode> context, CancellationToken ct) => Task.FromResult(context.Input);

    private static TypedSkillSet NewSet() => new TypedSkillSet()
        .Map("book", TypedJson.Default.BookingRequest, TypedJson.Default.BookingResult, Book, o =>
        {
            o.Name = "Book a room";
            o.Description = "Books a room.";
            o.Tags.Add("travel");
            o.Examples.Add("Book Oslo for 2 nights");
        })
        .Map("tree", TypedJson.Default.TreeNode, TypedJson.Default.TreeNode, Echo);

    private static AgentCard NewCard() => new() { Name = "Agent", Description = "d", Version = "1" };

    private static JsonElement Schema(AgentCard card, string id, bool input)
    {
        Assert.True(TypedSkillExtension.TryGetSchemas(card, id, out var i, out var o));
        return input ? i : o;
    }

    [Fact]
    public void Card_has_the_input_schema_with_required()
    {
        var card = NewCard();
        NewSet().ApplyTo(card);
        var schema = Schema(card, "book", input: true);
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["city"], required);
        Assert.Equal("integer", schema.GetProperty("properties").GetProperty("nights").GetProperty("type").GetString());
        Assert.True(schema.GetProperty("properties").TryGetProperty("extras", out _));
    }

    [Fact]
    public void Card_has_the_output_schema()
    {
        var card = NewCard();
        NewSet().ApplyTo(card);
        var schema = Schema(card, "book", input: false);
        Assert.True(schema.GetProperty("properties").TryGetProperty("reference", out _));
    }

    [Fact]
    public void Recursive_type_has_a_schema_that_refers_to_itself()
    {
        var card = NewCard();
        NewSet().ApplyTo(card);
        var schema = Schema(card, "tree", input: true);
        Assert.Contains("\"$ref\"", schema.GetRawText());
    }

    [Fact]
    public void Card_survives_a_round_trip_through_json()
    {
        var card = NewCard();
        NewSet().ApplyTo(card);
        var copy = A2A.Json.A2AJson.Deserialize<AgentCard>(A2A.Json.A2AJson.SerializeToElement(card));
        Assert.True(TypedSkillExtension.TryGetSchemas(copy, "tree", out _, out _));
    }

    [Fact]
    public void ApplyTo_adds_the_skill_modes_and_both_extensions()
    {
        var card = NewCard();
        NewSet().ApplyTo(card);
        var skill = card.Skills.Single(s => s.Id == "book");
        Assert.Equal("Book a room", skill.Name);
        Assert.Equal("Books a room.", skill.Description);
        Assert.Equal(["travel"], skill.Tags);
        Assert.Equal(["Book Oslo for 2 nights"], skill.Examples);
        Assert.Equal(["application/json"], skill.InputModes);
        Assert.Equal(["application/json"], skill.OutputModes);
        Assert.Equal("tree", card.Skills.Single(s => s.Id == "tree").Name);
        var extensions = card.Capabilities.Extensions!;
        Assert.Contains(extensions, e => e.Uri == SkillSelection.ExtensionUri && !e.Required);
        Assert.Contains(extensions, e => e.Uri == TypedSkillExtension.ExtensionUri && !e.Required);
        Assert.False(SkillSelection.RejectsUnknownSkills(card));
    }

    [Fact]
    public void ApplyTo_is_idempotent()
    {
        var set = NewSet();
        var card = NewCard();
        set.ApplyTo(card);
        var once = A2A.Json.A2AJson.SerializeToElement(card).GetRawText();
        set.ApplyTo(card);
        Assert.Equal(once, A2A.Json.A2AJson.SerializeToElement(card).GetRawText());
    }

    [Fact]
    public void ApplyTo_merges_an_existing_skill_and_keeps_author_text_and_other_extensions()
    {
        var card = NewCard();
        card.Skills.Add(new AgentSkill { Id = "book", Name = "Mine", Description = "My text", Tags = ["x"], InputModes = ["text/plain"] });
        card.Capabilities.Extensions = [new AgentExtension { Uri = "urn:other" }, SkillSelection.CreateExtension(rejectUnknownSkills: true)];
        NewSet().ApplyTo(card);
        var skill = card.Skills.Single(s => s.Id == "book");
        Assert.Equal("Mine", skill.Name);
        Assert.Equal("My text", skill.Description);
        Assert.Equal(["x"], skill.Tags);
        Assert.Equal(["text/plain", "application/json"], skill.InputModes);
        Assert.Equal(["Book Oslo for 2 nights"], skill.Examples);
        Assert.Single(card.Skills, s => s.Id == "book");
        Assert.Contains(card.Capabilities.Extensions, e => e.Uri == "urn:other");
        Assert.Single(card.Capabilities.Extensions, e => e.Uri == SkillSelection.ExtensionUri);
        Assert.True(SkillSelection.RejectsUnknownSkills(card)); // the author's choice stays
    }

    [Fact]
    public void ApplyTo_throws_on_a_signed_card_and_changes_nothing()
    {
        var card = NewCard();
        card.Signatures = [new AgentCardSignature { Protected = "p", Signature = "s" }];
        Assert.Throws<InvalidOperationException>(() => NewSet().ApplyTo(card));
        Assert.Empty(card.Skills);
        Assert.Null(card.Capabilities.Extensions);
    }

    [Fact]
    public void TryGetSchemas_is_false_for_a_plain_card_and_an_unknown_skill()
    {
        Assert.False(TypedSkillExtension.TryGetSchemas(NewCard(), "book", out _, out _));
        var card = NewCard();
        NewSet().ApplyTo(card);
        Assert.False(TypedSkillExtension.TryGetSchemas(card, "nope", out _, out _));
    }

    [Fact]
    public void TryGetSchemas_is_false_when_the_params_are_malformed()
    {
        var card = NewCard();
        card.Capabilities.Extensions =
        [
            new AgentExtension
            {
                Uri = TypedSkillExtension.ExtensionUri,
                Params = new() { [TypedSkillExtension.SkillsParam] = JsonDocument.Parse("""{"book":{"inputSchema":5}}""").RootElement.Clone() },
            },
        ];
        Assert.False(TypedSkillExtension.TryGetSchemas(card, "book", out _, out _));
    }

    [Fact]
    public void Map_rejects_a_duplicate_id_and_an_empty_id()
    {
        var set = NewSet();
        Assert.Throws<ArgumentException>(() => set.Map("book", TypedJson.Default.BookingRequest, TypedJson.Default.BookingResult, Book));
        Assert.Throws<ArgumentException>(() => set.Map("", TypedJson.Default.BookingRequest, TypedJson.Default.BookingResult, Book));
    }
}
