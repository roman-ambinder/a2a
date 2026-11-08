using A2A.Client;
using A2A.Client.Credentials;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace A2A.Tests.Credentials;

/// <summary>Real sockets: a credentialed client never sends its credential to another origin, even when the agent redirects.</summary>
public class CredentialRedirectTests
{
    private const string Secret = "secret-api-key";
    private const string CardPath = "/.well-known/agent-card.json";

    private static (Dictionary<string, SecurityScheme>, List<SecurityRequirement>) ApiKey() =>
        (new() { ["key"] = SecurityScheme.ApiKey("X-Key") }, [Cards.Req(("key", []))]);

    private static (Dictionary<string, SecurityScheme>, List<SecurityRequirement>) OAuth(Uri origin) =>
        (new()
        {
            ["oauth"] = new OAuth2Scheme(new OAuth2SecurityScheme
            {
                Flows = new OAuthFlows { ClientCredentials = new ClientCredentialsOAuthFlow { TokenUrl = new Uri(origin, "/token").ToString() } },
            }),
        }, [Cards.Req(("oauth", []))]);

    private static Task<TestOrigin> Target() =>
        TestOrigin.StartAsync((context, _) => { context.Response.StatusCode = 200; return Task.CompletedTask; });

    /// <summary>Origin A: serves the card, issues tokens, and answers /a2a with a 307 to origin B.</summary>
    private static Task<TestOrigin> Redirector(Func<Uri, (Dictionary<string, SecurityScheme>, List<SecurityRequirement>)> scheme,
        Func<TestOrigin> b, Func<Uri, Uri?>? interfaceOrigin = null) =>
        TestOrigin.StartAsync(async (context, a) =>
        {
            switch (context.Request.Path.Value)
            {
                case CardPath:
                    var (schemes, requirements) = scheme(a.Url);
                    await TestOrigin.WriteCard(context, TestOrigin.Card(a.Url, schemes, requirements, interfaceOrigin?.Invoke(a.Url)));
                    break;
                case "/token":
                    await TestOrigin.WriteJson(context, """{"access_token":"oauth-tok","token_type":"Bearer","expires_in":3600}""");
                    break;
                default:
                    context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                    context.Response.Headers.Location = new Uri(b().Url, "/stolen").ToString();
                    break;
            }
        });

    private static async Task AssertNothingLeaked(TestOrigin a, TestOrigin b, string headerName, string expected)
    {
        await Task.CompletedTask;
        Assert.Null(a.At(CardPath).Single().Header(headerName));
        Assert.Equal(expected, a.At("/a2a").Single().Header(headerName));
        Assert.Empty(b.Requests);
    }

    private static A2AClientOptions NoRetry() => new() { MaxRetries = 0 };

    [Fact]
    public async Task ApiKey_no_DI_does_not_follow_a_307_to_another_origin()
    {
        await using var b = await Target();
        await using var a = await Redirector(_ => ApiKey(), () => b);

        var client = await A2AClientFactory.CreateAsync(a.Url, [new ApiKeyCredentialProvider(Secret)], options: NoRetry());
        await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("hi"));

        await AssertNothingLeaked(a, b, "X-Key", Secret);
    }

    [Fact]
    public async Task ApiKey_DI_does_not_follow_a_307_to_another_origin()
    {
        await using var b = await Target();
        await using var a = await Redirector(_ => ApiKey(), () => b);
        var services = new ServiceCollection();
        services.AddA2AClient("agent", o => { o.AgentUrl = a.Url; o.Client.MaxRetries = 0; }).AddApiKey(Secret);
        using var sp = services.BuildServiceProvider();

        var client = await sp.GetRequiredService<IA2AClientFactory>().GetClientAsync("agent");
        await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("hi"));

        await AssertNothingLeaked(a, b, "X-Key", Secret);
    }

    [Fact]
    public async Task OAuth_no_DI_does_not_follow_a_307_to_another_origin()
    {
        await using var b = await Target();
        await using var a = await Redirector(OAuth, () => b);
        using var provider = new OAuth2ClientCredentialsProvider(new OAuth2ClientCredentialsOptions { ClientId = "id", ClientSecret = "s" });

        var client = await A2AClientFactory.CreateAsync(a.Url, [provider], options: NoRetry());
        await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("hi"));

        await AssertNothingLeaked(a, b, "Authorization", "Bearer oauth-tok");
    }

    [Fact]
    public async Task OAuth_DI_does_not_follow_a_307_to_another_origin()
    {
        await using var b = await Target();
        await using var a = await Redirector(OAuth, () => b);
        var services = new ServiceCollection();
        services.AddA2AClient("agent", o => { o.AgentUrl = a.Url; o.Client.MaxRetries = 0; })
            .AddOAuth2ClientCredentials(o => { o.ClientId = "id"; o.ClientSecret = "s"; });
        using var sp = services.BuildServiceProvider();

        var client = await sp.GetRequiredService<IA2AClientFactory>().GetClientAsync("agent");
        await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("hi"));

        await AssertNothingLeaked(a, b, "Authorization", "Bearer oauth-tok");
    }

    [Fact]
    public async Task Card_whose_interface_is_on_another_origin_fails_creation_and_B_gets_no_request()
    {
        await using var b = await Target();
        await using var a = await Redirector(_ => ApiKey(), () => b, _ => b.Url);

        var noDi = await Assert.ThrowsAsync<A2AException>(() =>
            A2AClientFactory.CreateAsync(a.Url, [new ApiKeyCredentialProvider(Secret)], options: NoRetry()));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, noDi.Code);

        var services = new ServiceCollection();
        services.AddA2AClient("agent", o => { o.AgentUrl = a.Url; o.Client.MaxRetries = 0; }).AddApiKey(Secret);
        using var sp = services.BuildServiceProvider();
        var di = await Assert.ThrowsAsync<A2AException>(() => sp.GetRequiredService<IA2AClientFactory>().GetClientAsync("agent").AsTask());
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, di.Code);

        Assert.Empty(b.Requests);
        Assert.Empty(a.At("/a2a"));
    }
}
