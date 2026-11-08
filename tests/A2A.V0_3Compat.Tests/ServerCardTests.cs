using System.Net;
using System.Text.Json.Nodes;
using A2A.Json;
using A2A.Signing;

namespace A2A.V0_3Compat.Tests;

/// <summary>The one blended Agent Card at the well-known path.</summary>
public class ServerCardTests
{
    private const string CardPath = "/.well-known/agent-card.json";

    private static async Task<(HttpResponseMessage Response, string Text)> GetCardAsync(V03Host host, string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CardPath);
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        var response = await host.Http(null).SendAsync(request);
        return (response, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Card_is_the_v1_card_plus_the_v03_members()
    {
        await using var host = await V03Host.StartAsync();
        var (response, text) = await GetCardAsync(host);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var card = JsonNode.Parse(text)!;

        Assert.Equal("0.3.0", card["protocolVersion"]!.GetValue<string>());
        Assert.Equal(V03Host.BaseUrl, card["url"]!.GetValue<string>());
        Assert.Equal("JSONRPC", card["preferredTransport"]!.GetValue<string>());
        Assert.Equal(["JSONRPC", "HTTP+JSON"], card["additionalInterfaces"]!.AsArray().Select(i => i!["transport"]!.GetValue<string>()));
        Assert.Equal("[{\"bearer\":[]}]", card["security"]!.ToJsonString());
        Assert.True(card["supportsAuthenticatedExtendedCard"]!.GetValue<bool>());
        Assert.NotNull(card["supportedInterfaces"]); // the v1.0 members are still there
        var bearer = card["securitySchemes"]!["bearer"]!;
        Assert.NotNull(bearer["httpAuthSecurityScheme"]);
        Assert.Equal("http", bearer["type"]!.GetValue<string>());
        // v1.0 push value is kept (it is shared by both versions)
        Assert.True(card["capabilities"]!["pushNotifications"]!.GetValue<bool>());

        var model = A2AJson.Deserialize<AgentCard>(text); // a v1.0 client reads it
        Assert.Equal("Scenario Agent", model.Name);
        Assert.IsType<HttpAuthScheme>(model.SecuritySchemes!["bearer"]);
    }

    [Fact]
    public async Task Without_compat_the_card_is_unchanged()
    {
        await using var host = await V03Host.StartAsync(compat: false);
        var (_, text) = await GetCardAsync(host);
        var card = JsonNode.Parse(text)!;
        Assert.Null(card["protocolVersion"]);
        Assert.Null(card["url"]);
        Assert.Null(card["securitySchemes"]!["bearer"]!["type"]);
    }

    [Fact]
    public async Task The_etag_covers_the_blended_bytes_and_conditional_requests_work()
    {
        await using var host = await V03Host.StartAsync();
        var (first, text) = await GetCardAsync(host);
        var etag = first.Headers.ETag!.Tag;
        var (again, againText) = await GetCardAsync(host);
        Assert.Equal(etag, again.Headers.ETag!.Tag);
        Assert.Equal(text, againText);
        var (notModified, _) = await GetCardAsync(host, etag);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);

        await using var plain = await V03Host.StartAsync(compat: false);
        var (plainResponse, _) = await GetCardAsync(plain);
        Assert.NotEqual(etag, plainResponse.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task A_signed_blended_card_still_verifies_as_v1()
    {
        var key = V03Host.NewKey();
        await using var host = await V03Host.StartAsync(configureHttp: o => o.SigningKey = key);
        var (_, text) = await GetCardAsync(host);
        var card = JsonNode.Parse(text)!.AsObject();
        Assert.NotNull(card["signatures"]);
        Assert.NotNull(card["protocolVersion"]);

        var policy = new CardTrustPolicy();
        policy.TrustedKeys["http://localhost"] = new JwkSet { Keys = [key.ToPublicJwk()] };
        var result = await new AgentCardVerifier(policy).VerifyAsync(card, new Uri("http://localhost" + CardPath));
        Assert.Equal(AgentCardVerificationStatus.Verified, result.Status);
    }

    [Fact]
    public async Task Tenant_cards_are_blended_too()
    {
        await using var host = await V03Host.StartAsync();
        using var response = await host.Http(null).GetAsync("/acme" + CardPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(JsonNode.Parse(await response.Content.ReadAsStringAsync())!["protocolVersion"]);
    }
}
