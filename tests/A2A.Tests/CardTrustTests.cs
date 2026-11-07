using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Client;
using A2A.Client.Cards;
using A2A.Json;
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
        var result = await new AgentCardVerifier(new CardTrustPolicy(), new HttpClient(evil)).VerifyAsync(raw, CardUri);
        Assert.Equal(AgentCardVerificationStatus.Unverified, result.Status);
        var resolved = await Resolver(new CardServer(raw), jwks: evil).ResolveAsync(AgentUrl);
        Assert.Equal(AgentCardVerificationStatus.Unverified, resolved.Verification.Status);
        Assert.Equal((Origin, (string?)null), resolved.Identity);

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

        var result = await new AgentCardVerifier(new CardTrustPolicy(), new HttpClient(jwks)).VerifyAsync(Signed(key), CardUri);

        Assert.Equal(AgentCardVerificationStatus.Unverified, result.Status);
        Assert.Equal(Origin, result.Issuer);
        Assert.Empty(jwks.Requests);
    }

    [Fact]
    public async Task Allowlisted_jku_verifies()
    {
        var key = NewKey("k1", "https://keys.example/jwks.json");
        var jwks = new JwksServer(Set(key));
        var policy = new CardTrustPolicy();
        policy.AllowedJkuHosts.Add("keys.example");
        var verifier = new AgentCardVerifier(policy, new HttpClient(jwks));

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
            (await new AgentCardVerifier(policy, new HttpClient(plain)).VerifyAsync(Signed(insecure), CardUri)).Status);
        Assert.Empty(plain.Requests);

        var resolved = await Resolver(new CardServer(Signed(key)), o => o.Trust.AllowedJkuHosts.Add("keys.example"), new JwksServer(Set(key)))
            .ResolveAsync(AgentUrl);
        Assert.Equal(AgentCardVerificationStatus.Verified, resolved.Verification.Status);
        Assert.Equal((Origin, (string?)"k1"), resolved.Identity);
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
        var server = new CardServer(Unsigned(c => c.Description = "v1"));
        var logger = new ListLogger();
        var store = new InMemoryCardPinStore();
        var changes = new List<CardChangedEventArgs>();
        var measured = new List<long>();
        using var listener = CounterListener("a2a.card.changed", Origin, measured);
        var notify = Resolver(server, o => o.PinStore = store, logger: logger);
        notify.CardChanged += (_, e) => changes.Add(e);

        await notify.ResolveAsync(AgentUrl);
        var v1 = (await store.GetAsync(Origin))!.Hash;
        Assert.Empty(changes);

        server.Json = Unsigned(c => c.Description = "v2");
        await notify.ResolveAsync(AgentUrl);

        var change = Assert.Single(changes);
        Assert.Equal(Origin, change.Origin);
        Assert.Equal(v1, change.OldHash);
        Assert.NotEqual(v1, change.NewHash);
        Assert.Equal(change.NewHash, (await store.GetAsync(Origin))!.Hash);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(Origin));
        Assert.Equal([1L], measured);

        var strict = Resolver(server, o =>
        {
            o.PinStore = store;
            o.PinPolicy = PinPolicy.Strict;
        });
        server.Json = Unsigned(c => c.Description = "v3");
        var ex = await Assert.ThrowsAsync<AgentCardPinException>(() => strict.ResolveAsync(AgentUrl));
        Assert.Equal(Origin, ex.Origin);
        Assert.Equal(change.NewHash, ex.OldHash);
        // Strict does not move the pin, so the changed card keeps failing.
        await Assert.ThrowsAsync<AgentCardPinException>(() => strict.ResolveAsync(AgentUrl));

        var off = Resolver(server, o =>
        {
            o.PinStore = store;
            o.PinPolicy = PinPolicy.Off;
        });
        off.CardChanged += (_, e) => changes.Add(e);
        await off.ResolveAsync(AgentUrl);
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

        Assert.Equal((Origin, (string?)"k1"), (await resolver.ResolveAsync(AgentUrl)).Identity);
        server.Json = Signed(k2);
        var rotated = await resolver.ResolveAsync(AgentUrl);

        Assert.Equal((Origin, (string?)"k2"), rotated.Identity);
        Assert.Empty(changes);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("k2"));
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
        Assert.Equal((Origin, (string?)null), resolved.Identity);
        Assert.Equal("Signer", resolved.Card.Name);
    }

    [Fact]
    public async Task Unsigned_card_rejected_once_trust_is_configured_unless_opted_out()
    {
        var key = NewKey("k1");
        var required = Resolver(new CardServer(Unsigned()), o => o.Trust.TrustedKeys[Origin] = Set(key));
        Assert.Equal(AgentCardVerificationStatus.Unsigned,
            (await Assert.ThrowsAsync<AgentCardVerificationException>(() => required.ResolveAsync(AgentUrl))).Result.Status);

        var jkuOnly = Resolver(new CardServer(Unsigned()), o => o.Trust.AllowedJkuHosts.Add("keys.example"));
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
    public async Task Default_jwks_client_refuses_private_addresses()
    {
        var key = NewKey("k1", "https://localhost:1/jwks");
        var policy = new CardTrustPolicy();
        policy.AllowedJkuHosts.Add("localhost");

        var result = await new AgentCardVerifier(policy).VerifyAsync(Signed(key), CardUri);

        Assert.Equal(AgentCardVerificationStatus.Unverified, result.Status);
        Assert.Contains("private address", result.Reason);
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
        policy.AllowedJkuHosts.Add("keys.example");

        var result = await new AgentCardVerifier(policy, new HttpClient(jwks)).VerifyAsync(A2AJson.Serialize(card), CardUri);

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
        policy.AllowedJkuHosts.Add("keys.example");
        var verifier = new AgentCardVerifier(policy, new HttpClient(jwks));

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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(A2AJson.Serialize(set), Encoding.UTF8, "application/json"),
            });
        }
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
