using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using A2A.Client;
using A2A.Client.Identity.Aws;
using A2A.Client.Identity.Azure;
using Amazon.Runtime;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests.Credentials;

/// <summary>The DI helpers of the AWS and Azure identity packages, end to end through IA2AClientFactory.</summary>
public class IdentityDiTests
{
    private const string AccessKey = "AKIDEXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";

    private sealed class FakeCredential : TokenCredential
    {
        public List<string[]> Scopes { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            lock (Scopes) Scopes.Add(requestContext.Scopes);
            return new AccessToken("tok1", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private static Task<TestOrigin> Serve(Dictionary<string, SecurityScheme> schemes, List<SecurityRequirement> requirements) =>
        TestOrigin.StartAsync(async (context, origin) =>
        {
            if (context.Request.Path == "/.well-known/agent-card.json")
                await TestOrigin.WriteCard(context, TestOrigin.Card(origin.Url, schemes, requirements));
            else
                await TestOrigin.WriteJson(context, TestOrigin.RpcOk);
        });

    private static async Task<A2AClient> Get(ServiceProvider sp) =>
        await sp.GetRequiredService<IA2AClientFactory>().GetClientAsync("agent");

    private static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Rebuilds the SigV4 signature from what the server received.</summary>
    private static void AssertSigned(TestOrigin origin, SeenRequest seen, string method)
    {
        var date = seen.Header("X-Amz-Date")!;
        var headers = new List<KeyValuePair<string, string>>
        {
            new("host", seen.Header("Host")!), new("x-amz-date", date), new("x-amz-content-sha256", Sha256Hex(seen.Body)),
        };
        if (seen.Header("Content-Type") is { } contentType && method == "POST") headers.Add(new("content-type", contentType));
        var when = DateTimeOffset.ParseExact(date, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var expected = SigV4.Authorization(method, new Uri(origin.Url, seen.Path), headers, Sha256Hex(seen.Body),
            AccessKey, SecretKey, "us-east-1", "execute-api", when);
        Assert.Equal(expected, seen.Header("Authorization"));
    }

    [Fact]
    public async Task AddAwsSigV4_signs_the_card_fetch_and_the_call_to_AgentUrl()
    {
        await using var origin = await Serve([], []);
        var services = new ServiceCollection();
        services.AddA2AClient("agent", o => { o.AgentUrl = origin.Url; o.Client.MaxRetries = 0; })
            .AddAwsSigV4(o =>
            {
                o.Region = "us-east-1";
                o.Service = "execute-api";
                o.Credentials = new BasicAWSCredentials(AccessKey, SecretKey); // AgentOrigin comes from AgentUrl
            });
        await using var sp = services.BuildServiceProvider();
        await (await Get(sp)).SendMessageAsync("hello");

        AssertSigned(origin, Assert.Single(origin.At("/.well-known/agent-card.json")), "GET");
        AssertSigned(origin, Assert.Single(origin.At("/a2a")), "POST");
    }

    [Fact]
    public async Task AddAwsSigV4_can_take_credentials_from_the_service_provider()
    {
        await using var origin = await Serve([], []);
        var services = new ServiceCollection();
        services.AddSingleton<AWSCredentials>(new BasicAWSCredentials(AccessKey, SecretKey));
        services.AddA2AClient("agent", o => o.AgentUrl = origin.Url)
            .AddAwsSigV4((provider, o) =>
            {
                o.Region = "us-east-1";
                o.Service = "execute-api";
                o.Credentials = provider.GetRequiredService<AWSCredentials>();
            });
        await using var sp = services.BuildServiceProvider();
        await (await Get(sp)).SendMessageAsync("hello");
        AssertSigned(origin, Assert.Single(origin.At("/a2a")), "POST");
    }

    [Fact]
    public void AddAwsSigV4_needs_an_A2A_client_builder()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddHttpClient("plain").AddAwsSigV4(o => { o.Region = "us-east-1"; o.Service = "execute-api"; }));
    }

    [Fact]
    public void AddAwsSigV4_without_AgentUrl_or_AgentOrigin_fails_when_the_handler_is_built()
    {
        var services = new ServiceCollection();
        var builder = services.AddA2AClient("agent", _ => { })
            .AddAwsSigV4(o => { o.Region = "us-east-1"; o.Service = "execute-api"; });
        using var sp = services.BuildServiceProvider();
        var ex = Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<IHttpClientFactory>().CreateClient(builder.Name));
        Assert.Contains("AgentUrl", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAzureTokenCredential_sends_the_token_and_asks_for_the_configured_scope()
    {
        await using var origin = await Serve(new() { ["entra"] = SecurityScheme.Bearer() }, [Cards.Req(("entra", ["agent.invoke"]))]);
        var credential = new FakeCredential();
        var services = new ServiceCollection();
        services.AddA2AClient("agent", o => { o.AgentUrl = origin.Url; o.Client.MaxRetries = 0; })
            .AddAzureTokenCredential(_ => credential, o => o.Scopes.Add("api://my-agent/.default"));
        await using var sp = services.BuildServiceProvider();
        await (await Get(sp)).SendMessageAsync("hello");

        Assert.Null(Assert.Single(origin.At("/.well-known/agent-card.json")).Header("Authorization")); // the card fetch carries none
        Assert.Equal("Bearer tok1", Assert.Single(origin.At("/a2a")).Header("Authorization"));
        Assert.Equal(["api://my-agent/.default"], Assert.Single(credential.Scopes));
    }

    [Fact]
    public async Task AddAzureTokenCredential_uses_the_registered_TokenCredential()
    {
        await using var origin = await Serve(new() { ["entra"] = SecurityScheme.Bearer() }, [Cards.Req(("entra", []))]);
        var credential = new FakeCredential();
        var services = new ServiceCollection();
        services.AddSingleton<TokenCredential>(credential);
        services.AddA2AClient("agent", o => o.AgentUrl = origin.Url)
            .AddAzureTokenCredential(o => o.Scopes.Add("api://my-agent/.default"));
        await using var sp = services.BuildServiceProvider();
        await (await Get(sp)).SendMessageAsync("hello");
        Assert.Equal("Bearer tok1", Assert.Single(origin.At("/a2a")).Header("Authorization"));
    }

    [Fact]
    public void AddAzureTokenCredential_needs_an_A2A_client_builder()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddHttpClient("plain").AddAzureTokenCredential(o => o.Scopes.Add("api://x/.default")));
    }

    [Fact]
    public void GetA2AClientName_returns_the_A2A_name_or_null()
    {
        var services = new ServiceCollection();
        Assert.Equal("agent", services.AddA2AClient("agent", _ => { }).GetA2AClientName());
        Assert.Null(services.AddHttpClient("plain").GetA2AClientName());
    }
}
