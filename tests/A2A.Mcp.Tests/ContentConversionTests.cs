using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace A2A.Mcp.Tests;

public sealed class ContentConversionTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Blocks_become_parts()
    {
        var text = Assert.IsType<TextPart>(new TextContentBlock { Text = "hi" }.ToPart());
        Assert.Equal("hi", text.Text);

        var image = Assert.IsType<RawPart>(ImageContentBlock.FromBytes(new byte[] { 1, 2, 3 }, "image/png").ToPart());
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, image.Raw.ToArray());

        var audio = Assert.IsType<RawPart>(AudioContentBlock.FromBytes(new byte[] { 9 }, "audio/wav").ToPart());
        Assert.Equal("audio/wav", audio.MediaType);

        var embeddedText = Assert.IsType<TextPart>(new EmbeddedResourceBlock { Resource = new TextResourceContents { Uri = "x://1", Text = "# md", MimeType = "text/markdown" } }.ToPart());
        Assert.Equal("# md", embeddedText.Text);
        Assert.Equal("text/markdown", embeddedText.MediaType);

        var blob = Assert.IsType<RawPart>(new EmbeddedResourceBlock { Resource = BlobResourceContents.FromBytes(new byte[] { 5, 6 }, "x://2", "application/pdf") }.ToPart());
        Assert.Equal("application/pdf", blob.MediaType);
        Assert.Equal(new byte[] { 5, 6 }, blob.Raw.ToArray());

        var link = Assert.IsType<UrlPart>(new ResourceLinkBlock { Uri = "https://example.com/a.txt", Name = "a.txt", MimeType = "text/plain" }.ToPart());
        Assert.Equal(new Uri("https://example.com/a.txt"), link.Url);
        Assert.Equal("a.txt", link.Filename);
        Assert.Equal("text/plain", link.MediaType);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/plain,hi")]
    [InlineData("not a uri")]
    [InlineData("/relative")]
    public void Links_that_are_not_web_links_are_dropped(string uri)
    {
        Assert.Null(new ResourceLinkBlock { Uri = uri, Name = "x" }.ToPart());
        Assert.Null(Part.FromUrl(new Uri(uri, UriKind.RelativeOrAbsolute)).ToContentBlock());
    }

    [Fact]
    public void Tool_use_and_tool_result_blocks_are_dropped()
    {
        // These block types are marked obsolete by the MCP SDK (sampling is deprecated). The bridge must still drop them.
#pragma warning disable MCP9005
        Assert.Null(new ToolUseContentBlock { Id = "1", Name = "t", Input = Json("{}") }.ToPart());
        Assert.Null(new ToolResultContentBlock { ToolUseId = "1", Content = [] }.ToPart());
#pragma warning restore MCP9005
    }

    [Fact]
    public void Bad_media_types_become_octet_stream()
    {
        var part = Assert.IsType<RawPart>(ImageContentBlock.FromBytes(new byte[] { 1 }, "no slash").ToPart());
        Assert.Equal("application/octet-stream", part.MediaType);
        var block = Assert.IsType<EmbeddedResourceBlock>(Part.FromBytes(new byte[] { 1 }, "bad type!!").ToContentBlock());
        Assert.Equal("application/octet-stream", block.Resource.MimeType);
    }

    [Fact]
    public void Parts_become_blocks()
    {
        Assert.Equal("hi", Assert.IsType<TextContentBlock>(Part.FromText("hi").ToContentBlock()).Text);
        Assert.Equal("""{"a":1}""", Assert.IsType<TextContentBlock>(Part.FromData(Json("""{"a":1}""")).ToContentBlock()).Text);
        Assert.IsType<ImageContentBlock>(Part.FromBytes(new byte[] { 1 }, "image/jpeg").ToContentBlock());
        Assert.IsType<AudioContentBlock>(Part.FromBytes(new byte[] { 1 }, "audio/mpeg").ToContentBlock());
        Assert.IsType<EmbeddedResourceBlock>(Part.FromBytes(new byte[] { 1 }, "application/zip").ToContentBlock());
        var link = Assert.IsType<ResourceLinkBlock>(Part.FromUrl(new Uri("https://example.com/f"), "text/csv", "f.csv").ToContentBlock());
        Assert.Equal("f.csv", link.Name);
    }

    [Fact]
    public void Result_with_structured_content_gives_one_data_part_and_drops_the_duplicate_text()
    {
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = """{ "b": [1, 2.0], "a": "x" }""" }, new TextContentBlock { Text = "extra" }],
            StructuredContent = Json("""{"a":"x","b":[1,2]}"""),
        };
        var parts = result.ToParts();
        Assert.Equal(2, parts.Count);
        Assert.IsType<DataPart>(parts[0]);
        Assert.Equal("extra", Assert.IsType<TextPart>(parts[1]).Text);
    }

    [Fact]
    public void Text_that_differs_from_the_structured_content_is_kept()
    {
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = """{"a":2}""" }],
            StructuredContent = Json("""{"a":1}"""),
        };
        var parts = result.ToParts();
        Assert.Equal(2, parts.Count);
        Assert.IsType<TextPart>(parts[0]);
        Assert.IsType<DataPart>(parts[1]);
    }

    [Fact]
    public void Result_without_content_gives_no_parts()
    {
        Assert.Empty(new CallToolResult { Content = [] }.ToParts());
        Assert.Empty(new CallToolResult().ToParts());
    }
}
