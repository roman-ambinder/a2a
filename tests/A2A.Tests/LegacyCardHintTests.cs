using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using A2A.Client;

namespace A2A.Tests;

/// <summary>
/// The plain client never reads a card older than 1.0 as a 1.0 card: it refuses it with an error that names the v0.3
/// compat factory, instead of "no supported interface" or a confusing parse error.
/// </summary>
public sealed class LegacyCardHintTests
{
    private sealed class CardHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private static JsonObject V03Card() => new()
    {
        ["protocolVersion"] = "0.3.0", ["name"] = "Old Agent", ["description"] = "d", ["version"] = "1",
        ["url"] = "http://localhost/a2a", ["preferredTransport"] = "JSONRPC",
        ["capabilities"] = new JsonObject(), ["defaultInputModes"] = new JsonArray("text/plain"),
        ["defaultOutputModes"] = new JsonArray("text/plain"), ["skills"] = new JsonArray(),
    };

    private static Task<A2AClient> CreateAsync(JsonObject card) =>
        A2AClientFactory.CreateAsync(new Uri("http://localhost/"), new HttpClient(new CardHandler(card.ToJsonString())),
            new A2AClientOptions { EndpointPolicy = AgentEndpointPolicies.AllowAll });

    [Theory]
    [InlineData("0.3.0")]
    [InlineData("0.2.6")]
    [InlineData(null)] // before v0.2.5: no protocolVersion, but a top-level url
    public async Task A_card_older_than_1_0_is_refused_with_a_pointer_to_the_compat_factory(string? version)
    {
        var card = V03Card();
        if (version is null) card.Remove("protocolVersion");
        else card["protocolVersion"] = version;

        var ex = await Assert.ThrowsAsync<A2AException>(() => CreateAsync(card));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
        Assert.Contains("V03CompatClientFactory", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RomanAmbinder.A2A.Client.V0_3Compat", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blended_card_with_supportedInterfaces_is_read_as_1_0()
    {
        var card = V03Card();
        card["supportedInterfaces"] = new JsonArray(new JsonObject
        {
            ["url"] = "http://localhost/a2a", ["protocolBinding"] = "JSONRPC", ["protocolVersion"] = "1.0",
        });
        var client = await CreateAsync(card);
        Assert.Equal("Old Agent", client.AgentCard!.Name);
    }

    [Theory]
    [InlineData("""{"name":"n","description":"d"}""", null)]
    [InlineData("""{"supportedInterfaces":[{"url":"http://x/a2a","protocolBinding":"JSONRPC","protocolVersion":"1.0"}],"url":"http://x"}""", null)]
    [InlineData("""{"protocolVersion":"1.0"}""", null)]
    [InlineData("""{"protocolVersion":"0.3"}""", "0.3")]
    [InlineData("""{"url":"http://x/a2a"}""", "0.2 (no protocolVersion)")]
    public void Legacy_card_detection(string json, string? expected)
    {
        Assert.Equal(expected, A2ACardResolver.LegacyCardVersion(JsonNode.Parse(json)!.AsObject()));
    }
}
