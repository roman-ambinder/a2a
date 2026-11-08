using System.Text.Json;
using A2A.Json;
using A2A.Skills;

namespace A2A.Tests;

public class SkillSelectionTests
{
    private static JsonElement J(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Message NewMessage() => new()
    {
        MessageId = "m1",
        Role = Role.User,
        Parts = [Part.FromText("hi")],
    };

    private static Message WithSkill(string json)
    {
        var message = NewMessage();
        message.Metadata = new() { [SkillSelection.MetadataKey] = J(json) };
        return message;
    }

    private static AgentCard NewCard() => new() { Name = "n", Description = "d", Version = "1" };

    private static AgentCard CardWith(params AgentExtension[] extensions)
    {
        var card = NewCard();
        card.Capabilities.Extensions = [.. extensions];
        return card;
    }

    [Fact]
    public void CreateExtension_defaults()
    {
        var extension = SkillSelection.CreateExtension();

        Assert.Equal(SkillSelection.ExtensionUri, extension.Uri);
        Assert.False(extension.Required);
        Assert.Null(extension.Params);
    }

    [Fact]
    public void CreateExtension_with_reject_is_detected()
    {
        var card = CardWith(SkillSelection.CreateExtension(rejectUnknownSkills: true));

        Assert.True(SkillSelection.RejectsUnknownSkills(card));
    }

    [Fact]
    public void RejectsUnknownSkills_is_false_without_extensions() =>
        Assert.False(SkillSelection.RejectsUnknownSkills(NewCard()));

    [Fact]
    public void RejectsUnknownSkills_is_false_for_other_uri() =>
        Assert.False(SkillSelection.RejectsUnknownSkills(CardWith(new AgentExtension
        {
            Uri = "https://example.com/other",
            Params = new() { [SkillSelection.RejectUnknownSkillsParam] = J("true") },
        })));

    [Fact]
    public void RejectsUnknownSkills_is_false_without_params() =>
        Assert.False(SkillSelection.RejectsUnknownSkills(CardWith(new AgentExtension { Uri = SkillSelection.ExtensionUri })));

    [Fact]
    public void RejectsUnknownSkills_is_false_for_false_param() =>
        Assert.False(SkillSelection.RejectsUnknownSkills(CardWith(new AgentExtension
        {
            Uri = SkillSelection.ExtensionUri,
            Params = new() { [SkillSelection.RejectUnknownSkillsParam] = J("false") },
        })));

    [Fact]
    public void RejectsUnknownSkills_is_false_for_string_true() =>
        Assert.False(SkillSelection.RejectsUnknownSkills(CardWith(new AgentExtension
        {
            Uri = SkillSelection.ExtensionUri,
            Params = new() { [SkillSelection.RejectUnknownSkillsParam] = J("\"true\"") },
        })));

    [Fact]
    public void Set_then_get_round_trips() =>
        Assert.Equal("code-review", NewMessage().SetRequestedSkillId("code-review").GetRequestedSkillId());

    [Fact]
    public void Set_creates_metadata_when_null()
    {
        var message = NewMessage();
        Assert.Null(message.Metadata);

        var returned = message.SetRequestedSkillId("a");

        Assert.Same(message, returned);
        Assert.NotNull(message.Metadata);
    }

    [Fact]
    public void Set_keeps_other_keys()
    {
        var message = NewMessage();
        message.Metadata = new() { ["other"] = J("1") };

        message.SetRequestedSkillId("a");

        Assert.True(message.Metadata.ContainsKey("other"));
        Assert.Equal("a", message.GetRequestedSkillId());
    }

    [Fact]
    public void Get_is_null_without_metadata() => Assert.Null(NewMessage().GetRequestedSkillId());

    [Fact]
    public void Get_is_null_without_key()
    {
        var message = NewMessage();
        message.Metadata = new() { ["other"] = J("\"x\"") };

        Assert.Null(message.GetRequestedSkillId());
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("42")]
    [InlineData("{\"a\":1}")]
    [InlineData("null")]
    public void Get_is_null_for_bad_shapes(string json) => Assert.Null(WithSkill(json).GetRequestedSkillId());

    [Fact]
    public void Set_rejects_null_and_empty()
    {
        Assert.ThrowsAny<ArgumentException>(() => NewMessage().SetRequestedSkillId(null!));
        Assert.ThrowsAny<ArgumentException>(() => NewMessage().SetRequestedSkillId(""));
    }

    [Fact]
    public void Skill_id_survives_json_round_trip()
    {
        var json = A2AJson.Serialize(NewMessage().SetRequestedSkillId("code-review"));

        var back = A2AJson.Deserialize<Message>(json);

        Assert.Equal("code-review", back.GetRequestedSkillId());
    }
}
