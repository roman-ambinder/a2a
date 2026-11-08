using System.Security.Cryptography;
using A2A.Client;
using A2A.Client.Cards;
using A2A.Client.Peers;
using A2A.Json;
using A2A.Signing;

namespace A2A.Tests;

/// <summary><see cref="PeerIdentity"/>, <see cref="IPeerIdentityResolver"/> and <see cref="ResolvedAgentCard.Identity"/>.</summary>
public class PeerIdentityTests
{
    private const string Origin = "https://agent.example";
    private static readonly Uri AgentUrl = new(Origin + "/");

    private static AgentCardSigningKey NewKey(string kid) =>
        new(ECDsa.Create(ECCurve.NamedCurves.nistP256), kid, "ES256", null);

    private static JwkSet Set(params AgentCardSigningKey[] keys) => new() { Keys = keys.Select(k => k.ToPublicJwk()).ToList() };

    private static string Signed(AgentCardSigningKey key) => A2AJson.Serialize(AgentCardSigner.Sign(SigningTests.SampleCard(), key));

    private static string Unsigned(Action<AgentCard>? change = null)
    {
        var card = SigningTests.SampleCard();
        change?.Invoke(card);
        return A2AJson.Serialize(card);
    }

    private static A2ACardResolver Resolver(HttpMessageHandler cards, Action<A2ACardResolverOptions>? configure = null)
    {
        var options = new A2ACardResolverOptions { DefaultCacheDuration = TimeSpan.Zero };
        configure?.Invoke(options);
        return new A2ACardResolver(new HttpClient(cards), options);
    }

    [Fact]
    public void Origin_is_normalized() =>
        Assert.Equal(new PeerIdentity("https://agent.example"), new PeerIdentity("https://Agent.Example:443/"));

    [Theory]
    [InlineData("ftp://a")]
    [InlineData("not a url")]
    [InlineData("file:///x")]
    public void Non_http_origin_is_rejected(string origin) =>
        Assert.Throws<ArgumentException>(() => new PeerIdentity(origin));

    [Fact]
    public void Empty_key_id_and_external_id_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new PeerIdentity(Origin, ""));
        Assert.Throws<ArgumentException>(() => new PeerIdentity(Origin) { ExternalId = "" });
        Assert.Throws<ArgumentException>(() => new PeerIdentity(Origin) { ExternalId = "  " });
    }

    [Fact]
    public void Assurance_marks_origin_only_as_weaker()
    {
        Assert.Equal(PeerIdentityAssurance.OriginOnly, new PeerIdentity(Origin).Assurance);
        Assert.Equal(PeerIdentityAssurance.Verified, new PeerIdentity(Origin, "k1").Assurance);
        Assert.Equal(PeerIdentityAssurance.Verified, (new PeerIdentity(Origin) { ExternalId = "did:web:a" }).Assurance);
    }

    [Fact]
    public void ToString_marks_origin_only()
    {
        Assert.Contains("origin only", new PeerIdentity("https://a").ToString());
        Assert.Equal("https://a#k1", new PeerIdentity("https://a", "k1").ToString());
        Assert.Equal("https://a#k1 did:web:a", new PeerIdentity("https://a", "k1") { ExternalId = "did:web:a" }.ToString());
    }

    [Fact]
    public void Equality_includes_key_and_external_id()
    {
        var a = new PeerIdentity(Origin, "k1");
        Assert.Equal(a, new PeerIdentity(Origin, "k1"));
        Assert.NotEqual(a, new PeerIdentity(Origin, "k2"));
        Assert.NotEqual(a, new PeerIdentity(Origin));
        Assert.NotEqual(a, a with { ExternalId = "did:web:a" });
        Assert.Equal(a with { ExternalId = "did:web:a" }, a with { ExternalId = "did:web:a" });
    }

    [Fact]
    public async Task Unsigned_card_has_origin_only_identity()
    {
        var resolved = await Resolver(new CardTrustTests.CardServer(Unsigned())).ResolveAsync(AgentUrl);

        Assert.Null(resolved.Identity.KeyId);
        Assert.Equal(PeerIdentityAssurance.OriginOnly, resolved.Identity.Assurance);
        Assert.Equal(Origin, resolved.Identity.Origin);
    }

    [Fact]
    public async Task Verified_card_identity_has_kid()
    {
        var key = NewKey("k1");
        var resolved = await Resolver(new CardTrustTests.CardServer(Signed(key)), o => o.Trust.TrustedKeys[Origin] = Set(key)).ResolveAsync(AgentUrl);

        Assert.Equal("k1", resolved.Identity.KeyId);
        Assert.Equal(PeerIdentityAssurance.Verified, resolved.Identity.Assurance);
    }

    [Fact]
    public async Task Unverified_kid_never_enters_identity()
    {
        var real = NewKey("real");
        var other = NewKey("other");
        var resolved = await Resolver(new CardTrustTests.CardServer(Signed(other)), o =>
        {
            o.Trust.TrustedKeys[Origin] = Set(real);
            o.Trust.RequireSignature = false;
        }).ResolveAsync(AgentUrl);

        Assert.Equal(AgentCardVerificationStatus.Unverified, resolved.Verification.Status);
        Assert.Equal("other", resolved.Verification.KeyId);
        Assert.Null(resolved.Identity.KeyId);
        Assert.Equal(PeerIdentityAssurance.OriginOnly, resolved.Identity.Assurance);
    }

    [Fact]
    public void Hand_built_card_gets_identity_from_verification()
    {
        var uri = new Uri(Origin + "/.well-known/agent-card.json");
        var verified = new AgentCardVerificationResult(AgentCardVerificationStatus.Verified, "k1", null, null);
        var unverified = new AgentCardVerificationResult(AgentCardVerificationStatus.Unverified, "k2", null, null);

        Assert.Equal(new PeerIdentity(Origin, "k1"), new ResolvedAgentCard(SigningTests.SampleCard(), "{}", uri, verified).Identity);
        Assert.Equal(new PeerIdentity(Origin), new ResolvedAgentCard(SigningTests.SampleCard(), "{}", uri, unverified).Identity);
    }

    [Fact]
    public async Task Custom_resolver_adds_external_id()
    {
        var resolved = await Resolver(new CardTrustTests.CardServer(Unsigned()), o => o.IdentityResolver = new DidResolver()).ResolveAsync(AgentUrl);

        Assert.Equal("did:web:agent.example", resolved.Identity.ExternalId);
        Assert.Equal(PeerIdentityAssurance.Verified, resolved.Identity.Assurance);
        Assert.Null(resolved.Identity.KeyId);
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("key")]
    public async Task Custom_resolver_cannot_change_origin_or_key(string change)
    {
        var counting = new CountingResolver(c => change == "origin" ? new PeerIdentity("https://evil.example") : new PeerIdentity(c.Baseline.Origin, "other"));
        var resolver = Resolver(new CardTrustTests.CardServer(Unsigned()), o =>
        {
            o.IdentityResolver = counting;
            o.DefaultCacheDuration = TimeSpan.FromMinutes(5);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(AgentUrl));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(AgentUrl));

        // Nothing was cached, so the resolver ran each time.
        Assert.Equal(2, counting.Calls);
    }

    [Fact]
    public async Task Resolver_is_not_called_for_a_rejected_card()
    {
        var counting = new CountingResolver(c => c.Baseline);

        var required = Resolver(new CardTrustTests.CardServer(Unsigned()), o =>
        {
            o.IdentityResolver = counting;
            o.Trust.RequireSignature = true;
        });
        await Assert.ThrowsAsync<AgentCardVerificationException>(() => required.ResolveAsync(AgentUrl));
        Assert.Equal(0, counting.Calls);

        var server = new CardTrustTests.CardServer(Unsigned());
        var store = new InMemoryCardPinStore();
        var strict = Resolver(server, o =>
        {
            o.IdentityResolver = counting;
            o.PinStore = store;
            o.PinPolicy = PinPolicy.Strict;
        });
        await strict.ResolveAsync(AgentUrl);
        Assert.Equal(1, counting.Calls);
        server.Json = Unsigned(c => c.Description = "changed");
        await Assert.ThrowsAsync<AgentCardPinException>(() => strict.ResolveAsync(AgentUrl));
        Assert.Equal(1, counting.Calls);
    }

    [Fact]
    public async Task Resolver_exception_fails_resolution()
    {
        var boom = new InvalidOperationException("boom");
        var resolver = Resolver(new CardTrustTests.CardServer(Unsigned()), o => o.IdentityResolver = new CountingResolver(_ => throw boom));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(AgentUrl));

        Assert.Same(boom, ex);
    }

    [Fact]
    public async Task Resolver_gets_the_timeout_token()
    {
        var seen = false;
        var resolver = Resolver(new CardTrustTests.CardServer(Unsigned()), o => o.IdentityResolver = new CountingResolver(c =>
        {
            seen = true;
            return c.Baseline;
        }, token => Assert.True(token.CanBeCanceled)));

        await resolver.ResolveAsync(AgentUrl);

        Assert.True(seen);
    }

    private sealed class DidResolver : IPeerIdentityResolver
    {
        public ValueTask<PeerIdentity> ResolveAsync(PeerIdentityContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(context.Baseline with { ExternalId = "did:web:agent.example" });
    }

    private sealed class CountingResolver(Func<PeerIdentityContext, PeerIdentity> result, Action<CancellationToken>? onToken = null) : IPeerIdentityResolver
    {
        public int Calls { get; private set; }

        public ValueTask<PeerIdentity> ResolveAsync(PeerIdentityContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            onToken?.Invoke(cancellationToken);
            return ValueTask.FromResult(result(context));
        }
    }
}
