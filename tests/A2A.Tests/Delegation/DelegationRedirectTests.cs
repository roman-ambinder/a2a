using System.Net;
using A2A.Client;
using A2A.Client.Delegation;
using A2A.Delegation;
using A2A.Tests.Credentials;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace A2A.Tests;

/// <summary>Real sockets: the delegation helpers never send a secret or token to another origin, even when the server redirects.</summary>
public class DelegationRedirectTests
{
    private const string Secret = "client-secret-value";
    private const string SubjectToken = "subject-token-value";
    private const string ActorToken = "actor-token-value";

    private static Task<TestOrigin> Target() =>
        TestOrigin.StartAsync((context, _) => { context.Response.StatusCode = 200; return Task.CompletedTask; });

    private static Task<TestOrigin> Redirector(int status, Func<TestOrigin> to) =>
        TestOrigin.StartAsync((context, _) =>
        {
            context.Response.StatusCode = status;
            context.Response.Headers.Location = new Uri(to().Url, "/stolen").ToString();
            return Task.CompletedTask;
        });

    private static OAuthTokenExchangerOptions Options(Uri endpoint, Func<HttpRequestMessage, CancellationToken, ValueTask>? configure = null) =>
        new() { TokenEndpoint = endpoint, ClientId = "agent", ClientSecret = Secret, ConfigureRequest = configure };

    private static TokenExchangeRequest Request() => new()
    {
        SubjectToken = SubjectToken,
        ActorToken = ActorToken,
        ActorTokenType = TokenTypes.Jwt,
    };

    private static void AssertNoSecretAt(TestOrigin origin)
    {
        Assert.Empty(origin.Requests);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Default_exchanger_does_not_follow_a_redirect(int status)
    {
        await using var b = await Target();
        await using var a = await Redirector(status, () => b);
        var exchanger = new OAuthTokenExchanger(Options(a.Uri("/token")));

        var ex = await Assert.ThrowsAsync<TokenExchangeException>(async () =>
            await exchanger.ExchangeAsync(Request(), CancellationToken.None));

        Assert.Single(a.Requests);
        AssertNoSecretAt(b);
        Assert.DoesNotContain(SubjectToken, ex.Message);
        Assert.DoesNotContain(Secret, ex.Message);
    }

    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Exchanger_with_a_non_following_handler_does_not_follow_a_redirect(int status)
    {
        await using var b = await Target();
        await using var a = await Redirector(status, () => b);
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        var exchanger = new OAuthTokenExchanger(handler, Options(a.Uri("/token")));

        await Assert.ThrowsAsync<TokenExchangeException>(async () =>
            await exchanger.ExchangeAsync(Request(), CancellationToken.None));

        AssertNoSecretAt(b);
    }

    [Fact]
    public async Task A_following_handler_would_repost_the_form_so_it_is_refused_up_front()
    {
        await using var b = await Target();
        await using var a = await Redirector(307, () => b);
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = true };

        // Control: this is the leak the check prevents. A plain HttpClient re-posts the body on a 307.
        using (var control = new HttpClient(handler, disposeHandler: false))
        {
            using var content = new FormUrlEncodedContent([new("subject_token", SubjectToken)]);
            using var _ = await control.PostAsync(a.Uri("/token"), content);
        }
        Assert.Contains(SubjectToken, Assert.Single(b.Requests).Body);
        b.Requests.Clear();

        var ex = Assert.Throws<ArgumentException>(() => new OAuthTokenExchanger(handler, Options(a.Uri("/token"))));
        Assert.Equal("handler", ex.ParamName);
        AssertNoSecretAt(b);
    }

    [Fact]
    public void Refuses_a_following_handler_under_a_delegating_handler()
    {
        using var inner = new HttpClientHandler { AllowAutoRedirect = true };
        using var outer = new FakeDelegating(inner);
        Assert.Throws<ArgumentException>(() => new OAuthTokenExchanger(outer, Options(new Uri("https://auth.example.com/token"))));
    }

    [Fact]
    public async Task Redirect_to_another_path_on_the_same_origin_is_refused_too()
    {
        await using var a = await TestOrigin.StartAsync((context, origin) =>
        {
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            context.Response.Headers.Location = "/other";
            return Task.CompletedTask;
        });
        var exchanger = new OAuthTokenExchanger(Options(a.Uri("/token")));

        await Assert.ThrowsAsync<TokenExchangeException>(async () =>
            await exchanger.ExchangeAsync(Request(), CancellationToken.None));

        Assert.Single(a.Requests);
    }

    [Fact]
    public async Task Exchanger_sends_the_form_to_the_endpoint_only()
    {
        await using var a = await TestOrigin.StartAsync(async (context, _) =>
            await TestOrigin.WriteJson(context, """{"access_token":"new","token_type":"Bearer"}"""));
        var exchanger = new OAuthTokenExchanger(Options(a.Uri("/token")));

        var result = await exchanger.ExchangeAsync(Request(), CancellationToken.None);

        Assert.Equal("new", result.AccessToken);
        var seen = Assert.Single(a.Requests);
        Assert.Contains(SubjectToken, seen.Body);
        Assert.StartsWith("Basic ", seen.Header("Authorization"));
    }

    [Fact]
    public async Task ConfigureRequest_cannot_point_the_secret_at_another_uri()
    {
        await using var b = await Target();
        await using var a = await Target();
        var exchanger = new OAuthTokenExchanger(Options(a.Uri("/token"), (request, _) =>
        {
            request.RequestUri = b.Uri("/stolen");
            return ValueTask.CompletedTask;
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await exchanger.ExchangeAsync(Request(), CancellationToken.None));

        AssertNoSecretAt(a);
        AssertNoSecretAt(b);
    }

    private sealed class FakeDelegating(HttpMessageHandler inner) : DelegatingHandler(inner);

    // ---- UseBearerToken ----

    private static readonly TokenExchangeResult Token = new() { AccessToken = "exchanged-token", TokenType = "Bearer" };

    private static (Dictionary<string, SecurityScheme>, List<SecurityRequirement>) NoSchemes() => ([], []);

    private static A2AClient ClientFor(Uri url, HttpMessageHandler handler)
    {
        var options = new A2AClientOptions { MaxRetries = 0 };
        var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        return new A2AClient(new A2A.Client.Transports.JsonRpcTransport(http, url, options), options);
    }

    private static async Task TryCallAsync(A2AClient client, A2ARequestOptions options)
    {
        try { await client.SendMessageAsync(Message.FromUser("hi"), options: options); }
        catch (A2AException) { /* the target answers 307 or 200 with no JSON-RPC body: only what it saw matters */ }
        catch (HttpRequestException) { }
    }

    [Fact]
    public async Task UseBearerToken_sends_the_token_to_the_bound_origin()
    {
        await using var a = await Target();
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        var client = ClientFor(a.Uri("/a2a"), handler);

        await TryCallAsync(client, new A2ARequestOptions().UseBearerToken(Token, a.Uri("/")));

        Assert.Equal("Bearer exchanged-token", Assert.Single(a.Requests).Header("Authorization"));
    }

    [Fact]
    public async Task UseBearerToken_refuses_a_client_on_another_origin_before_sending()
    {
        await using var a = await Target();
        await using var other = await Target();
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        var client = ClientFor(other.Uri("/a2a"), handler);

        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            client.SendMessageAsync(Message.FromUser("hi"), options: new A2ARequestOptions().UseBearerToken(Token, a.Uri("/"))));

        Assert.DoesNotContain("exchanged-token", ex.Message);
        AssertNoSecretAt(a);
        AssertNoSecretAt(other);
    }

    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public async Task UseBearerToken_does_not_cross_a_redirect_with_a_non_following_client(int status)
    {
        await using var b = await Target();
        await using var a = await Redirector(status, () => b);
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        var client = ClientFor(a.Uri("/a2a"), handler);

        await TryCallAsync(client, new A2ARequestOptions().UseBearerToken(Token, a.Uri("/")));

        Assert.Equal("Bearer exchanged-token", Assert.Single(a.Requests).Header("Authorization"));
        AssertNoSecretAt(b);
    }

    [Fact]
    public async Task UseBearerToken_through_the_factory_does_not_cross_a_redirect()
    {
        await using var b = await Target();
        await using var a = await TestOrigin.StartAsync(async (context, origin) =>
        {
            if (context.Request.Path.Value == "/.well-known/agent-card.json")
            {
                var (schemes, requirements) = NoSchemes();
                await TestOrigin.WriteCard(context, TestOrigin.Card(origin.Url, schemes, requirements));
                return;
            }
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            context.Response.Headers.Location = new Uri(b.Url, "/stolen").ToString();
        });
        var client = await A2AClientFactory.CreateAsync(a.Url, [new NoopProvider()], options: new A2AClientOptions { MaxRetries = 0 });

        await TryCallAsync(client, new A2ARequestOptions().UseBearerToken(Token, a.Url));

        AssertNoSecretAt(b);
    }

    [Fact]
    public async Task UseBearerToken_keeps_an_existing_ConfigureRequest()
    {
        var calls = new List<string>();
        var options = new A2ARequestOptions { ConfigureRequest = (_, _) => { calls.Add("first"); return ValueTask.CompletedTask; } };
        options.UseBearerToken(Token, new Uri("https://agent.example.com"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://agent.example.com/a2a");

        await options.ConfigureRequest!(request, CancellationToken.None);

        Assert.Equal(["first"], calls);
    }

    [Theory]
    [InlineData("https://agent.example.com/other", true)]
    [InlineData("https://agent.example.com:443/a2a", true)]
    [InlineData("https://AGENT.example.com/a2a", true)]
    [InlineData("http://agent.example.com/a2a", false)]
    [InlineData("https://agent.example.com:8443/a2a", false)]
    [InlineData("https://evil.example.com/a2a", false)]
    [InlineData("https://agent.example.com.evil.test/a2a", false)]
    public async Task UseBearerToken_compares_the_origin(string requestUrl, bool allowed)
    {
        var options = new A2ARequestOptions().UseBearerToken(Token, new Uri("https://agent.example.com/a2a"));
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);

        if (allowed)
        {
            await options.ConfigureRequest!(request, CancellationToken.None);
        }
        else
        {
            await Assert.ThrowsAsync<A2AException>(async () => await options.ConfigureRequest!(request, CancellationToken.None));
        }
    }

    [Theory]
    [InlineData("/relative")]
    [InlineData("ftp://agent.example.com/")]
    public void UseBearerToken_needs_an_absolute_http_url(string url)
    {
        var agentUrl = new Uri(url, UriKind.RelativeOrAbsolute);
        Assert.Throws<ArgumentException>(() => new A2ARequestOptions().UseBearerToken(Token, agentUrl));
    }

    [Theory]
    [InlineData("abc def")]
    [InlineData("abc\r\nX-Evil: 1")]
    public void UseBearerToken_refuses_a_token_that_does_not_fit_a_header_and_does_not_quote_it(string accessToken)
    {
        var token = new TokenExchangeResult { AccessToken = accessToken, TokenType = "Bearer" };
        var ex = Assert.Throws<InvalidOperationException>(() => new A2ARequestOptions().UseBearerToken(token, new Uri("https://agent.example.com")));
        Assert.DoesNotContain("abc", ex.Message);
    }

    private sealed class NoopProvider : A2A.Client.Credentials.IA2ACredentialProvider
    {
        public bool CanHandle(A2A.Client.Credentials.A2ACredentialContext context) => false;

        public ValueTask ApplyAsync(HttpRequestMessage request, A2A.Client.Credentials.A2ACredentialContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
