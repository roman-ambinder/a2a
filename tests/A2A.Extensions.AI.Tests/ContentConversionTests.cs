using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace A2A.Extensions.AI.Tests;

public class ContentConversionTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---- A2A -> AIContent ----

    [Fact]
    public void TextPart_becomes_TextContent()
    {
        var content = Assert.IsType<TextContent>(Part.FromText("hello").ToAIContent());
        Assert.Equal("hello", content.Text);
    }

    [Fact]
    public void RawPart_becomes_DataContent_with_filename_as_name()
    {
        var part = Part.FromBytes(new byte[] { 1, 2, 3 }, "image/png", "a.png");
        var content = Assert.IsType<DataContent>(part.ToAIContent());
        Assert.Equal("image/png", content.MediaType);
        Assert.Equal("a.png", content.Name);
        Assert.Equal(new byte[] { 1, 2, 3 }, content.Data.ToArray());
    }

    [Fact]
    public void RawPart_without_media_type_is_octet_stream()
    {
        var part = new RawPart(new byte[] { 9 });
        var content = Assert.IsType<DataContent>(part.ToAIContent());
        Assert.Equal("application/octet-stream", content.MediaType);
    }

    [Fact]
    public void RawPart_with_malformed_media_type_falls_back_and_does_not_throw()
    {
        var part = new RawPart(new byte[] { 9 }) { MediaType = "not a media type" };
        var content = Assert.IsType<DataContent>(part.ToAIContent());
        Assert.Equal("application/octet-stream", content.MediaType);
        Assert.Equal(new byte[] { 9 }, content.Data.ToArray());
    }

    [Fact]
    public void UrlPart_becomes_UriContent()
    {
        var part = Part.FromUrl(new Uri("https://example.com/a.png"), "image/png");
        var content = Assert.IsType<UriContent>(part.ToAIContent());
        Assert.Equal(new Uri("https://example.com/a.png"), content.Uri);
        Assert.Equal("image/png", content.MediaType);
    }

    [Fact]
    public void UrlPart_without_media_type_is_octet_stream()
    {
        var content = Assert.IsType<UriContent>(Part.FromUrl(new Uri("https://example.com/x")).ToAIContent());
        Assert.Equal("application/octet-stream", content.MediaType);
    }

    [Fact]
    public void UrlPart_with_malformed_media_type_falls_back()
    {
        var content = Assert.IsType<UriContent>(Part.FromUrl(new Uri("https://example.com/x"), "nonsense").ToAIContent());
        Assert.Equal("application/octet-stream", content.MediaType);
    }

    [Fact]
    public void UrlPart_with_relative_url_becomes_base_AIContent_that_has_no_part()
    {
        var part = new UrlPart(new Uri("a/b.png", UriKind.Relative));
        var content = part.ToAIContent();
        Assert.Equal(typeof(AIContent), content.GetType());
        Assert.Same(part, content.RawRepresentation);
        Assert.Null(content.ToPart());
    }

    [Fact]
    public void UriContent_with_relative_uri_has_no_part()
    {
        var content = new UriContent(new Uri("rel/x.png", UriKind.Relative), "image/png");
        Assert.Null(content.ToPart());
    }

    [Fact]
    public void DataPart_becomes_DataContent_with_json_bytes()
    {
        var content = Assert.IsType<DataContent>(Part.FromData(Json("""{"a": 1}""")).ToAIContent());
        Assert.Equal("application/json", content.MediaType);
        using var doc = JsonDocument.Parse(content.Data);
        Assert.Equal(1, doc.RootElement.GetProperty("a").GetInt32());
    }

    [Theory]
    [InlineData("application/vnd.acme+json", "application/vnd.acme+json")]
    [InlineData("application/json", "application/json")]
    [InlineData("text/plain", "application/json")]
    [InlineData(null, "application/json")]
    public void DataPart_keeps_json_like_media_type_else_application_json(string? partType, string expected)
    {
        var part = new DataPart(Json("{}")) { MediaType = partType };
        var content = Assert.IsType<DataContent>(part.ToAIContent());
        Assert.Equal(expected, content.MediaType);
    }

    [Fact]
    public void ToAIContent_sets_RawRepresentation_and_copies_metadata()
    {
        var part = new TextPart("x") { Metadata = new() { ["k"] = Json("\"v\""), ["n"] = Json("5") } };
        var content = part.ToAIContent();
        Assert.Same(part, content.RawRepresentation);
        Assert.NotNull(content.AdditionalProperties);
        Assert.Equal("v", ((JsonElement)content.AdditionalProperties!["k"]!).GetString());
        Assert.Equal(5, ((JsonElement)content.AdditionalProperties["n"]!).GetInt32());
    }

    // ---- AIContent -> A2A ----

    [Fact]
    public void TextContent_becomes_TextPart()
    {
        var part = Assert.IsType<TextPart>(new TextContent("hi").ToPart());
        Assert.Equal("hi", part.Text);
    }

    [Fact]
    public void DataContent_non_json_becomes_RawPart_with_name_as_filename()
    {
        var part = Assert.IsType<RawPart>(new DataContent(new byte[] { 5, 6 }, "image/png") { Name = "p.png" }.ToPart());
        Assert.Equal("image/png", part.MediaType);
        Assert.Equal("p.png", part.Filename);
        Assert.Equal(new byte[] { 5, 6 }, part.Raw.ToArray());
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/vnd.acme+json")]
    public void DataContent_json_becomes_DataPart(string mediaType)
    {
        var part = Assert.IsType<DataPart>(new DataContent(Encoding.UTF8.GetBytes("""{"a":[1,2]}"""), mediaType).ToPart());
        Assert.Equal(mediaType, part.MediaType);
        Assert.Equal(2, part.Data.GetProperty("a").GetArrayLength());
    }

    [Fact]
    public void DataContent_json_that_does_not_parse_becomes_RawPart()
    {
        var bytes = Encoding.UTF8.GetBytes("{ this is not json");
        var part = Assert.IsType<RawPart>(new DataContent(bytes, "application/json").ToPart());
        Assert.Equal(bytes, part.Raw.ToArray());
        Assert.Equal("application/json", part.MediaType);
    }

    [Fact]
    public void UriContent_becomes_UrlPart()
    {
        var part = Assert.IsType<UrlPart>(new UriContent(new Uri("https://example.com/f"), "text/csv").ToPart());
        Assert.Equal(new Uri("https://example.com/f"), part.Url);
        Assert.Equal("text/csv", part.MediaType);
    }

    [Fact]
    public void ToPart_always_rebuilds_from_content_never_from_RawRepresentation()
    {
        var original = Part.FromText("old");
        var content = Assert.IsType<TextContent>(original.ToAIContent());
        content.Text = "new";
        var part = Assert.IsType<TextPart>(content.ToPart());
        Assert.NotSame(original, part);
        Assert.Equal("new", part.Text);
    }

    [Fact]
    public void TextPart_media_type_and_filename_are_lost_through_TextContent()
    {
        var back = Assert.IsType<TextPart>(new TextPart("x") { MediaType = "text/markdown", Filename = "r.md" }.ToAIContent().ToPart());
        Assert.Null(back.MediaType);
        Assert.Null(back.Filename);
    }

    [Fact]
    public void DataPart_round_trip_keeps_meaning()
    {
        var back = Assert.IsType<DataPart>(Part.FromData(Json("""{ "a" : 1,   "b":[true] }""")).ToAIContent().ToPart());
        Assert.Equal(1, back.Data.GetProperty("a").GetInt32());
        Assert.True(back.Data.GetProperty("b")[0].GetBoolean());
    }

    public static TheoryData<AIContent> ContentWithoutPart() => new()
    {
        new FunctionCallContent("1", "f"),
        new FunctionResultContent("1", "r"),
        new TextReasoningContent("thinking"),
        new ErrorContent("bad"),
        new UsageContent(new UsageDetails { InputTokenCount = 1 }),
        new HostedFileContent("file-1"),
        new AIContent(),
    };

    [Theory]
    [MemberData(nameof(ContentWithoutPart))]
    public void Content_without_an_A2A_form_has_no_part(AIContent content) => Assert.Null(content.ToPart());

    [Fact]
    public void Message_conversion_drops_content_without_an_A2A_form()
    {
        var message = new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("keep"),
            new FunctionCallContent("1", "f"),
            new TextReasoningContent("secret thoughts"),
            new ErrorContent("e"),
            new UsageContent(new UsageDetails()),
        ]);
        var a2a = message.ToA2AMessage();
        var part = Assert.Single(a2a.Parts);
        Assert.Equal("keep", Assert.IsType<TextPart>(part).Text);
        var artifact = message.ToA2AArtifact();
        Assert.Single(artifact.Parts);
    }

    // ---- metadata ----

    [Fact]
    public void AdditionalProperties_become_metadata_for_json_string_bool_and_numbers_only()
    {
        var content = new TextContent("x")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["json"] = Json("""{"z":1}"""),
                ["str"] = "s",
                ["bool"] = true,
                ["int"] = 3,
                ["long"] = 4L,
                ["double"] = 1.5,
                ["decimal"] = 2.5m,
                ["obj"] = new object(),
                ["list"] = new[] { 1, 2 },
                ["null"] = null,
                ["nan"] = double.NaN,
            },
        };
        var part = content.ToPart()!;
        var md = part.Metadata!;
        Assert.Equal(1, md["json"].GetProperty("z").GetInt32());
        Assert.Equal("s", md["str"].GetString());
        Assert.True(md["bool"].GetBoolean());
        Assert.Equal(3, md["int"].GetInt32());
        Assert.Equal(4L, md["long"].GetInt64());
        Assert.Equal(1.5, md["double"].GetDouble());
        Assert.Equal(2.5m, md["decimal"].GetDecimal());
        Assert.False(md.ContainsKey("obj"));
        Assert.False(md.ContainsKey("list"));
        Assert.False(md.ContainsKey("null"));
        Assert.False(md.ContainsKey("nan"));
    }

    [Fact]
    public void Message_metadata_is_copied_both_ways()
    {
        var message = new Message { MessageId = "m1", Role = Role.User, Parts = [Part.FromText("x")], Metadata = new() { ["k"] = Json("\"v\"") } };
        var chat = message.ToChatMessage();
        Assert.Equal("v", ((JsonElement)chat.AdditionalProperties!["k"]!).GetString());
        var back = chat.ToA2AMessage();
        Assert.Equal("v", back.Metadata!["k"].GetString());
    }

    // ---- roles and messages ----

    [Fact]
    public void Roles_map_agent_to_assistant_and_user_to_user()
    {
        var agent = new Message { MessageId = "a", Role = Role.Agent, Parts = [Part.FromText("x")] }.ToChatMessage();
        var user = new Message { MessageId = "u", Role = Role.User, Parts = [Part.FromText("x")] }.ToChatMessage();
        Assert.Equal(ChatRole.Assistant, agent.Role);
        Assert.Equal(ChatRole.User, user.Role);
        Assert.Equal("a", agent.MessageId);
    }

    [Fact]
    public void ToA2AMessage_maps_roles_and_keeps_or_creates_message_id()
    {
        Assert.Equal(Role.Agent, new ChatMessage(ChatRole.Assistant, "x").ToA2AMessage().Role);
        Assert.Equal(Role.User, new ChatMessage(ChatRole.User, "x").ToA2AMessage().Role);
        Assert.Equal(Role.User, new ChatMessage(ChatRole.System, "x").ToA2AMessage().Role);
        Assert.Equal(Role.User, new ChatMessage(ChatRole.Tool, "x").ToA2AMessage().Role);
        Assert.Equal("keep", new ChatMessage(ChatRole.User, "x") { MessageId = "keep" }.ToA2AMessage().MessageId);
        var generated = new ChatMessage(ChatRole.User, "x").ToA2AMessage().MessageId;
        Assert.False(string.IsNullOrEmpty(generated));
        Assert.NotEqual(generated, new ChatMessage(ChatRole.User, "x").ToA2AMessage().MessageId);
    }

    // ---- artifacts ----

    [Fact]
    public void Artifact_becomes_assistant_message_with_artifact_id()
    {
        var artifact = new Artifact { ArtifactId = "art-1", Name = "report", Parts = [Part.FromText("body")] };
        var chat = artifact.ToChatMessage();
        Assert.Equal(ChatRole.Assistant, chat.Role);
        Assert.Equal("art-1", chat.MessageId);
        Assert.Equal("body", chat.Text);
    }

    [Fact]
    public void ToA2AArtifact_uses_given_id_and_name_or_defaults()
    {
        var message = new ChatMessage(ChatRole.Assistant, "body");
        var given = message.ToA2AArtifact("id-1", "name-1");
        Assert.Equal("id-1", given.ArtifactId);
        Assert.Equal("name-1", given.Name);
        var defaulted = message.ToA2AArtifact();
        Assert.True(Guid.TryParse(defaulted.ArtifactId, out _));
        Assert.Null(defaulted.Name);
        Assert.Equal("body", Assert.IsType<TextPart>(Assert.Single(defaulted.Parts)).Text);
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => ((Part)null!).ToAIContent());
        Assert.Throws<ArgumentNullException>(() => ((AIContent)null!).ToPart());
        Assert.Throws<ArgumentNullException>(() => ((Message)null!).ToChatMessage());
        Assert.Throws<ArgumentNullException>(() => ((ChatMessage)null!).ToA2AMessage());
        Assert.Throws<ArgumentNullException>(() => ((Artifact)null!).ToChatMessage());
        Assert.Throws<ArgumentNullException>(() => ((ChatMessage)null!).ToA2AArtifact());
    }
}
