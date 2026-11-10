using System.Collections.Concurrent;
using System.Net;
using System.Text;
using A2A.Client.Credentials;

namespace A2A.Tests.Credentials;

public class OAuth2ClientCredentialsProviderTests
{
    private static readonly Uri Origin = new("https://agent.example/");
    private const string CardTokenUrl = "https://agent.example/oauth/token";

    private sealed record Seen(HttpMethod Method, Uri Uri, string? Authorization, string Body, string? ContentType, string? Accept);

    private sealed class TokenHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ConcurrentQueue<Seen> Requests { get; } = new();
        public TaskCompletionSource Entered { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Gate { get; set; }
        public Func<int, HttpResponseMessage> Respond { get; set; } = n => Json(HttpStatusCode.OK, $$"""{"access_token":"tok{{n}}","token_type":"Bearer","expires_in":3600}""");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _calls);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(new Seen(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body,
                request.Content?.Headers.ContentType?.MediaType, request.Headers.Accept.ToString()));
            var gate = Gate; // read before signalling Entered: a test may null Gate right after Entered completes
            Entered.TrySetResult();
            if (gate is not null) await gate.Task.WaitAsync(cancellationToken);
            return Respond(n);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static A2ACredentialContext OAuth(string tokenUrl = CardTokenUrl, string[]? scopes = null, string name = "oauth", Uri? origin = null) =>
        new(name, new OAuth2Scheme(new OAuth2SecurityScheme
        {
            Flows = new OAuthFlows { ClientCredentials = new ClientCredentialsOAuthFlow { TokenUrl = tokenUrl } },
        }), scopes ?? [], origin ?? Origin);

    private static (OAuth2ClientCredentialsProvider Provider, TokenHandler Handler, FakeTimeProvider Time) Make(Action<OAuth2ClientCredentialsOptions>? configure = null)
    {
        var handler = new TokenHandler();
        var time = new FakeTimeProvider();
        var options = new OAuth2ClientCredentialsOptions
        {
            ClientId = "id", ClientSecret = "secret", TokenHandler = handler, TimeProvider = time,
        };
        configure?.Invoke(options);
        return (new OAuth2ClientCredentialsProvider(options), handler, time);
    }

    private static async Task<string?> Apply(OAuth2ClientCredentialsProvider p, A2ACredentialContext? ctx = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://agent.example/a2a");
        await p.ApplyAsync(request, ctx ?? OAuth(), ct);
        return request.Headers.Authorization?.ToString();
    }

    private static async Task<A2AException> Fails(OAuth2ClientCredentialsProvider p, A2ACredentialContext? ctx = null)
    {
        var ex = await Assert.ThrowsAsync<A2AException>(() => Apply(p, ctx));
        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        return ex;
    }

    [Fact]
    public async Task Two_applies_make_one_fetch_and_use_the_same_token()
    {
        var (p, h, _) = Make();
        Assert.Equal("Bearer tok1", await Apply(p));
        Assert.Equal("Bearer tok1", await Apply(p));
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Authorization_set_earlier_is_replaced()
    {
        var (p, _, _) = Make();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://agent.example/a2a");
        request.Headers.TryAddWithoutValidation("Authorization", "Basic old");
        await p.ApplyAsync(request, OAuth(), default);
        Assert.Equal("Bearer tok1", request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Token_is_refreshed_at_RefreshAt_and_not_before()
    {
        var (p, h, time) = Make(); // 3600 s lifetime, 60 s skew: refresh at 3540 s
        Assert.Equal("Bearer tok1", await Apply(p));
        time.Advance(TimeSpan.FromSeconds(3539));
        Assert.Equal("Bearer tok1", await Apply(p));
        Assert.Equal(1, h.Calls);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("Bearer tok2", await Apply(p));
        Assert.Equal(2, h.Calls);
    }

    private static Task<string?> Start(OAuth2ClientCredentialsProvider p, CancellationToken ct = default) => Apply(p, null, ct);

    [Fact]
    public async Task Fifty_callers_join_one_gated_fetch()
    {
        var (p, h, _) = Make();
        h.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Start(p);
        await h.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Each call runs synchronously through the lock: it has joined (or started) a fetch when it returns.
        var joined = Enumerable.Range(0, 50).Select(_ => Start(p)).ToList();
        Assert.All(joined, t => Assert.False(t.IsCompleted));
        Assert.False(first.IsCompleted);
        h.Gate.SetResult();
        var results = await Task.WhenAll(joined.Append(first)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, r => Assert.Equal("Bearer tok1", r));
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Parallel_callers_make_one_fetch()
    {
        var (p, h, _) = Make();
        h.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 50).Select(_ => Task.Run(() => Apply(p))).ToArray();
        await h.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Gate.SetResult();
        var results = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, r => Assert.Equal("Bearer tok1", r));
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Cancelling_the_caller_that_started_the_fetch_does_not_cancel_it_for_others()
    {
        var (p, h, _) = Make();
        h.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var creator = Start(p, cts.Token);
        await h.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var other = Start(p);
        Assert.False(other.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creator);
        Assert.False(other.IsCompleted);
        h.Gate.SetResult();
        Assert.Equal("Bearer tok1", await other.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Evicted_slot_is_not_used_and_in_flight_slot_survives_eviction()
    {
        var (p, h, _) = Make();
        var key = ("https://agent.example/oauth/token", Origin.AbsoluteUri, "");
        await Apply(p);
        var old = p.GetSlot(key);
        p.EvictIdleSlots();
        Assert.True(old.Evicted);
        Assert.Equal(0, p.SlotCount);
        await Apply(p); // must start a fetch on a fresh slot, not reuse the evicted one
        var fresh = p.GetSlot(key);
        Assert.NotSame(old, fresh);
        Assert.NotNull(fresh.Token);
        Assert.Equal(2, h.Calls);

        // An in-flight slot is left alone by eviction scans.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Gate = gate;
        h.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var inflight = Apply(p, OAuth(scopes: ["busy"]));
        await h.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Gate = null;
        for (var i = 0; i < 300; i++) await Apply(p, OAuth(scopes: [$"s{i}"]));
        Assert.True(p.SlotCount <= 257);
        var joiner = Apply(p, OAuth(scopes: ["busy"]));
        Assert.False(joiner.IsCompleted);
        gate.SetResult();
        await inflight.WaitAsync(TimeSpan.FromSeconds(10));
        await joiner.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3 + 300, h.Calls);
    }

    [Fact]
    public async Task Failed_refresh_with_valid_token_returns_old_token_then_retries()
    {
        var (p, h, time) = Make();
        h.Respond = n => n == 1
            ? Json(HttpStatusCode.OK, """{"access_token":"old","expires_in":100}""")
            : n == 2 ? Json(HttpStatusCode.InternalServerError, "{}")
            : Json(HttpStatusCode.OK, """{"access_token":"new","expires_in":100}""");
        Assert.Equal("Bearer old", await Apply(p));
        time.Advance(TimeSpan.FromSeconds(60)); // past RefreshAt (50 s), before ExpiresAt (100 s)
        Assert.Equal("Bearer old", await Apply(p));
        Assert.Equal(2, h.Calls);
        Assert.Equal("Bearer new", await Apply(p));
        Assert.Equal(3, h.Calls);
    }

    [Fact]
    public async Task Failed_first_fetch_and_failed_refresh_after_expiry_throw()
    {
        var (p, h, time) = Make();
        h.Respond = _ => Json(HttpStatusCode.ServiceUnavailable, "{}");
        await Fails(p);

        var (p2, h2, time2) = Make();
        h2.Respond = n => n == 1 ? Json(HttpStatusCode.OK, """{"access_token":"old","expires_in":100}""") : Json(HttpStatusCode.BadGateway, "{}");
        Assert.Equal("Bearer old", await Apply(p2));
        time2.Advance(TimeSpan.FromSeconds(100));
        await Fails(p2);
    }

    [Fact]
    public async Task Refresh_skew_is_capped_at_half_the_lifetime()
    {
        var (p, h, time) = Make();
        h.Respond = n => Json(HttpStatusCode.OK, $$"""{"access_token":"t{{n}}","expires_in":10}""");
        await Apply(p);
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal("Bearer t1", await Apply(p));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("Bearer t2", await Apply(p));
    }

    [Fact]
    public async Task Missing_expires_in_uses_DefaultTokenLifetime()
    {
        var (p, h, time) = Make();
        h.Respond = n => Json(HttpStatusCode.OK, $$"""{"access_token":"t{{n}}"}""");
        await Apply(p);
        time.Advance(TimeSpan.FromSeconds(239)); // 300 s lifetime, 60 s skew
        Assert.Equal("Bearer t1", await Apply(p));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("Bearer t2", await Apply(p));
    }

    [Fact]
    public async Task String_expires_in_is_accepted()
    {
        var (p, h, time) = Make();
        h.Respond = n => Json(HttpStatusCode.OK, $$"""{"access_token":"t{{n}}","expires_in":"100"}""");
        await Apply(p);
        time.Advance(TimeSpan.FromSeconds(49));
        Assert.Equal("Bearer t1", await Apply(p));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("Bearer t2", await Apply(p));
    }

    [Fact]
    public async Task Cache_entries_are_per_scope_set_and_card_origin_and_scope_order_does_not_matter()
    {
        var (p, h, _) = Make();
        await Apply(p, OAuth(scopes: ["b", "a"]));
        await Apply(p, OAuth(scopes: ["a", "b", "a"]));
        Assert.Equal(1, h.Calls);
        Assert.Contains("scope=a+b", h.Requests.Last().Body);
        await Apply(p, OAuth(scopes: ["a"]));
        Assert.Equal(2, h.Calls);

        var (q, hq, _) = Make(o => o.TokenEndpoint = new Uri("https://auth.example/token"));
        await Apply(q, OAuth(origin: new Uri("https://one.example/")));
        await Apply(q, OAuth(origin: new Uri("https://two.example/")));
        await Apply(q, OAuth(origin: new Uri("https://one.example/")));
        Assert.Equal(2, hq.Calls);
    }

    [Fact]
    public async Task Option_scopes_beat_the_requirement_scopes()
    {
        var (p, h, _) = Make(o => o.Scopes = ["x"]);
        await Apply(p, OAuth(scopes: ["ignored"]));
        Assert.Contains("scope=x", h.Requests.Single().Body);
        Assert.DoesNotContain("ignored", h.Requests.Single().Body);
    }

    [Fact]
    public async Task No_scope_parameter_when_there_are_no_scopes()
    {
        var (p, h, _) = Make();
        await Apply(p);
        Assert.DoesNotContain("scope", h.Requests.Single().Body);
    }

    [Fact]
    public async Task Basic_wire_format_url_encodes_id_and_secret()
    {
        var (p, h, _) = Make(o => { o.ClientId = "my id"; o.ClientSecret = "s:e/c+ret"; o.AdditionalParameters["audience"] = "api://x"; });
        await Apply(p, OAuth(scopes: ["s1"]));
        var seen = h.Requests.Single();
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("my+id:s%3Ae%2Fc%2Bret")), seen.Authorization);
        Assert.Equal("application/x-www-form-urlencoded", seen.ContentType);
        Assert.Equal("application/json", seen.Accept);
        Assert.Equal("grant_type=client_credentials&scope=s1&audience=api%3A%2F%2Fx", seen.Body);
    }

    [Fact]
    public async Task Post_wire_format_puts_credentials_in_the_body()
    {
        var (p, h, _) = Make(o => { o.ClientAuthentication = OAuth2ClientAuthentication.ClientSecretPost; o.ClientId = "my id"; o.ClientSecret = "s&c"; });
        await Apply(p);
        var seen = h.Requests.Single();
        Assert.Null(seen.Authorization);
        Assert.Equal("grant_type=client_credentials&client_id=my+id&client_secret=s%26c", seen.Body);
    }

    [Fact]
    public async Task Configured_endpoint_beats_card_token_url_and_secret_goes_only_there()
    {
        var (p, h, _) = Make(o => o.TokenEndpoint = new Uri("https://auth.example/token"));
        await Apply(p, OAuth("https://evil.example/steal"));
        Assert.Equal("https://auth.example/token", h.Requests.Single().Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Cross_origin_card_token_url_without_TokenEndpoint_is_refused_at_apply()
    {
        var (p, h, _) = Make();
        await Fails(p, OAuth("https://evil.example/steal"));
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public void CanHandle_matrix()
    {
        var (card, _, _) = Make();
        var (withEndpoint, _, _) = Make(o => o.TokenEndpoint = new Uri("https://auth.example/token"));
        var bearer = new A2ACredentialContext("b", SecurityScheme.Bearer(), [], Origin);
        var bearerLower = new A2ACredentialContext("b", new HttpAuthScheme(new HttpAuthSecurityScheme { Scheme = "bearer" }), [], Origin);
        var basic = new A2ACredentialContext("b", new HttpAuthScheme(new HttpAuthSecurityScheme { Scheme = "Basic" }), [], Origin);
        var oidc = new A2ACredentialContext("o", new OpenIdConnectScheme(new OpenIdConnectSecurityScheme { OpenIdConnectUrl = "https://agent.example/.well-known/openid-configuration" }), [], Origin);
        var authCodeOnly = new A2ACredentialContext("a", new OAuth2Scheme(new OAuth2SecurityScheme
        {
            Flows = new OAuthFlows { AuthorizationCode = new AuthorizationCodeOAuthFlow { AuthorizationUrl = "https://agent.example/a", TokenUrl = CardTokenUrl } },
        }), [], Origin);
        var apiKey = new A2ACredentialContext("k", SecurityScheme.ApiKey("X-Key"), [], Origin);

        Assert.True(card.CanHandle(OAuth()));
        Assert.False(card.CanHandle(OAuth("https://evil.example/token")));
        Assert.False(card.CanHandle(OAuth("http://agent.example/token"))); // different origin and no HTTPS
        Assert.False(card.CanHandle(OAuth("not a url")));
        Assert.True(withEndpoint.CanHandle(OAuth("https://evil.example/token")));
        Assert.False(card.CanHandle(bearer));
        Assert.False(card.CanHandle(oidc));
        Assert.True(withEndpoint.CanHandle(bearer));
        Assert.True(withEndpoint.CanHandle(bearerLower));
        Assert.False(withEndpoint.CanHandle(basic));
        Assert.True(withEndpoint.CanHandle(oidc));
        Assert.False(card.CanHandle(authCodeOnly));
        Assert.False(withEndpoint.CanHandle(authCodeOnly));
        Assert.False(withEndpoint.CanHandle(apiKey));

        var (named, _, _) = Make(o => o.SchemeName = "oauth");
        Assert.True(named.CanHandle(OAuth()));
        Assert.False(named.CanHandle(OAuth(name: "other")));
    }

    [Fact]
    public void CanHandle_allows_http_loopback_card_token_url_and_RequireHttps_false()
    {
        var (p, _, _) = Make();
        var local = new Uri("http://localhost:5000/");
        Assert.True(p.CanHandle(OAuth("http://localhost:5000/token", origin: local)));
        var plain = new Uri("http://agent.example/");
        Assert.False(p.CanHandle(OAuth("http://agent.example/token", origin: plain)));
        var (relaxed, _, _) = Make(o => o.RequireHttps = false);
        Assert.True(relaxed.CanHandle(OAuth("http://agent.example/token", origin: plain)));
    }

    [Fact]
    public async Task Redirect_from_the_token_endpoint_is_one_request_and_Unauthenticated()
    {
        var (p, h, _) = Make();
        h.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://evil.example/steal");
            return r;
        };
        var ex = await Fails(p);
        Assert.Equal(1, h.Calls);
        Assert.Contains("302", ex.Message);
    }

    [Fact]
    public void Redirecting_TokenHandler_is_refused()
    {
        using var redirecting = new SocketsHttpHandler { AllowAutoRedirect = true };
        Assert.Throws<ArgumentException>(() => new OAuth2ClientCredentialsProvider(
            new OAuth2ClientCredentialsOptions { ClientId = "i", ClientSecret = "s", TokenHandler = redirecting }));
    }

    [Fact]
    public async Task Error_messages_do_not_contain_secret_or_token()
    {
        var (p, h, _) = Make(o => o.ClientSecret = "SECRET-VALUE");
        h.Respond = _ => Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_client","error_description":"bad SECRET-VALUE here","echo":"SECRET-VALUE","access_token":"TOKEN-VALUE","client_secret":"SECRET-VALUE"}""");
        var ex = await Fails(p);
        Assert.Contains("invalid_client", ex.Message);
        Assert.DoesNotContain("SECRET-VALUE", ex.Message);
        Assert.DoesNotContain("TOKEN-VALUE", ex.Message);

        h.Respond = _ => Json(HttpStatusCode.OK, """{"access_token":"TOKEN-VALUE","token_type":"mac","echo":"SECRET-VALUE"}""");
        var ex2 = await Fails(p);
        Assert.DoesNotContain("SECRET-VALUE", ex2.Message);
        Assert.DoesNotContain("TOKEN-VALUE", ex2.Message);
    }

    [Fact]
    public async Task Server_error_text_is_sanitized_and_capped()
    {
        var (p, h, _) = Make();
        h.Respond = _ => Json(HttpStatusCode.BadRequest, "{\"error\":\"" + new string('x', 1000) + "\\u202e\"}");
        var ex = await Fails(p);
        Assert.True(ex.Message.Length < 400);
        Assert.DoesNotContain('\u202e', ex.Message);
    }

    [Theory]
    [InlineData("""{"access_token":"t","token_type":"mac"}""")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"access_token":""}""")]
    [InlineData("""{"access_token":5}""")]
    [InlineData("""{"access_token":"a\nb"}""")]
    [InlineData("""{"access_token":"a b"}""")]
    [InlineData("""{"access_token":"t","expires_in":-1}""")]
    [InlineData("""{"access_token":"t","expires_in":"soon"}""")]
    public async Task Bad_responses_fail_Unauthenticated_and_are_not_cached(string body)
    {
        var (p, h, _) = Make();
        h.Respond = _ => Json(HttpStatusCode.OK, body);
        await Fails(p);
        await Fails(p);
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task Oversize_body_fails_Unauthenticated()
    {
        var (p, h, _) = Make();
        h.Respond = _ => Json(HttpStatusCode.OK, "{\"access_token\":\"t\",\"pad\":\"" + new string('p', 100_000) + "\"}");
        var ex = await Fails(p);
        Assert.IsAssignableFrom<Exception>(ex.InnerException);
    }

    [Fact]
    public async Task Timeout_fails_Unauthenticated()
    {
        var (p, h, time) = Make(o => o.TokenRequestTimeout = TimeSpan.FromSeconds(30));
        h.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously); // never released
        var apply = Apply(p);
        await h.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(31));
        var ex = await Assert.ThrowsAsync<A2AException>(() => apply.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task Failed_fetch_clears_in_flight_so_the_next_call_retries()
    {
        var (p, h, _) = Make();
        h.Respond = n => n == 1 ? Json(HttpStatusCode.InternalServerError, "{}") : Json(HttpStatusCode.OK, """{"access_token":"ok"}""");
        await Fails(p);
        Assert.Equal("Bearer ok", await Apply(p));
    }

    [Fact]
    public void Constructor_validation()
    {
        OAuth2ClientCredentialsProvider Build(Action<OAuth2ClientCredentialsOptions> c)
        {
            var o = new OAuth2ClientCredentialsOptions { ClientId = "i", ClientSecret = "s", TokenHandler = new TokenHandler() };
            c(o);
            return new OAuth2ClientCredentialsProvider(o);
        }

        Build(_ => { }).Dispose();
        Build(o => o.TokenEndpoint = new Uri("http://localhost:5000/token")).Dispose();
        Build(o => { o.RequireHttps = false; o.TokenEndpoint = new Uri("http://auth.example/token"); }).Dispose();

        Assert.Throws<ArgumentException>(() => Build(o => o.ClientId = ""));
        Assert.Throws<ArgumentException>(() => Build(o => o.ClientSecret = ""));
        Assert.Throws<ArgumentException>(() => Build(o => o.TokenEndpoint = new Uri("/relative", UriKind.Relative)));
        Assert.Throws<ArgumentException>(() => Build(o => o.TokenEndpoint = new Uri("http://auth.example/token")));
        Assert.Throws<ArgumentException>(() => Build(o => o.TokenEndpoint = new Uri("https://u:p@auth.example/token")));
        Assert.Throws<ArgumentException>(() => Build(o => o.TokenEndpoint = new Uri("https://auth.example/token#frag")));
        foreach (var reserved in new[] { "grant_type", "scope", "client_id", "client_secret" })
            Assert.Throws<ArgumentException>(() => Build(o => o.AdditionalParameters[reserved] = "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(o => o.RefreshBeforeExpiry = TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(o => o.DefaultTokenLifetime = TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(o => o.TokenRequestTimeout = TimeSpan.Zero));
    }

    [Fact]
    public async Task Options_are_snapshotted_and_ToString_hides_the_secret()
    {
        var handler = new TokenHandler();
        var options = new OAuth2ClientCredentialsOptions { ClientId = "id", ClientSecret = "SECRET-VALUE", TokenHandler = handler };
        var p = new OAuth2ClientCredentialsProvider(options);
        options.ClientId = "changed";
        await Apply(p);
        Assert.StartsWith("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("id:SECRET-VALUE")), handler.Requests.Single().Authorization);
        Assert.DoesNotContain("SECRET-VALUE", options.ToString());
    }

    [Fact]
    public async Task Dispose_does_not_dispose_a_caller_handler()
    {
        var handler = new TokenHandler();
        var p = new OAuth2ClientCredentialsProvider(new OAuth2ClientCredentialsOptions { ClientId = "i", ClientSecret = "s", TokenHandler = handler });
        p.Dispose();
        using var client = new HttpClient(handler, disposeHandler: false);
        using var resp = await client.PostAsync("https://x.example/", new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Many_distinct_scope_sets_stay_bounded_and_work()
    {
        var (p, h, _) = Make();
        for (var i = 0; i < 600; i++) await Apply(p, OAuth(scopes: [$"s{i}"]));
        Assert.Equal(600, h.Calls);
        Assert.StartsWith("Bearer tok", await Apply(p, OAuth(scopes: ["s0"]))); // evicted or cached, both fine
    }

    [Fact]
    public async Task Provider_without_TokenEndpoint_binds_to_the_first_origin_and_refuses_others()
    {
        var (p, h, _) = Make();
        var a = new Uri("https://a.example/");
        var b = new Uri("https://b.example/");

        Assert.True(p.CanHandle(OAuth("https://a.example/token", origin: a)));
        Assert.False(p.CanHandle(OAuth("https://b.example/token", origin: b)));
        Assert.True(p.CanHandle(OAuth("https://a.example/token", origin: a)));

        Assert.Equal("Bearer tok1", await Apply(p, OAuth("https://a.example/token", origin: a)));
        await Fails(p, OAuth("https://b.example/token", origin: b));
        Assert.Equal(1, h.Calls);
        Assert.All(h.Requests, r => Assert.Equal("a.example", r.Uri.Host));
    }

    [Fact]
    public async Task Binding_happens_on_first_apply_when_CanHandle_was_not_called()
    {
        var (p, h, _) = Make();
        await Apply(p, OAuth("https://a.example/token", origin: new Uri("https://a.example/")));
        await Fails(p, OAuth("https://b.example/token", origin: new Uri("https://b.example/")));
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Provider_with_TokenEndpoint_serves_every_origin()
    {
        var (p, h, _) = Make(o => o.TokenEndpoint = new Uri("https://auth.example/token"));
        Assert.True(p.CanHandle(OAuth(origin: new Uri("https://a.example/"))));
        Assert.True(p.CanHandle(OAuth(origin: new Uri("https://b.example/"))));
        await Apply(p, OAuth(origin: new Uri("https://a.example/")));
        await Apply(p, OAuth(origin: new Uri("https://b.example/")));
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task Origin_binding_uses_IdnHost_so_sigma_and_final_sigma_differ()
    {
        Assert.NotEqual(new Uri("https://σagent.example/").IdnHost, new Uri("https://ςagent.example/").IdnHost);
        var (p, _, _) = Make();
        var sigma = new Uri("https://σagent.example/");
        var final = new Uri("https://ςagent.example/");
        Assert.True(p.CanHandle(OAuth("https://σagent.example/token", origin: sigma)));
        Assert.False(p.CanHandle(OAuth("https://ςagent.example/token", origin: final)));
        await Fails(p, OAuth("https://ςagent.example/token", origin: final));
    }

    [Fact]
    public void Card_token_url_on_a_sigma_lookalike_host_is_not_on_the_card_origin()
    {
        Assert.NotEqual(new Uri("https://σagent.example/").IdnHost, new Uri("https://ςagent.example/").IdnHost);
        var (p, _, _) = Make();
        Assert.False(p.CanHandle(OAuth("https://ςagent.example/token", origin: new Uri("https://σagent.example/"))));
    }

    [Fact]
    public void CachedToken_ToString_does_not_print_the_token()
    {
        var t = new OAuth2ClientCredentialsProvider.CachedToken("SECRET-TOKEN", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain("SECRET-TOKEN", t.ToString());
    }

    [Fact]
    public async Task Scope_string_is_cached_per_context_and_slots_found_without_scanning()
    {
        var (p, h, _) = Make();
        var ctx = OAuth(scopes: ["b", "a"]);
        await Apply(p, ctx);
        await Apply(p, ctx);
        await Apply(p, OAuth(scopes: ["a", "b"]));
        Assert.Equal(1, h.Calls);
        Assert.Contains("scope=a+b", Assert.Single(h.Requests).Body);
    }
}
