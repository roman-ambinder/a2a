using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Client;
using A2A.Client.Peers;
using A2A.Client.Cards;
using A2A.Json;
using A2A.Security.Http;
using A2A.Signing;
using Microsoft.Extensions.Logging;

namespace A2A.Tests;

/// <summary>Per-origin trust, <c>jku</c> allowlist, card pinning and untrusted text (spec §7.5).</summary>
public class CardTrustTests
{
    private const string Origin = "https://agent.example";
    private static readonly Uri AgentUrl = new(Origin + "/");
    private static readonly Uri CardUri = new(Origin + "/.well-known/agent-card.json");

    private static AgentCardSigningKey NewKey(string kid, string? jku = null) =>
        new(ECDsa.Create(ECCurve.NamedCurves.nistP256), kid, "ES256", jku);

    private static JwkSet Set(params AgentCardSigningKey[] keys) => new() { Keys = keys.Select(k => k.ToPublicJwk()).ToList() };

    private static string Signed(AgentCardSigningKey key, Action<AgentCard>? change = null)
    {
        var card = SigningTests.SampleCard();
        change?.Invoke(card);
        return A2AJson.Serialize(AgentCardSigner.Sign(card, key));
    }

    [Fact]
    public async Task A_trusted_key_changed_after_use_verifies_with_its_new_value()
    {
        // Imported keys are reused per trusted key; changing the key's fields must not keep the old imported key.
        var first = NewKey("k1");
        var second = NewKey("k1");
        var policy = new CardTrustPolicy();
        var jwk = first.ToPublicJwk();
        policy.TrustedKeys[Origin] = new JwkSet { Keys = [jwk] };
        var verifier = new AgentCardVerifier(policy);

        Assert.Equal(AgentCardVerificationStatus.Verified, (await verifier.VerifyAsync(Signed(first), CardUri)).Status);
        Assert.Equal(AgentCardVerificationStatus.Verified, (await verifier.VerifyAsync(Signed(first), CardUri)).Status); // reused key

        var replacement = second.ToPublicJwk();
        jwk.X = replacement.X;
        jwk.Y = replacement.Y;
        Assert.Equal(AgentCardVerificationStatus.Failed, (await verifier.VerifyAsync(Signed(first), CardUri)).Status);
        Assert.Equal(AgentCardVerificationStatus.Verified, (await verifier.VerifyAsync(Signed(second), CardUri)).Status);
    }

    [Fact]
    public async Task Concurrent_verifications_with_one_trusted_key_all_verify()
    {
        var key = NewKey("k1");
        var verifier = new AgentCardVerifier(new CardTrustPolicy { TrustedKeys = { [Origin] = Set(key) } });
        var raw = Signed(key);
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => verifier.VerifyAsync(raw, CardUri))));
        Assert.All(results, r => Assert.Equal(AgentCardVerificationStatus.Verified, r.Status));
    }

    [Fact]
    public void Base64Url_encoding_matches_the_padded_form_with_url_characters_for_every_length()
    {
        var rng = new Random(7);
        for (var n = 0; n < 700; n++)
        {
            var data = new byte[n];
            rng.NextBytes(data);
            var expected = Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Assert.Equal(expected, Base64Url.Encode(data));
            Assert.Equal(data, Base64Url.Decode(expected));
        }
    }

    private static string Unsigned(Action<AgentCard>? change = null)
    {
        var card = SigningTests.SampleCard();
        change?.Invoke(card);
        return A2AJson.Serialize(card);
    }

    /// <summary>Signs the sample card with a hand-built protected header.</summary>
    internal static string SignedWithHeader(AgentCardSigningKey key, Dictionary<string, string> header)
    {
        var node = AgentCardCanonicalizer.ToNode(SigningTests.SampleCard());
        var protectedB64 = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(header, A2AJson.GetTypeInfo<Dictionary<string, string>>()));
        var input = Encoding.ASCII.GetBytes(protectedB64 + "." + Base64Url.Encode(AgentCardCanonicalizer.Canonicalize(node)));
        node["signatures"] = new JsonArray(new JsonObject
        {
            ["protected"] = protectedB64,
            ["signature"] = Base64Url.Encode(JwsAlgorithms.Sign(key.Algorithm, key.Key, input)),
        });
        return node.ToJsonString();
    }

    private static A2ACardResolver Resolver(HttpMessageHandler cards, Action<A2ACardResolverOptions>? configure = null,
        HttpMessageHandler? jwks = null, ILogger? logger = null)
    {
        var options = new A2ACardResolverOptions { DefaultCacheDuration = TimeSpan.Zero };
        if (jwks is not null) options.JwksHttpClient = new HttpClient(jwks);
        configure?.Invoke(options);
        return new A2ACardResolver(new HttpClient(cards), options, logger);
    }

    [Fact]
    public async Task Forged_card_with_attacker_jku_is_never_verified()
    {
        var attacker = NewKey("attacker", "https://evil.example/jwks");
        var raw = Signed(attacker, c => c.Description = "forged");
        var evil = new JwksServer(Set(attacker));

        // (a) No trust configured: reported Unverified, the card is still returned.
        var result = await new AgentCardVerifier(new CardTrustPolicy(), new GuardedJwksFetcher(new HttpMessageInvoker(evil))).VerifyAsync(raw, CardUri);
        Assert.Equal(AgentCardVerificationStatus.Unverified, result.Status);
        var resolved = await Resolver(new CardServer(raw), jwks: evil).ResolveAsync(AgentUrl);
        Assert.Equal(AgentCardVerificationStatus.Unverified, resolved.Verification.Status);
        Assert.Equal(new PeerIdentity(Origin), resolved.Identity);

        // (b) The real key is pinned for the origin: the attacker kid is not trusted, and signatures are now required.
        var real = NewKey("real");
        var resolver = Resolver(new CardServer(raw), o => o.Trust.TrustedKeys[Origin] = Set(real), evil);
        var ex = await Assert.ThrowsAsync<AgentCardVerificationException>(() => resolver.ResolveAsync(AgentUrl));
        Assert.Equal(AgentCardVerificationStatus.Unverified, ex.Result.Status);
        Assert.Empty(evil.Requests);
    }

    [Fact]
    public async Task Same_host_jku_without_allowlist_is_unverified()
    {
        var key = NewKey("k1", Origin + "/.well-known/jwks.json");
        var jwks = new JwksServer(Set(key));

        var result = await new AgentCardVerifier(new CardTrustPolicy(), new GuardedJwksFetcher(new HttpMessageInvoker(jwks))).VerifyAsync(Signed(key), CardUri);

        Assert.Equal(AgentCardVerificationStatus.Unverified, result.Status);
        Assert.Equal(Origin, result.Issuer);
        Assert.Empty(jwks.Requests);
    }

    // ---- §8.4.3: a key the publisher removed stops verifying when the published cache lifetime ends ----

    private static (AgentCardVerifier Verifier, JwksServer Jwks, AgentCardSigningKey Key) JkuSetup(string? cacheControl)
    {
        var key = NewKey("k1", "https://keys.example/jwks.json");
        var jwks = new JwksServer(Set(key)) { CacheControl = cacheControl };
        var policy = new CardTrustPolicy();
        policy.AllowJkuHost(Origin, "keys.example");
        return (new AgentCardVerifier(policy, new GuardedJwksFetcher(new HttpMessageInvoker(jwks))), jwks, key);
    }

    [Fact]
    public async Task A_key_removed_from_a_no_store_jwks_stops_verifying_at_once()
    {
        var (verifier, jwks, key) = JkuSetup("no-store");
        Assert.Equal(AgentCardVerificationStatus.Verified, (await verifier.VerifyAsync(Signed(key), CardUri)).Status);

        jwks.Set = new JwkSet(); // revoked at the publisher
        var after = await verifier.VerifyAsync(Signed(key), CardUri);

        Assert.NotEqual(AgentCardVerificationStatus.Verified, after.Status);
        Assert.Equal(2, jwks.Requests.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("max-age=3600")]
    public async Task A_jwks_without_a_short_lifetime_is_kept_for_the_default_ten_minutes(string? cacheControl)
    {
        var (verifier, jwks, key) = JkuSetup(cacheControl);
        await verifier.VerifyAsync(Signed(key), CardUri);
        jwks.Set = new JwkSet();

        var again = await verifier.VerifyAsync(Signed(key), CardUri);

        Assert.Equal(AgentCardVerificationStatus.Verified, again.Status); // still inside the cache lifetime
        Assert.Single(jwks.Requests);
    }

    [Theory]
    [InlineData("no-store", null, null, 0)]
    [InlineData("no-cache", null, null, 0)]
    [InlineData("max-age=300", null, null, 300)]
    [InlineData("max-age=300", 100, null, 200)]
    [InlineData("max-age=300", 400, null, 0)]
    [InlineData(null, null, 120, 120)]
    [InlineData(null, null, -5, 0)]
    [InlineData(null, null, null, -1)]
    public void Jwks_cache_lifetime_follows_the_response(string? cacheControl, int? ageSeconds, int? expiresInSeconds, int expected)
    {
        var date = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        response.Headers.Date = date;
        if (cacheControl is not null) response.Headers.TryAddWithoutValidation("Cache-Control", cacheControl);
        if (ageSeconds is { } age) response.Headers.Age = TimeSpan.FromSeconds(age);
        if (expiresInSeconds is { } e) response.Content.Headers.Expires = date.AddSeconds(e);

        var lifetime = GuardedJwksFetcher.MaxAgeOf(response, date);

        Assert.Equal(expected < 0 ? null : TimeSpan.FromSeconds(expected), lifetime);
    }

    [Fact]
    public void Jwks_cache_lifetime_is_capped_at_ten_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), AgentCardVerifier.CacheLifetime(null));
        Assert.Equal(TimeSpan.FromMinutes(10), AgentCardVerifier.CacheLifetime(TimeSpan.FromHours(1)));
        Assert.Equal(TimeSpan.FromSeconds(30), AgentCardVerifier.CacheLifetime(TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.Zero, AgentCardVerifier.CacheLifetime(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task Allowlisted_jku_verifies()
    {
        var key = NewKey("k1", "https://keys.example/jwks.json");
        var jwks = new JwksServer(Set(key));
        var policy = new CardTrustPolicy();
        policy.AllowJkuHost(Origin, "keys.example");
        var verifier = new AgentCardVerifier(policy, new GuardedJwksFetcher(new HttpMessageInvoker(jwks)));

        var result = await verifier.VerifyAsync(Signed(key), CardUri);

        Assert.Equal(AgentCardVerificationStatus.Verified, result.Status);
        Assert.Equal("k1", result.KeyId);
        Assert.Equal(Origin, result.Issuer);
        Assert.Equal(["https://keys.example/jwks.json"], jwks.Requests);
        Assert.Equal(AgentCardVerificationStatus.Verified, (await verifier.VerifyAsync(Signed(key), CardUri)).Status);
        Assert.Single(jwks.Requests); // cached

        // HTTPS only, even for an allowlisted host.
        var insecure = NewKey("k2", "http://keys.example/jwks.json");
        var plain = new JwksServer(Set(insecure));
        Assert.Equal(AgentCardVerificationStatus.Unverified,
            (await new AgentCardVerifier(policy, new GuardedJwksFetcher(new HttpMessageInvoker(plain))).VerifyAsync(Signed(insecure), CardUri)).Status);
        Assert.Empty(plain.Requests);

        var resolved = await Resolver(new CardServer(Signed(key)), o => o.Trust.AllowJkuHost(Origin, "keys.example"), new JwksServer(Set(key)))
            .ResolveAsync(AgentUrl);
        Assert.Equal(AgentCardVerificationStatus.Verified, resolved.Verification.Status);
        Assert.Equal(new PeerIdentity(Origin, "k1"), resolved.Identity);
    }

    [Fact]
    public async Task Jku_host_allowlisted_for_one_origin_does_not_verify_a_card_from_another()
    {
        var key = NewKey("k1", "https://keys.example/jwks.json");
        var jwks = new JwksServer(Set(key));
        var policy = new CardTrustPolicy();
        policy.AllowJkuHost(Origin, "keys.example");
        var verifier = new AgentCardVerifier(policy, new GuardedJwksFetcher(new HttpMessageInvoker(jwks)));

        var other = await verifier.VerifyAsync(Signed(key), new Uri("https://other.example/.well-known/agent-card.json"));

        Assert.Equal(AgentCardVerificationStatus.Unverified, other.Status);
        Assert.Contains("not allowed for https://other.example", other.Reason);
        Assert.Empty(jwks.Requests);
        // The origin it is listed for still verifies, and the configured origin is normalized.
        Assert.Equal(AgentCardVerificationStatus.Verified, (await verifier.VerifyAsync(Signed(key), CardUri)).Status);
        var normalized = new CardTrustPolicy();
        normalized.AllowedJkuHosts["HTTPS://Agent.Example:443/"] = new HashSet<string> { "KEYS.example" };
        Assert.Equal(AgentCardVerificationStatus.Verified,
            (await new AgentCardVerifier(normalized, new GuardedJwksFetcher(new HttpMessageInvoker(jwks))).VerifyAsync(Signed(key), CardUri)).Status);
    }

    [Fact]
    public async Task Trusted_key_wrong_signature_fails()
    {
        var key = NewKey("k1");
        var tampered = Signed(key).Replace("\"Signer\"", "\"Signes\"");
        var policy = new CardTrustPolicy();
        policy.TrustedKeys[Origin] = Set(key);

        var result = await new AgentCardVerifier(policy).VerifyAsync(tampered, CardUri);

        Assert.Equal(AgentCardVerificationStatus.Failed, result.Status);
        Assert.Equal("k1", result.KeyId);
        var resolver = Resolver(new CardServer(tampered), o =>
        {
            o.Trust.TrustedKeys[Origin] = Set(key);
            o.Trust.RequireSignature = false;
        });
        var ex = await Assert.ThrowsAsync<AgentCardVerificationException>(() => resolver.ResolveAsync(AgentUrl));
        Assert.Equal(AgentCardVerificationStatus.Failed, ex.Result.Status);
    }

    [Fact]
    public async Task Missing_typ_fails()
    {
        var key = NewKey("k1");
        var trusted = new CardTrustPolicy();
        trusted.TrustedKeys[Origin] = Set(key);

        var noTyp = SignedWithHeader(key, new() { ["alg"] = "ES256", ["kid"] = "k1" });
        var missing = await new AgentCardVerifier(trusted).VerifyAsync(noTyp, CardUri);
        Assert.Equal(AgentCardVerificationStatus.Failed, missing.Status);
        Assert.Contains("typ", missing.Reason);
        // Header checks do not depend on trust configuration.
        Assert.Equal(AgentCardVerificationStatus.Failed, (await new AgentCardVerifier(new CardTrustPolicy()).VerifyAsync(noTyp, CardUri)).Status);

        var jwt = SignedWithHeader(key, new() { ["alg"] = "ES256", ["kid"] = "k1", ["typ"] = "JWT" });
        Assert.Equal(AgentCardVerificationStatus.Failed, (await new AgentCardVerifier(trusted).VerifyAsync(jwt, CardUri)).Status);

        var joseJson = SignedWithHeader(key, new() { ["alg"] = "ES256", ["kid"] = "k1", ["typ"] = "JOSE+JSON" });
        Assert.Equal(AgentCardVerificationStatus.Verified, (await new AgentCardVerifier(trusted).VerifyAsync(joseJson, CardUri)).Status);
    }

    [Fact]
    public async Task Trusted_keys_are_per_origin()
    {
        var key = NewKey("k1");
        var policy = new CardTrustPolicy();
        policy.TrustedKeys["https://a.example"] = Set(key);
        var verifier = new AgentCardVerifier(policy);
        var raw = Signed(key);

        Assert.Equal(AgentCardVerificationStatus.Unverified, (await verifier.VerifyAsync(raw, new Uri("https://b.example/.well-known/agent-card.json"))).Status);
        Assert.Equal(AgentCardVerificationStatus.Unverified, (await verifier.VerifyAsync(raw, new Uri("https://a.example:8443/.well-known/agent-card.json"))).Status);
        var ok = await verifier.VerifyAsync(raw, new Uri("https://A.example:443/.well-known/agent-card.json"));
        Assert.Equal(AgentCardVerificationStatus.Verified, ok.Status);
        Assert.Equal("https://a.example", ok.Issuer);
    }

    [Fact]
    public async Task Card_change_raises_event_and_strict_throws()
    {
        // Its own origin: the a2a.card.changed counter is process-wide, and other tests change cards of Origin in parallel.
        const string origin = "https://card-change.agent.example";
        var agentUrl = new Uri(origin + "/");
        var server = new CardServer(Unsigned(c => c.Description = "v1"));
        var logger = new ListLogger();
        var store = new InMemoryCardPinStore();
        var changes = new List<CardChangedEventArgs>();
        var measured = new List<long>();
        using var listener = CounterListener("a2a.card.changed", origin, measured);
        var notify = Resolver(server, o => o.PinStore = store, logger: logger);
        notify.CardChanged += (_, e) => changes.Add(e);

        await notify.ResolveAsync(agentUrl);
        var v1 = (await store.GetAsync(origin))!.Hash;
        Assert.Empty(changes);

        server.Json = Unsigned(c => c.Description = "v2");
        await notify.ResolveAsync(agentUrl);

        var change = Assert.Single(changes);
        Assert.Equal(origin, change.Origin);
        Assert.Equal(v1, change.OldHash);
        Assert.NotEqual(v1, change.NewHash);
        Assert.Equal(change.NewHash, (await store.GetAsync(origin))!.Hash);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(origin));
        Assert.Equal([1L], measured);

        var strict = Resolver(server, o =>
        {
            o.PinStore = store;
            o.PinPolicy = PinPolicy.Strict;
        });
        server.Json = Unsigned(c => c.Description = "v3");
        var ex = await Assert.ThrowsAsync<AgentCardPinException>(() => strict.ResolveAsync(agentUrl));
        Assert.Equal(origin, ex.Origin);
        Assert.Equal(change.NewHash, ex.OldHash);
        // Strict does not move the pin, so the changed card keeps failing.
        await Assert.ThrowsAsync<AgentCardPinException>(() => strict.ResolveAsync(agentUrl));

        var off = Resolver(server, o =>
        {
            o.PinStore = store;
            o.PinPolicy = PinPolicy.Off;
        });
        off.CardChanged += (_, e) => changes.Add(e);
        await off.ResolveAsync(agentUrl);
        Assert.Single(changes);
    }

    [Fact]
    public async Task Rotated_key_same_content_is_not_a_change()
    {
        var k1 = NewKey("k1");
        var k2 = NewKey("k2");
        var server = new CardServer(Signed(k1));
        var logger = new ListLogger();
        var changes = new List<CardChangedEventArgs>();
        var resolver = Resolver(server, o =>
        {
            o.Trust.TrustedKeys[Origin] = Set(k1, k2);
            o.PinPolicy = PinPolicy.Strict;
        }, logger: logger);
        resolver.CardChanged += (_, e) => changes.Add(e);

        Assert.Equal(new PeerIdentity(Origin, "k1"), (await resolver.ResolveAsync(AgentUrl)).Identity);
        server.Json = Signed(k2);
        var rotated = await resolver.ResolveAsync(AgentUrl);

        Assert.Equal(new PeerIdentity(Origin, "k2"), rotated.Identity);
        Assert.Empty(changes);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("k2"));
    }

    [Fact]
    public void Untrusted_text_strips_tag_zero_width_and_format_characters()
    {
        // Unicode tag characters (U+E0000-U+E007F) can hide an instruction ("ignore" spelled in tags) inside visible text.
        var hidden = "ok\U000E0001\U000E0069\U000E0067\U000E006E\U000E006F\U000E0072\U000E0065\U000E007F!";
        Assert.Equal("ok!", new UntrustedText(hidden).ToString());

        var invisible = "a\u200Bb\u200Cc\u200Dd\u200Ee\u200Ff\u2060g\u2061h\u2062i\u2063j\u2064k\uFEFFl\u061Cm\u180En";
        Assert.Equal("abcdefghijklmn", new UntrustedText(invisible).ToString());

        // Neighbours of the stripped ranges, and other supplementary characters, stay.
        var kept = "\u2010\u2065\uFEFE\U0001F600\U000E0080\U000DFFFF";
        Assert.Equal(kept, new UntrustedText(kept).ToString());
        Assert.Equal("x y", new UntrustedText("x\u200B \U000E0020y").ToString());
    }

    [Fact]
    public async Task Untrusted_text_strips_controls_and_truncates()
    {
        var text = new UntrustedText("a\u0007b\u202Ec");
        Assert.Equal("abc", text.ToString());
        Assert.Equal("a\u0007b\u202Ec", text.Raw);
        Assert.Equal("l1\n\tl2", new UntrustedText("l1\n\tl2\r\u0000\u0085\u009B\u202A\u2066\u2069").ToString());

        var longText = new UntrustedText(new string('x', 3000)).ToString();
        Assert.Equal(2000, longText.Length);
        Assert.EndsWith("…", longText);
        Assert.Equal("", default(UntrustedText).ToString());

        var raw = Unsigned(c =>
        {
            c.Description = "evil\u202Etxt";
            c.Skills[0].Examples = ["ex\u001B[31m"];
        });
        var resolved = await Resolver(new CardServer(raw), o => o.MaxCardTextLength = 3).ResolveAsync(AgentUrl);
        Assert.Equal("ev…", resolved.Description.ToString());
        Assert.Equal("evil\u202Etxt", resolved.Description.Raw);
        var skill = Assert.Single(resolved.Skills);
        Assert.Equal("S", skill.Name.ToString());
        Assert.Equal("x", skill.Description.ToString());
        Assert.Equal("ex…", Assert.Single(skill.Examples).ToString());
    }

    [Fact]
    public async Task Unsigned_card_allowed_when_no_trust_configured()
    {
        var resolved = await new A2ACardResolver(new HttpClient(new CardServer(Unsigned()))).ResolveAsync(AgentUrl);

        Assert.Equal(AgentCardVerificationStatus.Unsigned, resolved.Verification.Status);
        Assert.Equal(new PeerIdentity(Origin), resolved.Identity);
        Assert.Equal("Signer", resolved.Card.Name);
    }

    /// <summary>What an auto-redirecting handler reports: the request now points at the redirect target.</summary>
    private sealed class RedirectingCardServer(string json, Uri target) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = target;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task A_card_served_from_another_origin_after_a_redirect_is_refused()
    {
        // An HttpClient that follows redirects (the default) must not let another origin's card be credited to this one.
        var http = new HttpClient(new RedirectingCardServer(Unsigned(), new Uri("https://evil.example/card.json")));
        var ex = await Assert.ThrowsAsync<A2AException>(() => new A2ACardResolver(http).ResolveAsync(AgentUrl));

        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
        Assert.Contains("https://evil.example", ex.Message);
    }

    [Fact]
    public async Task A_redirect_within_the_same_origin_is_accepted()
    {
        var http = new HttpClient(new RedirectingCardServer(Unsigned(), new Uri(Origin + "/cards/agent.json")));
        var resolved = await new A2ACardResolver(http).ResolveAsync(AgentUrl);

        Assert.Equal(new PeerIdentity(Origin), resolved.Identity);
    }

    [Fact]
    public async Task Unsigned_card_rejected_once_trust_is_configured_unless_opted_out()
    {
        var key = NewKey("k1");
        var required = Resolver(new CardServer(Unsigned()), o => o.Trust.TrustedKeys[Origin] = Set(key));
        Assert.Equal(AgentCardVerificationStatus.Unsigned,
            (await Assert.ThrowsAsync<AgentCardVerificationException>(() => required.ResolveAsync(AgentUrl))).Result.Status);

        var jkuOnly = Resolver(new CardServer(Unsigned()), o => o.Trust.AllowJkuHost(Origin, "keys.example"));
        await Assert.ThrowsAsync<AgentCardVerificationException>(() => jkuOnly.ResolveAsync(AgentUrl));

        var optedOut = Resolver(new CardServer(Unsigned()), o =>
        {
            o.Trust.TrustedKeys[Origin] = Set(key);
            o.Trust.RequireSignature = false;
        });
        Assert.Equal(AgentCardVerificationStatus.Unsigned, (await optedOut.ResolveAsync(AgentUrl)).Verification.Status);

        var explicitRequire = Resolver(new CardServer(Unsigned()), o => o.Trust.RequireSignature = true);
        await Assert.ThrowsAsync<AgentCardVerificationException>(() => explicitRequire.ResolveAsync(AgentUrl));
    }

    [Fact]
    public async Task Signatures_case_variant_on_unsigned_card_does_not_populate_signatures()
    {
        var key = NewKey("k1");
        var signature = JsonNode.Parse(Signed(key))!["signatures"]!.DeepClone();
        var node = JsonNode.Parse(Unsigned())!.AsObject();
        node["Signatures"] = signature;
        var raw = node.ToJsonString();

        Assert.Equal(AgentCardVerificationStatus.Unsigned, (await new AgentCardVerifier(new CardTrustPolicy()).VerifyAsync(raw, CardUri)).Status);
        var resolved = await Resolver(new CardServer(raw)).ResolveAsync(AgentUrl);
        Assert.Equal(AgentCardVerificationStatus.Unsigned, resolved.Verification.Status);
        Assert.Null(resolved.Card.Signatures);
    }

    [Fact]
    public void Signed_content_that_does_not_bind_is_an_A2AException()
    {
        var ex = Assert.Throws<A2AException>(() => A2ACardResolver.FromSignedContent(JsonNode.Parse("""{"name":5}""")!.AsObject()));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    [Theory]
    [InlineData("top-level")]
    [InlineData("nested")]
    public async Task Duplicate_member_in_card_is_an_A2AException(string where)
    {
        var raw = Unsigned();
        raw = where == "top-level"
            ? raw.Replace("\"name\":\"Signer\"", "\"name\":\"Signer\",\"name\":\"Evil\"")
            : raw.Replace("\"name\":\"S\"", "\"name\":\"S\",\"name\":\"Evil\"");
        Assert.Contains("Evil", raw);

        var ex = await Assert.ThrowsAsync<A2AException>(() => Resolver(new CardServer(raw)).ResolveAsync(AgentUrl));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    [Fact]
    public async Task Verified_signature_wins_over_a_failed_one()
    {
        var key = NewKey("k1");
        var card = SigningTests.SampleCard();
        var bad = AgentCardSigner.CreateSignature(Changed(c => c.Description = "other content"), key); // trusted kid, wrong content
        card.Signatures = [bad, AgentCardSigner.CreateSignature(SigningTests.SampleCard(), key)];
        var policy = new CardTrustPolicy();
        policy.TrustedKeys[Origin] = Set(key);

        var result = await new AgentCardVerifier(policy).VerifyAsync(A2AJson.Serialize(card), CardUri);

        Assert.Equal(AgentCardVerificationStatus.Verified, result.Status);
        Assert.Equal("k1", result.KeyId);
    }

    [Fact]
    public async Task Failed_and_unverified_signatures_are_failed()
    {
        var trusted = NewKey("k1");
        var card = SigningTests.SampleCard();
        card.Signatures =
        [
            AgentCardSigner.CreateSignature(SigningTests.SampleCard(), NewKey("unknown")),
            AgentCardSigner.CreateSignature(Changed(c => c.Description = "other content"), trusted),
        ];
        var policy = new CardTrustPolicy();
        policy.TrustedKeys[Origin] = Set(trusted);

        var result = await new AgentCardVerifier(policy).VerifyAsync(A2AJson.Serialize(card), CardUri);

        Assert.Equal(AgentCardVerificationStatus.Failed, result.Status);
        Assert.Equal("k1", result.KeyId);
    }

    [Fact]
    public async Task Default_guarded_fetcher_refuses_private_addresses()
    {
        var key = NewKey("k1", "https://localhost:1/jwks");
        var policy = new CardTrustPolicy();
        policy.AllowJkuHost(Origin, "localhost");

        var result = await new AgentCardVerifier(policy, new GuardedJwksFetcher()).VerifyAsync(Signed(key), CardUri);

        Assert.Equal(AgentCardVerificationStatus.Unverified, result.Status);
        Assert.Contains("private address", result.Reason);
    }

    [Fact]
    public async Task Body_read_io_error_is_unverified_and_negatively_cached()
    {
        var key = NewKey("k1", "https://keys.example/jwks.json");
        var handler = new BrokenBodyHandler();
        var policy = new CardTrustPolicy();
        policy.AllowJkuHost(Origin, "keys.example");
        var verifier = new AgentCardVerifier(policy, new GuardedJwksFetcher(new HttpMessageInvoker(handler)));

        var first = await verifier.VerifyAsync(Signed(key), CardUri);
        var second = await verifier.VerifyAsync(Signed(key), CardUri);

        Assert.Equal(AgentCardVerificationStatus.Unverified, first.Status);
        Assert.Contains("jku fetch failed", first.Reason);
        Assert.Equal(AgentCardVerificationStatus.Unverified, second.Status);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Resolver_reads_the_jku_allowlist_live()
    {
        var key = NewKey("k1", "https://keys.example/jwks.json");
        CardTrustPolicy? trust = null;
        var resolver = Resolver(new CardServer(Signed(key)), o => trust = o.Trust, new JwksServer(Set(key)));

        trust!.AllowJkuHost(Origin, "keys.example");
        var resolved = await resolver.ResolveAsync(AgentUrl);

        Assert.Equal(AgentCardVerificationStatus.Verified, resolved.Verification.Status);
    }

    private sealed class BrokenBodyHandler : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ThrowingStream()) });
        }
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("connection reset");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Verifier_without_a_fetcher_does_not_fetch_jku_keys()
    {
        var key = NewKey("k1", "https://keys.example/jwks.json");
        var policy = new CardTrustPolicy();
        policy.AllowJkuHost(Origin, "keys.example");

        var result = await new AgentCardVerifier(policy).VerifyAsync(Signed(key), CardUri);

        Assert.Equal(AgentCardVerificationStatus.Unverified, result.Status);
        Assert.Contains("no JWKS fetcher configured", result.Reason);
    }

    [Fact]
    public async Task Jku_fetches_are_capped_per_verification()
    {
        var card = SigningTests.SampleCard();
        card.Signatures = Enumerable.Range(0, 7)
            .Select(i => AgentCardSigner.CreateSignature(SigningTests.SampleCard(), NewKey($"k{i}", $"https://keys.example/jwks{i}.json")))
            .ToList();
        var jwks = new JwksServer(new JwkSet()); // never holds the kid, so every signature asks for its key set
        var policy = new CardTrustPolicy();
        policy.AllowJkuHost(Origin, "keys.example");

        var result = await new AgentCardVerifier(policy, new GuardedJwksFetcher(new HttpMessageInvoker(jwks))).VerifyAsync(A2AJson.Serialize(card), CardUri);

        Assert.Equal(AgentCardVerificationStatus.Unverified, result.Status);
        Assert.Equal(AgentCardVerifier.MaxJkuFetchesPerVerification, jwks.Requests.Count);
        Assert.Contains("limit", result.Reason);
    }

    [Fact]
    public async Task Failed_jku_fetch_is_cached_briefly()
    {
        var key = NewKey("k1", "https://keys.example/jwks.json");
        var jwks = new JwksServer(Set(key)) { Status = HttpStatusCode.InternalServerError };
        var policy = new CardTrustPolicy();
        policy.AllowJkuHost(Origin, "keys.example");
        var verifier = new AgentCardVerifier(policy, new GuardedJwksFetcher(new HttpMessageInvoker(jwks)));

        var first = await verifier.VerifyAsync(Signed(key), CardUri);
        jwks.Status = HttpStatusCode.OK;
        var second = await verifier.VerifyAsync(Signed(key), CardUri);

        Assert.Equal(AgentCardVerificationStatus.Unverified, first.Status);
        Assert.Contains("jku fetch failed", first.Reason);
        Assert.Equal(AgentCardVerificationStatus.Unverified, second.Status);
        Assert.Single(jwks.Requests);
    }

    [Fact]
    public async Task Header_values_in_reasons_are_sanitized()
    {
        var key = NewKey("k1");
        var hostile = "evil\u202E\u0007\r\n" + new string('x', 500);

        var typ = await new AgentCardVerifier(new CardTrustPolicy())
            .VerifyAsync(SignedWithHeader(key, new() { ["alg"] = "ES256", ["kid"] = "k1", ["typ"] = hostile }), CardUri);
        var kid = await new AgentCardVerifier(new CardTrustPolicy())
            .VerifyAsync(SignedWithHeader(key, new() { ["alg"] = "ES256", ["kid"] = hostile, ["typ"] = "JOSE" }), CardUri);
        var alg = await new AgentCardVerifier(new CardTrustPolicy())
            .VerifyAsync(SignedWithHeader(key, new() { ["alg"] = hostile, ["kid"] = "k1", ["typ"] = "JOSE" }), CardUri);

        foreach (var result in new[] { typ, kid, alg })
        {
            Assert.Contains("evil", result.Reason);
            Assert.DoesNotContain('\u202E', result.Reason!);
            Assert.DoesNotContain(result.Reason!, c => char.IsControl(c));
            Assert.DoesNotContain(new string('x', 200), result.Reason);
        }
    }

    [Fact]
    public async Task Accepted_unsigned_card_counts_a2a_card_unsigned()
    {
        const string origin = "https://unsigned.example";
        var measured = new List<long>();
        using var listener = CounterListener("a2a.card.unsigned", origin, measured);

        await Resolver(new CardServer(Unsigned())).ResolveAsync(new Uri(origin + "/"));
        await Assert.ThrowsAsync<AgentCardVerificationException>(() =>
            Resolver(new CardServer(Unsigned()), o => o.Trust.RequireSignature = true).ResolveAsync(new Uri(origin + "/")));

        Assert.Equal([1L], measured);
    }

    private static AgentCard Changed(Action<AgentCard> change)
    {
        var card = SigningTests.SampleCard();
        change(card);
        return card;
    }

    private static MeterListener CounterListener(string name, string origin, List<long> measured)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "A2A" && instrument.Name == name) l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "origin" && Equals(tag.Value, origin))
                {
                    lock (measured) measured.Add(value);
                }
            }
        });
        listener.Start();
        return listener;
    }

    internal sealed class CardServer(string json) : HttpMessageHandler
    {
        public string Json { get; set; } = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json, Encoding.UTF8, "application/json") });
    }

    private sealed class JwksServer(JwkSet set) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        /// <summary>The served key set; replace it to "remove" a key at the publisher.</summary>
        public JwkSet Set { get; set; } = set;

        /// <summary>The Cache-Control header of the response, or null for none.</summary>
        public string? CacheControl { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!.AbsoluteUri);
            var response = new HttpResponseMessage(Status)
            {
                Content = new StringContent(A2AJson.Serialize(Set), Encoding.UTF8, "application/json"),
            };
            if (CacheControl is not null) response.Headers.TryAddWithoutValidation("Cache-Control", CacheControl);
            return Task.FromResult(response);
        }
    }

    // ---- warning for a card accepted without verification (spec 8.4.3) ----

    [Fact]
    public async Task An_unsigned_card_accepted_without_trust_is_logged_once_per_origin()
    {
        var log = new ListLogger();
        var resolver = Resolver(new CardServer(A2AJson.Serialize(SigningTests.SampleCard())), logger: log);

        await resolver.ResolveAsync(AgentUrl);
        await resolver.ResolveAsync(AgentUrl); // fetched again (no cache), not logged again

        var warning = Assert.Single(log.Entries, e => e.Message.Contains("8.4.3", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(Origin, warning.Message, StringComparison.Ordinal);
        Assert.Contains("Unsigned", warning.Message, StringComparison.Ordinal);
        Assert.Contains("no trusted keys", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unverified_card_accepted_with_RequireSignature_false_names_that_setting()
    {
        var log = new ListLogger();
        var resolver = Resolver(new CardServer(Signed(NewKey("other"))), o =>
        {
            o.Trust.TrustedKeys[Origin] = Set(NewKey("k1"));
            o.Trust.RequireSignature = false;
        }, logger: log);

        await resolver.ResolveAsync(AgentUrl);

        var warning = Assert.Single(log.Entries, e => e.Message.Contains("8.4.3", StringComparison.Ordinal));
        Assert.Contains("Unverified", warning.Message, StringComparison.Ordinal);
        Assert.Contains("RequireSignature is false", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_verified_card_and_a_rejected_card_are_not_logged_as_unverified()
    {
        var key = NewKey("k1");
        var log = new ListLogger();
        var verified = Resolver(new CardServer(Signed(key)), o => o.Trust.TrustedKeys[Origin] = Set(key), logger: log);
        var required = Resolver(new CardServer(A2AJson.Serialize(SigningTests.SampleCard())),
            o => o.Trust.TrustedKeys[Origin] = Set(key), logger: log);

        await verified.ResolveAsync(AgentUrl);
        await Assert.ThrowsAsync<AgentCardVerificationException>(() => required.ResolveAsync(AgentUrl));

        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("8.4.3", StringComparison.Ordinal));
    }

    internal sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
