using System.Collections.Concurrent;
using A2A.Client.Credentials;
using A2A.Client.Identity.Azure;
using Azure.Core;

namespace A2A.Tests.Credentials;

public class AzureTokenCredentialProviderTests
{
    private static readonly Uri Origin = new("https://agent.example/");
    private const string Scope = "api://my-agent/.default";

    private sealed class FakeCredential(FakeTimeProvider time) : TokenCredential
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ConcurrentQueue<TokenRequestContext> Requests { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Gate { get; set; }
        public Func<int, AccessToken> Respond { get; set; } = n => new AccessToken($"tok{n}", time.GetUtcNow().AddHours(1));
        public Func<int, Exception?> Fail { get; set; } = _ => null;

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The provider must use GetTokenAsync.");

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _calls);
            Requests.Enqueue(requestContext);
            var gate = Gate;
            Entered.TrySetResult();
            if (gate is not null) await gate.Task.WaitAsync(cancellationToken);
            if (Fail(n) is { } ex) throw ex;
            return Respond(n);
        }
    }

    private static (AzureTokenCredentialProvider Provider, FakeCredential Credential, FakeTimeProvider Time) Make(
        Action<AzureTokenCredentialOptions>? configure = null)
    {
        var time = new FakeTimeProvider();
        var credential = new FakeCredential(time);
        var options = new AzureTokenCredentialOptions { TimeProvider = time };
        options.Scopes.Add(Scope);
        configure?.Invoke(options);
        return (new AzureTokenCredentialProvider(credential, options), credential, time);
    }

    private static A2ACredentialContext Context(SecurityScheme? scheme = null, string[]? scopes = null, Uri? origin = null, string name = "entra") =>
        new(name, scheme ?? new OAuth2Scheme(new OAuth2SecurityScheme()), scopes ?? [], origin ?? Origin);

    private static async Task<string?> Apply(AzureTokenCredentialProvider p, A2ACredentialContext? ctx = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://agent.example/a2a");
        await p.ApplyAsync(request, ctx ?? Context(), ct);
        return request.Headers.Authorization?.ToString();
    }

    private static async Task<A2AException> Fails(AzureTokenCredentialProvider p, A2ACredentialContext? ctx = null)
    {
        var ex = await Assert.ThrowsAsync<A2AException>(() => Apply(p, ctx));
        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        return ex;
    }

    [Fact]
    public async Task Sends_the_configured_scopes_and_tenant_and_never_the_card_scopes()
    {
        var (p, c, _) = Make(o => o.TenantId = "tenant-1");
        Assert.Equal("Bearer tok1", await Apply(p, Context(scopes: ["agent.invoke", "admin"])));
        var request = Assert.Single(c.Requests);
        Assert.Equal([Scope], request.Scopes);
        Assert.Equal("tenant-1", request.TenantId);
    }

    [Fact]
    public void Handles_oauth2_openid_connect_and_bearer_schemes()
    {
        var (p, _, _) = Make();
        Assert.True(p.CanHandle(Context(new OAuth2Scheme(new OAuth2SecurityScheme()))));
        Assert.True(p.CanHandle(Context(new OpenIdConnectScheme(new OpenIdConnectSecurityScheme()))));
        Assert.True(p.CanHandle(Context(SecurityScheme.Bearer())));
        Assert.False(p.CanHandle(Context(new HttpAuthScheme(new HttpAuthSecurityScheme { Scheme = "Basic" }))));
        Assert.False(p.CanHandle(Context(SecurityScheme.ApiKey("X-Key"))));
        Assert.False(p.CanHandle(Context(new MutualTlsScheme(new MutualTlsSecurityScheme()))));
    }

    [Fact]
    public void SchemeName_limits_the_provider_to_one_scheme()
    {
        var (p, _, _) = Make(o => o.SchemeName = "entra");
        Assert.False(p.CanHandle(Context(name: "other")));
        Assert.True(p.CanHandle(Context(name: "entra")));
    }

    [Fact]
    public async Task Binds_to_the_first_card_origin_and_refuses_others()
    {
        var (p, c, _) = Make();
        Assert.True(p.CanHandle(Context()));
        Assert.False(p.CanHandle(Context(origin: new Uri("https://other.example/"))));
        var ex = await Fails(p, Context(origin: new Uri("https://other.example/")));
        Assert.Contains("other.example", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, c.Calls);
    }

    [Fact]
    public async Task AgentOrigin_is_the_only_origin_from_the_start()
    {
        var (p, c, _) = Make(o => o.AgentOrigin = new Uri("https://agent.example/some/path"));
        Assert.False(p.CanHandle(Context(origin: new Uri("https://first.example/"))));
        Assert.True(p.CanHandle(Context()));
        await Fails(p, Context(origin: new Uri("https://first.example/")));
        Assert.Equal(0, c.Calls);
        Assert.Equal("Bearer tok1", await Apply(p));
    }

    [Fact]
    public async Task Token_is_cached_and_refreshed_five_minutes_before_expiry()
    {
        var (p, c, time) = Make(); // one hour lifetime, refresh at 55 minutes
        await Apply(p);
        time.Advance(TimeSpan.FromMinutes(55) - TimeSpan.FromSeconds(1));
        Assert.Equal("Bearer tok1", await Apply(p));
        Assert.Equal(1, c.Calls);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("Bearer tok2", await Apply(p));
        Assert.Equal(2, c.Calls);
    }

    [Fact]
    public async Task Refresh_is_capped_at_half_a_short_lifetime()
    {
        var (p, c, time) = Make();
        c.Respond = n => new AccessToken($"tok{n}", time.GetUtcNow().AddMinutes(4));
        await Apply(p);
        time.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        Assert.Equal("Bearer tok1", await Apply(p));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("Bearer tok2", await Apply(p));
    }

    [Fact]
    public async Task An_earlier_RefreshOn_from_the_credential_wins()
    {
        var (p, c, time) = Make();
        c.Respond = n => new AccessToken($"tok{n}", time.GetUtcNow().AddHours(1), time.GetUtcNow().AddMinutes(30));
        await Apply(p);
        time.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(1));
        Assert.Equal("Bearer tok1", await Apply(p));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("Bearer tok2", await Apply(p));
    }

    [Fact]
    public async Task Concurrent_callers_share_one_request()
    {
        var (p, c, _) = Make();
        c.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 50).Select(_ => Task.Run(() => Apply(p))).ToArray();
        await c.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        c.Gate.SetResult();
        var results = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, r => Assert.Equal("Bearer tok1", r));
        Assert.Equal(1, c.Calls);
    }

    [Fact]
    public async Task Cancelling_one_caller_does_not_cancel_the_shared_request()
    {
        var (p, c, _) = Make();
        c.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var first = Apply(p, ct: cts.Token);
        await c.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Apply(p);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        c.Gate.SetResult();
        Assert.Equal("Bearer tok1", await second.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, c.Calls);
    }

    [Fact]
    public async Task A_failed_refresh_uses_the_old_token_while_it_is_valid()
    {
        var (p, c, time) = Make();
        c.Fail = n => n >= 2 ? new InvalidOperationException("down") : null;
        await Apply(p);
        time.Advance(TimeSpan.FromMinutes(56));
        Assert.Equal("Bearer tok1", await Apply(p));
        Assert.Equal(2, c.Calls);
        time.Advance(TimeSpan.FromMinutes(4));
        await Fails(p);
    }

    [Fact]
    public async Task Credential_failure_is_Unauthenticated_with_the_type()
    {
        var (p, c, _) = Make();
        c.Fail = _ => new InvalidOperationException("ManagedIdentityCredential authentication unavailable.");
        var ex = await Fails(p);
        Assert.Contains("InvalidOperationException", ex.Message, StringComparison.Ordinal);
        Assert.Contains("unavailable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_credential_that_hangs_times_out()
    {
        var (p, c, time) = Make(o => o.TokenRequestTimeout = TimeSpan.FromMilliseconds(50));
        c.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously); // never released
        // The timeout runs on the provider's clock, so it fires when the fake clock moves past it.
        var apply = Apply(p);
        await c.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(1));
        var ex = await Assert.ThrowsAsync<A2AException>(() => apply.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tok en", null)]
    [InlineData("tok\r\nX: y", null)]
    [InlineData("tok", "PoP")]
    public async Task Bad_tokens_are_refused(string token, string? type)
    {
        var (p, c, time) = Make();
        c.Respond = _ => new AccessToken(token, time.GetUtcNow().AddHours(1), null, type ?? "Bearer");
        await Fails(p);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var (p, c, time) = Make();
        c.Respond = _ => new AccessToken("tok", time.GetUtcNow().AddSeconds(-1));
        var ex = await Fails(p);
        Assert.Contains("expired", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_are_checked()
    {
        var credential = new FakeCredential(new FakeTimeProvider());
        Assert.Throws<ArgumentException>(() => new AzureTokenCredentialProvider(credential, new AzureTokenCredentialOptions()));
        var blank = new AzureTokenCredentialOptions();
        blank.Scopes.Add(" ");
        Assert.Throws<ArgumentException>(() => new AzureTokenCredentialProvider(credential, blank));
        var relative = new AzureTokenCredentialOptions { AgentOrigin = new Uri("/a2a", UriKind.Relative) };
        relative.Scopes.Add(Scope);
        Assert.Throws<ArgumentException>(() => new AzureTokenCredentialProvider(credential, relative));
        var timeout = new AzureTokenCredentialOptions { TokenRequestTimeout = TimeSpan.Zero };
        timeout.Scopes.Add(Scope);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AzureTokenCredentialProvider(credential, timeout));
    }

    [Fact]
    public async Task Card_selection_and_the_handler_send_the_token_to_the_agent()
    {
        var (p, _, _) = Make();
        var card = Cards.Card(
            schemes: new Dictionary<string, SecurityScheme> { ["key"] = SecurityScheme.ApiKey("X-Key"), ["entra"] = SecurityScheme.Bearer() },
            requirements: [Cards.Req(("key", [])), Cards.Req(("entra", ["agent.invoke"]))]);
        var resolved = Cards.Resolved(card);
        var binding = A2ACredentialBinding.Create(resolved, card.SupportedInterfaces[0], [p]);
        Assert.Equal("entra", Assert.Single(binding.Schemes).SchemeName);

        var inner = new FakeHandler();
        using var http = new HttpClient(new A2ACredentialHandler(binding, inner));
        using var response = await http.GetAsync(new Uri("https://agent.example/a2a"));
        Assert.True(inner.Requests.TryDequeue(out var sent));
        Assert.Equal("Bearer tok1", sent!.Headers.Authorization!.ToString());
    }
}
