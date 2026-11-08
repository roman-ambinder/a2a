using System.Net;
using System.Text;
using A2A.Client;
using A2A.Client.Delegation;
using A2A.Delegation;

namespace A2A.Tests;

public class OAuthTokenExchangerTests
{
    private const string Endpoint = "https://auth.example.com/token";
    private const string SubjectToken = "subject-token-value";
    private static readonly Uri Agent = new("https://agent.example.com/a2a");

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static OAuthTokenExchanger Create(FakeHandler handler, OAuthTokenExchangerOptions? options = null) =>
        new(handler, options ?? new OAuthTokenExchangerOptions { TokenEndpoint = new Uri(Endpoint) });

    private static Dictionary<string, string> Form(string? body) =>
        (body ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    private const string OkBody = "{\"access_token\":\"new\",\"token_type\":\"Bearer\"}";

    [Fact]
    public async Task Posts_the_rfc8693_form()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, OkBody));
        var exchanger = Create(handler);

        await exchanger.ExchangeAsync(new TokenExchangeRequest
        {
            SubjectToken = SubjectToken,
            Audience = "agent-b",
            Resource = "https://b.example.com",
            Scopes = ["read", "write"],
            RequestedTokenType = TokenTypes.Jwt,
            ActorToken = "actor-token",
            ActorTokenType = TokenTypes.Jwt,
        }, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal(new Uri(Endpoint), handler.Request.RequestUri);
        Assert.Equal("application/x-www-form-urlencoded", handler.Request.Content!.Headers.ContentType!.MediaType);
        var form = Form(handler.Body);
        Assert.Equal("urn:ietf:params:oauth:grant-type:token-exchange", form["grant_type"]);
        Assert.Equal(SubjectToken, form["subject_token"]);
        Assert.Equal(TokenTypes.AccessToken, form["subject_token_type"]);
        Assert.Equal("agent-b", form["audience"]);
        Assert.Equal("https://b.example.com", form["resource"]);
        Assert.Equal("read write", form["scope"]);
        Assert.Equal(TokenTypes.Jwt, form["requested_token_type"]);
        Assert.Equal("actor-token", form["actor_token"]);
        Assert.Equal(TokenTypes.Jwt, form["actor_token_type"]);
    }

    [Fact]
    public async Task Absent_optionals_are_not_sent()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, OkBody));
        await Create(handler).ExchangeAsync(new TokenExchangeRequest { SubjectToken = SubjectToken }, CancellationToken.None);

        var form = Form(handler.Body);
        Assert.Equal(["grant_type", "subject_token", "subject_token_type"], form.Keys.Order().ToArray());
    }

    [Fact]
    public async Task Reads_the_token_response()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK,
            "{\"access_token\":\"new\",\"issued_token_type\":\"urn:ietf:params:oauth:token-type:access_token\",\"token_type\":\"Bearer\",\"expires_in\":300,\"scope\":\"a b\"}"));

        var result = await Create(handler).ExchangeAsync(new TokenExchangeRequest { SubjectToken = SubjectToken }, CancellationToken.None);

        Assert.Equal("new", result.AccessToken);
        Assert.Equal(TokenTypes.AccessToken, result.IssuedTokenType);
        Assert.Equal("Bearer", result.TokenType);
        Assert.Equal(TimeSpan.FromSeconds(300), result.ExpiresIn);
        Assert.Equal("a b", result.Scope);
    }

    [Fact]
    public async Task Expires_in_that_is_not_a_number_is_null()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{\"access_token\":\"new\",\"token_type\":\"Bearer\",\"expires_in\":\"soon\"}"));
        var result = await Create(handler).ExchangeAsync(new TokenExchangeRequest { SubjectToken = SubjectToken }, CancellationToken.None);
        Assert.Null(result.ExpiresIn);
    }

    [Fact]
    public async Task Missing_token_type_throws()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{\"access_token\":\"new\"}"));
        await Assert.ThrowsAsync<TokenExchangeException>(async () =>
            await Create(handler).ExchangeAsync(new TokenExchangeRequest { SubjectToken = SubjectToken }, CancellationToken.None));
    }

    [Fact]
    public async Task Sends_client_credentials_as_basic_auth()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, OkBody));
        var exchanger = Create(handler, new OAuthTokenExchangerOptions
        {
            TokenEndpoint = new Uri(Endpoint),
            ClientId = "my client",
            ClientSecret = "s:e+cret",
        });

        await exchanger.ExchangeAsync(new TokenExchangeRequest { SubjectToken = SubjectToken }, CancellationToken.None);

        var auth = handler.Request!.Headers.Authorization!;
        Assert.Equal("Basic", auth.Scheme);
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("my+client:s%3Ae%2Bcret"));
        Assert.Equal(expected, auth.Parameter);
    }

    [Fact]
    public async Task ConfigureRequest_runs_last()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, OkBody));
        var exchanger = Create(handler, new OAuthTokenExchangerOptions
        {
            TokenEndpoint = new Uri(Endpoint),
            ClientId = "id",
            ClientSecret = "secret",
            ConfigureRequest = (req, _) =>
            {
                req.Headers.Authorization = new("Custom", "x");
                return ValueTask.CompletedTask;
            },
        });

        await exchanger.ExchangeAsync(new TokenExchangeRequest { SubjectToken = SubjectToken }, CancellationToken.None);

        Assert.Equal("Custom", handler.Request!.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task Error_response_throws_with_only_the_error_code()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.BadRequest,
            "{\"error\":\"invalid_target\",\"error_description\":\"secret detail\"}"));

        var ex = await Assert.ThrowsAsync<TokenExchangeException>(async () =>
            await Create(handler).ExchangeAsync(new TokenExchangeRequest { SubjectToken = SubjectToken }, CancellationToken.None));

        Assert.Equal("invalid_target", ex.Error);
        Assert.DoesNotContain("secret detail", ex.Message);
        Assert.DoesNotContain(SubjectToken, ex.Message);
    }

    [Fact]
    public async Task Error_code_with_bad_characters_is_dropped()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.BadRequest, "{\"error\":\"bad \\\"code\\\"\"}"));
        var ex = await Assert.ThrowsAsync<TokenExchangeException>(async () =>
            await Create(handler).ExchangeAsync(new TokenExchangeRequest { SubjectToken = SubjectToken }, CancellationToken.None));
        Assert.Null(ex.Error);
    }

    [Fact]
    public async Task Error_code_is_truncated_to_128_characters()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.BadRequest, "{\"error\":\"" + new string('a', 300) + "\"}"));
        var ex = await Assert.ThrowsAsync<TokenExchangeException>(async () =>
            await Create(handler).ExchangeAsync(new TokenExchangeRequest { SubjectToken = SubjectToken }, CancellationToken.None));
        Assert.Equal(128, ex.Error!.Length);
    }

    [Fact]
    public async Task Malformed_json_throws_without_the_body()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{not json " + SubjectToken));
        var ex = await Assert.ThrowsAsync<TokenExchangeException>(async () =>
            await Create(handler).ExchangeAsync(new TokenExchangeRequest { SubjectToken = SubjectToken }, CancellationToken.None));
        Assert.DoesNotContain(SubjectToken, ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void Refuses_http_endpoint()
    {
        var options = new OAuthTokenExchangerOptions { TokenEndpoint = new Uri("http://example.com/token") };
        var ex = Assert.Throws<ArgumentException>(() => new OAuthTokenExchanger(options));
        Assert.Equal("options", ex.ParamName);
    }

    [Fact]
    public void Allows_loopback_http()
    {
        var options = new OAuthTokenExchangerOptions { TokenEndpoint = new Uri("http://localhost:8080/token") };
        _ = new OAuthTokenExchanger(options);
        options = new OAuthTokenExchangerOptions { TokenEndpoint = new Uri("http://127.0.0.1/token") };
        _ = new OAuthTokenExchanger(options);
    }

    [Fact]
    public async Task Refuses_a_redirected_response()
    {
        var handler = new FakeHandler(req =>
        {
            var response = Json(HttpStatusCode.OK, OkBody);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://evil.example.com/token");
            return response;
        });

        await Assert.ThrowsAsync<TokenExchangeException>(async () =>
            await Create(handler).ExchangeAsync(new TokenExchangeRequest { SubjectToken = SubjectToken }, CancellationToken.None));
    }

    [Fact]
    public async Task Refuses_an_oversized_response()
    {
        var big = "{\"access_token\":\"" + new string('a', 70 * 1024) + "\",\"token_type\":\"Bearer\"}";
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, big));

        await Assert.ThrowsAsync<TokenExchangeException>(async () =>
            await Create(handler).ExchangeAsync(new TokenExchangeRequest { SubjectToken = SubjectToken }, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_access_token_throws()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{\"token_type\":\"Bearer\"}"));
        await Assert.ThrowsAsync<TokenExchangeException>(async () =>
            await Create(handler).ExchangeAsync(new TokenExchangeRequest { SubjectToken = SubjectToken }, CancellationToken.None));
    }

    [Fact]
    public void ToString_hides_tokens()
    {
        var request = new TokenExchangeRequest { SubjectToken = SubjectToken, ActorToken = "actor-secret" };
        var result = new TokenExchangeResult { AccessToken = "access-secret" };

        Assert.DoesNotContain(SubjectToken, request.ToString());
        Assert.DoesNotContain("actor-secret", request.ToString());
        Assert.DoesNotContain("access-secret", result.ToString());
    }

    [Fact]
    public void UseBearerToken_sets_the_header()
    {
        var options = new A2ARequestOptions().UseBearerToken(new TokenExchangeResult { AccessToken = "abc" }, Agent);
        Assert.Equal("Bearer abc", options.Headers["Authorization"]);
    }

    [Fact]
    public void UseBearerToken_refuses_other_token_types()
    {
        var token = new TokenExchangeResult { AccessToken = "abc", TokenType = "DPoP" };
        Assert.Throws<InvalidOperationException>(() => new A2ARequestOptions().UseBearerToken(token, Agent));
    }

    [Fact]
    public void UseBearerToken_ignores_case_of_the_token_type()
    {
        var options = new A2ARequestOptions().UseBearerToken(new TokenExchangeResult { AccessToken = "abc", TokenType = "bearer" }, Agent);
        Assert.Equal("Bearer abc", options.Headers["Authorization"]);
    }
}
