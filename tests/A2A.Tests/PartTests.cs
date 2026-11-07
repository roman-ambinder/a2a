using System.Net;
using System.Text;
using System.Text.Json;
using A2A.Json;

namespace A2A.Tests;

public class PartTests
{
    [Fact]
    public void Each_part_kind_round_trips()
    {
        var data = JsonSerializer.SerializeToElement(new { a = 1 });
        var meta = new Dictionary<string, JsonElement> { ["k"] = JsonSerializer.SerializeToElement("v") };
        Part[] parts =
        [
            Part.FromText("hi") with { Metadata = meta },
            Part.FromBytes(new byte[] { 1, 2, 3 }, "application/octet-stream", "a.bin"),
            Part.FromUrl(new Uri("https://x/y.png"), "image/png"),
            Part.FromData(data),
        ];
        var json = A2AJson.Serialize(parts);
        Assert.Contains("\"raw\":\"AQID\"", json);
        Assert.Contains("\"url\":\"https://x/y.png\"", json);
        Assert.Contains("\"metadata\":{\"k\":\"v\"}", json);
        Assert.Contains("\"data\":{\"a\":1}", json);

        var back = A2AJson.Deserialize<List<Part>>(json);
        Assert.Equal("hi", Assert.IsType<TextPart>(back[0]).Text);
        Assert.Equal("v", back[0].Metadata!["k"].GetString());
        var raw = Assert.IsType<RawPart>(back[1]);
        Assert.Equal(new byte[] { 1, 2, 3 }, raw.Raw.ToArray());
        Assert.Equal("a.bin", raw.Filename);
        Assert.Equal("application/octet-stream", raw.MediaType);
        Assert.Equal(new Uri("https://x/y.png"), Assert.IsType<UrlPart>(back[2]).Url);
        Assert.Equal(1, Assert.IsType<DataPart>(back[3]).Data.GetProperty("a").GetInt32());
    }

    [Fact]
    public void Part_with_two_contents_is_rejected()
    {
        var ex = Assert.Throws<JsonException>(() => A2AJson.Deserialize<Part>("""{"text":"a","url":"https://x"}"""));
        Assert.Contains("found: text, url", ex.Message);
    }

    [Fact]
    public void Part_with_no_content_is_rejected()
    {
        var ex = Assert.Throws<JsonException>(() => A2AJson.Deserialize<Part>("""{"mediaType":"text/plain"}"""));
        Assert.Contains("found none", ex.Message);
    }

    [Theory]
    [MemberData(nameof(LimitsTests.Bindings), MemberType = typeof(LimitsTests))]
    public async Task Server_rejects_multi_content_part(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        const string json = """{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"a","url":"https://x"}]}}""";
        var rpc = binding == ProtocolBindings.JsonRpc;
        using var req = new HttpRequestMessage(HttpMethod.Post, rpc ? "/a2a" : "/a2a/message:send")
        {
            Content = new StringContent(
                rpc ? $$"""{"jsonrpc":"2.0","id":1,"method":"{{A2AProtocol.Methods.SendMessage}}","params":{{json}}}""" : json,
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        req.Headers.Add("X-Test-User", "alice");
        using var client = host.Http();
        using var resp = await client.SendAsync(req);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        if (rpc)
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal((int)A2AErrorCode.InvalidParams, body.GetProperty("error").GetProperty("code").GetInt32());
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        Assert.Contains("found: text, url", body.GetProperty("error").GetRawText());
    }

    [Fact]
    public void Derived_typed_serialization_matches_part_typed_wire_format()
    {
        var meta = new Dictionary<string, JsonElement> { ["k"] = JsonSerializer.SerializeToElement(1) };
        var text = Part.FromText("hi", "text/plain") with { Metadata = meta };
        var raw = Part.FromBytes(new byte[] { 1, 2, 3 }, "application/octet-stream", "a.bin");
        var url = Part.FromUrl(new Uri("https://x/y.png"), "image/png");
        var data = Part.FromData(JsonSerializer.SerializeToElement(new { a = 1 }));

        Assert.Equal(A2AJson.Serialize<Part>(text), A2AJson.Serialize(text));
        Assert.Equal(A2AJson.Serialize<Part>(raw), A2AJson.Serialize(raw));
        Assert.Equal(A2AJson.Serialize<Part>(url), A2AJson.Serialize(url));
        Assert.Equal(A2AJson.Serialize<Part>(data), A2AJson.Serialize(data));
        Assert.Contains("\"raw\":\"AQID\"", A2AJson.Serialize(raw));
        Assert.Contains("\"url\":\"https://x/y.png\"", A2AJson.Serialize(url));

        Assert.Equal(new byte[] { 1, 2, 3 }, A2AJson.Deserialize<RawPart>("""{"raw":"AQID"}""").Raw.ToArray());
        Assert.Equal("hi", A2AJson.Deserialize<TextPart>("""{"text":"hi"}""").Text);
        Assert.Equal(new Uri("https://x"), A2AJson.Deserialize<UrlPart>("""{"url":"https://x"}""").Url);
        Assert.Equal(1, A2AJson.Deserialize<DataPart>("""{"data":{"a":1}}""").Data.GetProperty("a").GetInt32());
        Assert.Throws<JsonException>(() => A2AJson.Deserialize<TextPart>("""{"url":"https://x"}"""));
    }
}
