using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using A2A.Json;
using A2A.Signing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using static A2A.Tests.Cli.CliTestSupport;

namespace A2A.Tests.Cli;

public sealed class CliTrustAndAuthTests
{
    private static AgentCardSigningKey NewKey(string kid) => new(ECDsa.Create(ECCurve.NamedCurves.nistP256), kid, "ES256");

    /// <summary>Serves the signed card unchanged, so the server cannot alter what was signed.</summary>
    private static Action<WebApplication> ServeCard(AgentCard card) => app =>
    {
        var json = A2AJson.Serialize(card);
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path != "/.well-known/agent-card.json") { await next(ctx); return; }
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(json);
        });
    };

    private static async Task<string> WriteJwksAsync(TempDir dir, params AgentCardSigningKey[] keys)
    {
        var path = Path.Combine(dir.Path, "keys.json");
        await File.WriteAllTextAsync(path, A2AJson.Serialize(new JwkSet { Keys = keys.Select(k => k.ToPublicJwk()).ToList() }));
        return path;
    }

    [Fact]
    public async Task Trusted_key_verifies_the_card_in_text_and_json()
    {
        using var dir = new TempDir();
        var key = NewKey("test-key-1");
        var jwks = await WriteJwksAsync(dir, key);
        await using var host = await StartAsync(pipeline: ServeCard(AgentCardSigner.Sign(TestAgentHost.DefaultCard(), key)));

        var text = await Run(host, "card", "--trust-jwks", jwks);
        Assert.True(text.Code == 0, text.Shown);
        Assert.Contains("Verification: Verified, key id test-key-1", text.Out);
        Assert.Contains("assurance Verified", text.Out);

        var json = await Run(host, "card", "--trust-jwks", jwks, "--json");
        Assert.True(json.Code == 0, json.Shown);
        using var doc = JsonDocument.Parse(json.Out);
        var verification = doc.RootElement.GetProperty("verification");
        Assert.Equal("verified", verification.GetProperty("status").GetString());
        Assert.Equal("test-key-1", verification.GetProperty("keyId").GetString());
    }

    [Fact]
    public async Task A_different_trusted_key_exits_7()
    {
        using var dir = new TempDir();
        var jwks = await WriteJwksAsync(dir, NewKey("test-key-1"));
        var other = NewKey("test-key-1"); // same key id, other key material
        await using var host = await StartAsync(pipeline: ServeCard(AgentCardSigner.Sign(TestAgentHost.DefaultCard(), other)));

        var run = await Run(host, "card", "--trust-jwks", jwks);
        Assert.Equal(7, run.Code);
        Assert.Equal("", run.Out);
    }

    [Fact]
    public async Task Trust_jwks_with_a_missing_or_malformed_file_exits_2()
    {
        using var dir = new TempDir();
        await using var host = await StartAsync();
        var missing = await Run(host, "card", "--trust-jwks", Path.Combine(dir.Path, "nope.json"));
        Assert.Equal(2, missing.Code);

        var bad = Path.Combine(dir.Path, "bad.json");
        await File.WriteAllTextAsync(bad, "{ not json");
        Assert.Equal(2, (await Run(host, "card", "--trust-jwks", bad)).Code);
    }

    private sealed class TokenEndpoint : HttpMessageHandler
    {
        public List<(string Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests) Requests.Add((request.Headers.Authorization?.ToString() ?? "", body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"issued-token-789","token_type":"Bearer","expires_in":3600}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task OAuth2_client_credentials_get_a_token_and_the_secret_stays_out_of_the_output()
    {
        const string Secret = "client-secret-xyz";
        var card = SecuredCard(new OAuth2Scheme(new OAuth2SecurityScheme
        {
            Flows = new OAuthFlows { ClientCredentials = new ClientCredentialsOAuthFlow { TokenUrl = "http://localhost/token" } },
        }));
        var seen = new List<(string, string?, string?)>();
        await using var host = await StartAsync(card, requireAuth: true, pipeline: app =>
            app.Use((ctx, next) =>
            {
                var auth = ctx.Request.Headers.Authorization.ToString();
                lock (seen) seen.Add((ctx.Request.Path, auth, null));
                if (ctx.Request.Path.StartsWithSegments("/a2a") && auth == "Bearer issued-token-789") ctx.Request.Headers["X-Test-User"] = "cli";
                return next(ctx);
            }));
        var tokens = new TokenEndpoint();
        var env = new Dictionary<string, string?> { ["A2A_CLIENT_SECRET"] = Secret };

        var run = await RunAsync(host, ["send", "hello", "--client-id", "client-1"], env, oauthHandler: tokens);

        Assert.True(run.Code == 0, run.Shown);
        lock (seen) Assert.Contains(seen, s => s.Item1.StartsWith("/a2a", StringComparison.Ordinal) && s.Item2 == "Bearer issued-token-789");
        lock (tokens.Requests) Assert.NotEmpty(tokens.Requests);
        Assert.DoesNotContain(Secret, run.All);
        Assert.DoesNotContain("issued-token-789", run.All);
    }

    [Fact]
    public async Task Allow_http_never_lets_a_credential_go_to_a_non_loopback_http_agent()
    {
        var card = SecuredCard(SecurityScheme.Bearer());
        foreach (var itf in card.SupportedInterfaces) itf.Url = "http://agent.example/a2a";
        var seen = new List<(string, string?, string?)>();
        await using var host = await StartAsync(card, pipeline: app =>
            app.Use((ctx, next) =>
            {
                lock (seen) seen.Add((ctx.Request.Path, ctx.Request.Headers.Authorization.ToString(), null));
                return next(ctx);
            }));

        var run = await RunAsync(host, ["send", "hello", "--agent", "http://agent.example/", "--allow-http", "--token", "tok-plain"], addAgent: false);

        Assert.NotEqual(0, run.Code);
        Assert.DoesNotContain("tok-plain", run.All);
        lock (seen)
        {
            Assert.NotEmpty(seen); // the card fetch happened
            Assert.All(seen, s => Assert.Equal("", s.Item2));
            Assert.DoesNotContain(seen, s => s.Item1.StartsWith("/a2a", StringComparison.Ordinal));
        }
    }
}
