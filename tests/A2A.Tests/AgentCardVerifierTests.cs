using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Signing;

namespace A2A.Tests;

/// <summary>
///     <see cref="AgentCardVerifier" />: each check on a signature entry and its protected header, the outcome of several
///     signatures, and the <c>jku</c> rules. Signatures are built by hand here so each header can be set exactly; the
///     signer itself is covered in <see cref="SigningTests" /> and trust end to end in <see cref="CardTrustTests" />.
/// </summary>
public class AgentCardVerifierTests
{
    private const string Origin = "https://agent.example";
    private static readonly Uri CardUri = new(Origin + "/.well-known/agent-card.json");
    private const string GoodHeader = """{"alg":"ES256","typ":"JOSE","kid":"k1"}""";

    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static JsonObject Card() => AgentCardCanonicalizer.ToNode(SigningTests.SampleCard());

    private static string B64(string text) => Base64Url.Encode(Encoding.UTF8.GetBytes(text));

    // Appends a signature over the card's canonical form with the given protected header (JSON text).
    private static JsonObject Sign(JsonObject card, string headerJson, ECDsa? key = null, string alg = "ES256")
    {
        var protectedB64 = B64(headerJson);
        var input = AgentCardVerifier.SigningInput(protectedB64, AgentCardCanonicalizer.Canonicalize(card));
        var signature = Base64Url.Encode(JwsAlgorithms.Sign(alg, key ?? Key, input));
        AddSignature(card, new JsonObject { ["protected"] = protectedB64, ["signature"] = signature });
        return card;
    }

    private static void AddSignature(JsonObject card, JsonNode? entry)
    {
        if (card["signatures"] is not JsonArray signatures) card["signatures"] = signatures = new JsonArray();
        signatures.Add(entry);
    }

    private static CardTrustPolicy Trusting(params Jwk[] keys) =>
        new() { TrustedKeys = { [Origin] = new JwkSet { Keys = keys.ToList() } } };

    private static CardTrustPolicy TrustingKey() => Trusting(Jwk.FromPublicKey(Key, "k1"));

    private static Task<AgentCardVerificationResult> Verify(JsonObject card, CardTrustPolicy? policy = null, IJwksFetcher? fetcher = null) =>
        new AgentCardVerifier(policy ?? TrustingKey(), fetcher).VerifyAsync(card, CardUri, TestContext.Current.CancellationToken);

    private static async Task AssertResult(JsonObject card, AgentCardVerificationStatus status, string reason,
        CardTrustPolicy? policy = null, IJwksFetcher? fetcher = null)
    {
        var result = await Verify(card, policy, fetcher);
        Assert.Equal(status, result.Status);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(Origin, result.Issuer);
    }

    // ---- arguments and input ----

    [Fact]
    public async Task Card_and_source_are_required()
    {
        var verifier = new AgentCardVerifier(new CardTrustPolicy());
        await Assert.ThrowsAsync<ArgumentNullException>(() => verifier.VerifyAsync((JsonObject)null!, CardUri));
        await Assert.ThrowsAsync<ArgumentNullException>(() => verifier.VerifyAsync(Card(), null!));
    }

    [Fact]
    public async Task Raw_json_must_be_an_object()
    {
        var verifier = new AgentCardVerifier(new CardTrustPolicy());
        var ex = await Assert.ThrowsAsync<JsonException>(() => verifier.VerifyAsync("null", CardUri));
        Assert.Equal("Agent Card must be a JSON object.", ex.Message);
    }

    [Fact]
    public async Task The_signed_card_verifies()
    {
        var result = await Verify(Sign(Card(), GoodHeader));
        Assert.Equal(new AgentCardVerificationResult(AgentCardVerificationStatus.Verified, "k1", Origin, null), result);
    }

    // ---- no signatures ----

    [Fact]
    public async Task Card_without_signatures_is_unsigned() =>
        await AssertResult(Card(), AgentCardVerificationStatus.Unsigned, "the card has no signatures");

    [Fact]
    public async Task Card_with_an_empty_signature_list_is_unsigned()
    {
        var card = Card();
        card["signatures"] = new JsonArray();
        await AssertResult(card, AgentCardVerificationStatus.Unsigned, "the card has no signatures");
    }

    // ---- the signature entry ----

    [Fact]
    public async Task Entry_that_is_not_a_signature_object_is_malformed()
    {
        var card = Card();
        AddSignature(card, 42);
        var result = await Verify(card);
        Assert.Equal(AgentCardVerificationStatus.Failed, result.Status);
        Assert.StartsWith("signature[0]: malformed (", result.Reason);
    }

    [Theory]
    [InlineData(null, "c2ln")]
    [InlineData("", "c2ln")]
    [InlineData("e30", null)]
    [InlineData("e30", "")]
    public async Task Entry_without_protected_header_or_signature_fails(string? protectedB64, string? signature)
    {
        var card = Card();
        AddSignature(card, new JsonObject { ["protected"] = protectedB64, ["signature"] = signature });
        await AssertResult(card, AgentCardVerificationStatus.Failed, "signature[0]: missing protected header or signature");
    }

    [Fact]
    public async Task Null_entry_fails()
    {
        var card = Card();
        AddSignature(card, null);
        await AssertResult(card, AgentCardVerificationStatus.Failed, "signature[0]: missing protected header or signature");
    }

    [Theory]
    [InlineData("abcde", "c2ln")]       // base64url length 1 mod 4: FormatException
    [InlineData("e30", "abcde")]        // the same for the signature
    [InlineData("bm90IGpzb24", "c2ln")] // "not json": JsonException
    public async Task Undecodable_header_or_signature_fails(string protectedB64, string signature)
    {
        var card = Card();
        AddSignature(card, new JsonObject { ["protected"] = protectedB64, ["signature"] = signature });
        var result = await Verify(card);
        Assert.Equal(AgentCardVerificationStatus.Failed, result.Status);
        Assert.StartsWith("signature[0]: undecodable (", result.Reason);
    }

    [Fact]
    public async Task Header_that_is_json_null_fails()
    {
        var card = Card();
        AddSignature(card, new JsonObject { ["protected"] = B64("null"), ["signature"] = "c2ln" });
        await AssertResult(card, AgentCardVerificationStatus.Failed, "signature[0]: empty header");
    }

    // ---- the protected header ----

    [Fact]
    public async Task Missing_typ_fails() =>
        await AssertResult(Sign(Card(), """{"alg":"ES256","kid":"k1"}"""), AgentCardVerificationStatus.Failed,
            "signature[0]: missing typ header");

    [Fact]
    public async Task Other_typ_fails() =>
        await AssertResult(Sign(Card(), """{"alg":"ES256","typ":"JWT","kid":"k1"}"""), AgentCardVerificationStatus.Failed,
            "signature[0]: typ 'JWT' not accepted");

    [Fact]
    public async Task Missing_alg_fails()
    {
        var result = await Verify(Sign(Card(), """{"typ":"JOSE","kid":"k1"}"""));
        Assert.Equal(AgentCardVerificationStatus.Failed, result.Status);
        Assert.Contains("not allowed", result.Reason);
    }

    [Fact]
    public async Task Supported_alg_left_out_of_the_policy_fails()
    {
        var policy = TrustingKey();
        policy.AllowedAlgorithms.Remove("ES256");
        await AssertResult(Sign(Card(), GoodHeader), AgentCardVerificationStatus.Failed,
            "signature[0]: algorithm 'ES256' not allowed", policy);
    }

    [Fact]
    public async Task Alg_the_policy_allows_but_the_sdk_does_not_support_fails()
    {
        var policy = TrustingKey();
        policy.AllowedAlgorithms.Add("HS256");
        await AssertResult(Sign(Card(), """{"alg":"HS256","typ":"JOSE","kid":"k1"}"""), AgentCardVerificationStatus.Failed,
            "signature[0]: algorithm 'HS256' not allowed", policy);
    }

    [Fact]
    public async Task Crit_header_fails() =>
        await AssertResult(Sign(Card(), """{"alg":"ES256","typ":"JOSE","kid":"k1","crit":["exp"],"exp":1}"""),
            AgentCardVerificationStatus.Failed, "signature[0]: unsupported 'crit' header");

    [Theory]
    [InlineData("""{"alg":"ES256","typ":"JOSE"}""")]
    [InlineData("""{"alg":"ES256","typ":"JOSE","kid":""}""")]
    [InlineData("""{"alg":"ES256","typ":"JOSE","kid":5}""")] // not a string: read as missing
    public async Task Missing_kid_is_unverified(string header)
    {
        var result = await Verify(Sign(Card(), header));
        Assert.Equal(new AgentCardVerificationResult(AgentCardVerificationStatus.Unverified, null, Origin,
            "signature[0]: missing kid, no trusted key applies"), result);
    }

    [Fact]
    public async Task Unknown_kid_is_unverified_and_named() =>
        await AssertResult(Sign(Card(), """{"alg":"ES256","typ":"JOSE","kid":"other"}"""),
            AgentCardVerificationStatus.Unverified, "signature[0]: no trusted key for kid 'other' at https://agent.example");

    // ---- trusted keys ----

    [Fact]
    public async Task Wrong_key_fails()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await AssertResult(Sign(Card(), GoodHeader, other), AgentCardVerificationStatus.Failed,
            "signature[0]: signature does not verify with trusted key 'k1'");
    }

    [Fact]
    public async Task Changed_card_fails()
    {
        var card = Sign(Card(), GoodHeader);
        card["name"] = "Changed";
        await AssertResult(card, AgentCardVerificationStatus.Failed, "signature[0]: signature does not verify with trusted key 'k1'");
    }

    [Fact]
    public async Task Trusted_key_pinned_to_another_alg_is_not_used()
    {
        // The right key, but its "alg" says ES384: it must not verify an ES256 signature.
        var jwk = Jwk.FromPublicKey(Key, "k1", "ES384");
        await AssertResult(Sign(Card(), GoodHeader), AgentCardVerificationStatus.Failed,
            "signature[0]: signature does not verify with trusted key 'k1'", Trusting(jwk));
    }

    [Fact]
    public async Task Trusted_key_pinned_to_the_same_alg_is_used() =>
        Assert.Equal(AgentCardVerificationStatus.Verified,
            (await Verify(Sign(Card(), GoodHeader), Trusting(Jwk.FromPublicKey(Key, "k1", "ES256")))).Status);

    [Fact]
    public async Task Unusable_trusted_key_is_skipped_for_the_next_one()
    {
        // The first key has no x coordinate (FormatException on import) and the second an unknown curve
        // (NotSupportedException); neither stops the third from verifying.
        var good = Jwk.FromPublicKey(Key, "k1");
        var noX = new Jwk { Kty = "EC", Kid = "k1", Crv = "P-256", Y = good.Y };
        var badCurve = new Jwk { Kty = "EC", Kid = "k1", Crv = "P-999", X = good.X, Y = good.Y };

        Assert.Equal(AgentCardVerificationStatus.Verified, (await Verify(Sign(Card(), GoodHeader), Trusting(noX, badCurve, good))).Status);
        await AssertResult(Sign(Card(), GoodHeader), AgentCardVerificationStatus.Failed,
            "signature[0]: signature does not verify with trusted key 'k1'", Trusting(noX, badCurve));
    }

    // ---- several signatures ----

    [Fact]
    public async Task One_good_signature_among_bad_ones_verifies()
    {
        var card = Card();
        Sign(card, """{"alg":"ES256","typ":"JWT","kid":"k1"}"""); // fails
        Sign(card, """{"alg":"ES256","typ":"JOSE","kid":"unknown"}"""); // unverified
        Sign(card, GoodHeader);

        Assert.Equal(new AgentCardVerificationResult(AgentCardVerificationStatus.Verified, "k1", Origin, null), await Verify(card));
    }

    [Fact]
    public async Task Failed_signature_wins_over_unverified_ones_and_all_reasons_are_listed()
    {
        var card = Card();
        Sign(card, """{"alg":"ES256","typ":"JOSE","kid":"unknown"}""");
        Sign(card, """{"alg":"ES256","typ":"JWT","kid":"bad"}""");

        var result = await Verify(card);

        Assert.Equal(AgentCardVerificationStatus.Failed, result.Status);
        Assert.Equal("bad", result.KeyId);
        Assert.Equal(
            "signature[1]: typ 'JWT' not accepted; signature[0]: no trusted key for kid 'unknown' at https://agent.example",
            result.Reason);
    }

    [Fact]
    public async Task Only_unverified_signatures_list_every_reason()
    {
        var card = Card();
        Sign(card, """{"alg":"ES256","typ":"JOSE","kid":"a"}""");
        Sign(card, """{"alg":"ES256","typ":"JOSE","kid":"b"}""");

        var result = await Verify(card);

        Assert.Equal(AgentCardVerificationStatus.Unverified, result.Status);
        Assert.Equal("a", result.KeyId);
        Assert.Equal(
            "signature[0]: no trusted key for kid 'a' at https://agent.example; signature[1]: no trusted key for kid 'b' at https://agent.example",
            result.Reason);
    }

    // ---- case-variant members ----

    [Fact]
    public async Task Verified_card_with_case_variant_members_fails_and_names_each()
    {
        var card = Sign(Card(), GoodHeader);
        card["IconUrl"] = "https://evil.example/icon.png";
        card["NAME"] = "Evil";

        var result = await Verify(card);

        Assert.Equal(AgentCardVerificationStatus.Failed, result.Status);
        Assert.Equal("k1", result.KeyId);
        Assert.Equal(
            "member '$.IconUrl' differs from a v1.0 Agent Card field only by case and is not signed; " +
            "member '$.NAME' differs from a v1.0 Agent Card field only by case and is not signed",
            result.Reason);
    }

    // ---- jku ----

    private const string JkuHeader = """{"alg":"ES256","typ":"JOSE","kid":"k1","jku":"https://keys.example/jwks"}""";

    private static CardTrustPolicy JkuPolicy() => new CardTrustPolicy().AllowJkuHost(Origin, "keys.example");

    private static JwkSet KeySet() => new() { Keys = [Jwk.FromPublicKey(Key, "k1")] };

    [Fact]
    public async Task Key_from_an_allowed_jku_verifies()
    {
        var fetcher = new Fetcher(_ => KeySet());
        Assert.Equal(AgentCardVerificationStatus.Verified, (await Verify(Sign(Card(), JkuHeader), JkuPolicy(), fetcher)).Status);
        Assert.Equal(["https://keys.example/jwks"], fetcher.Requests.Select(u => u.AbsoluteUri));
    }

    [Theory]
    [InlineData("keys/jwks", "jku is not an absolute URL")]
    [InlineData("http://keys.example/jwks", "jku must use https")]
    [InlineData("https://user:pw@keys.example/jwks", "jku host 'keys.example' is not allowed for https://agent.example")]
    [InlineData("https://other.example/jwks", "jku host 'other.example' is not allowed for https://agent.example")]
    public async Task Refused_jku_is_unverified_with_the_reason(string jku, string note)
    {
        var fetcher = new Fetcher(_ => KeySet());
        await AssertResult(Sign(Card(), $$"""{"alg":"ES256","typ":"JOSE","kid":"k1","jku":"{{jku}}"}"""),
            AgentCardVerificationStatus.Unverified,
            $"signature[0]: no trusted key for kid 'k1' at https://agent.example ({note})", JkuPolicy(), fetcher);
        Assert.Empty(fetcher.Requests);
    }

    [Fact]
    public async Task Jku_without_a_fetcher_is_unverified() =>
        await AssertResult(Sign(Card(), JkuHeader), AgentCardVerificationStatus.Unverified,
            "signature[0]: no trusted key for kid 'k1' at https://agent.example (no JWKS fetcher configured)", JkuPolicy());

    [Fact]
    public async Task Failed_fetch_is_unverified_and_gives_every_message()
    {
        var fetcher = new Fetcher(_ => throw new HttpRequestException("outer", new HttpRequestException("inner")));
        await AssertResult(Sign(Card(), JkuHeader), AgentCardVerificationStatus.Unverified,
            "signature[0]: no trusted key for kid 'k1' at https://agent.example (jku fetch failed: outer inner)", JkuPolicy(), fetcher);
    }

    [Fact]
    public async Task Fetch_timeout_is_a_failed_fetch()
    {
        // OperationCanceledException without the caller's token cancelled: an HttpClient timeout.
        var fetcher = new Fetcher(_ => throw new TaskCanceledException("timed out"));
        var result = await Verify(Sign(Card(), JkuHeader), JkuPolicy(), fetcher);
        Assert.Equal(AgentCardVerificationStatus.Unverified, result.Status);
        Assert.EndsWith("(jku fetch failed: timed out)", result.Reason);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        var fetcher = new Fetcher(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var verifier = new AgentCardVerifier(JkuPolicy(), fetcher);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.VerifyAsync(Sign(Card(), JkuHeader), CardUri, cts.Token));
    }

    [Fact]
    public async Task Fetched_key_set_is_reused_by_later_verifications()
    {
        var fetcher = new Fetcher(_ => KeySet());
        var verifier = new AgentCardVerifier(JkuPolicy(), fetcher);
        var card = Sign(Card(), JkuHeader);

        for (var i = 0; i < 3; i++)
            Assert.Equal(AgentCardVerificationStatus.Verified, (await verifier.VerifyAsync(card, CardUri)).Status);
        Assert.Single(fetcher.Requests);
    }

    [Fact]
    public async Task Key_set_with_zero_lifetime_is_fetched_again_each_time()
    {
        var fetcher = new LifetimeFetcher(TimeSpan.Zero);
        var verifier = new AgentCardVerifier(JkuPolicy(), fetcher);
        var card = Sign(Card(), JkuHeader);

        for (var i = 0; i < 3; i++)
            Assert.Equal(AgentCardVerificationStatus.Verified, (await verifier.VerifyAsync(card, CardUri)).Status);
        Assert.Equal(3, fetcher.Calls);
    }

    [Theory]
    [InlineData(null, 600)]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(300, 300)]
    [InlineData(599, 599)]
    [InlineData(600, 600)]
    [InlineData(3600, 600)]
    public void Cache_lifetime_follows_the_publisher_up_to_ten_minutes(int? maxAgeSeconds, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
            AgentCardVerifier.CacheLifetime(maxAgeSeconds is { } s ? TimeSpan.FromSeconds(s) : null));

    private sealed class Fetcher(Func<Uri, JwkSet> fetch) : IJwksFetcher
    {
        public List<Uri> Requests { get; } = new();

        public ValueTask<JwkSet> FetchAsync(Uri jku, CancellationToken cancellationToken)
        {
            Requests.Add(jku);
            return ValueTask.FromResult(fetch(jku));
        }
    }

    private sealed class LifetimeFetcher(TimeSpan maxAge) : IJwksLifetimeFetcher
    {
        public int Calls { get; private set; }

        public ValueTask<JwkSet> FetchAsync(Uri jku, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("FetchWithLifetimeAsync is used when it is available.");

        public ValueTask<JwksFetchResult> FetchWithLifetimeAsync(Uri jku, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new JwksFetchResult(KeySet(), maxAge));
        }
    }
}
