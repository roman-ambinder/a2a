using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using A2A.Signing;

namespace A2A.Tests;

/// <summary>
///     <see cref="AgentCardSigningKey" />, <see cref="AgentCardSigner" /> and <see cref="JwsAlgorithms" />: key checks, the
///     signature header, appending to existing signatures, and every supported algorithm. Verification of signed cards is in
///     <see cref="AgentCardVerifierTests" />.
/// </summary>
public class AgentCardSignerTests
{
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("signing input");

    private static readonly string[] AllAlgorithms =
        ["ES256", "ES384", "ES512", "RS256", "RS384", "RS512", "PS256", "PS384", "PS512"];

    private static AsymmetricAlgorithm KeyFor(string alg) => alg switch
    {
        "ES256" => ECDsa.Create(ECCurve.NamedCurves.nistP256),
        "ES384" => ECDsa.Create(ECCurve.NamedCurves.nistP384),
        "ES512" => ECDsa.Create(ECCurve.NamedCurves.nistP521),
        _ => RSA.Create(2048)
    };

    // ---- values the signature would not cover (S2) ----

    private static AgentCardSigningKey Es256() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256), "k1", "ES256");

    /// <summary>A card with a security requirement that has no scopes: the canonicalization prunes the empty list.</summary>
    private static AgentCard CardWithPrunedValue()
    {
        var card = SigningTests.SampleCard();
        card.SecurityRequirements = [new SecurityRequirement { Schemes = { ["bearer"] = new StringList() } }];
        return card;
    }

    [Fact]
    public void A_card_with_pruned_values_is_refused_when_asked_and_the_paths_are_named()
    {
        var card = CardWithPrunedValue();
        Assert.NotEmpty(AgentCardCanonicalizer.PrunedPaths(AgentCardCanonicalizer.ToNode(card)));

        var sign = Assert.Throws<ArgumentException>(() => AgentCardSigner.Sign(card, Es256(), refusePrunedValues: true));
        Assert.Contains("$.securityRequirements", sign.Message);
        Assert.Equal("card", sign.ParamName);
        Assert.Throws<ArgumentException>(() => AgentCardSigner.CreateSignature(card, Es256(), refusePrunedValues: true));
    }

    [Fact]
    public void Without_the_option_a_card_with_pruned_values_is_signed_around_them()
    {
        var card = CardWithPrunedValue();
        Assert.Single(AgentCardSigner.Sign(card, Es256()).Signatures!);
        Assert.Single(AgentCardSigner.Sign(card, Es256(), refusePrunedValues: false).Signatures!);
    }

    [Fact]
    public void A_card_without_pruned_values_is_signed_with_the_option_on()
    {
        Assert.Single(AgentCardSigner.Sign(SigningTests.SampleCard(), Es256(), refusePrunedValues: true).Signatures!);
    }

    // ---- AgentCardSigningKey ----

    [Theory]
    [InlineData("HS256")]
    [InlineData("none")]
    [InlineData("es256")] // names are case-sensitive
    [InlineData("")]
    public void Key_with_an_unsupported_algorithm_is_refused(string alg)
    {
        var ex = Assert.Throws<ArgumentException>(() => new AgentCardSigningKey(ECDsa.Create(), "k1", alg));
        Assert.Equal("algorithm", ex.ParamName);
        Assert.StartsWith($"Unsupported JWS algorithm '{alg}'.", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Key_without_a_key_id_is_refused(string? kid)
    {
        var ex = Assert.Throws<ArgumentException>(() => new AgentCardSigningKey(ECDsa.Create(), kid!, "ES256"));
        Assert.Equal("keyId", ex.ParamName);
        Assert.StartsWith("Key id is required.", ex.Message);
    }

    [Fact]
    public void Key_keeps_what_it_was_given()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = new AgentCardSigningKey(ec, "k1", "ES256", "https://keys.example/jwks");

        Assert.Same(ec, key.Key);
        Assert.Equal("k1", key.KeyId);
        Assert.Equal("ES256", key.Algorithm);
        Assert.Equal("https://keys.example/jwks", key.JwksUrl);
        Assert.Null(new AgentCardSigningKey(ec, "k1", "ES256").JwksUrl);
    }

    [Fact]
    public void Public_jwk_carries_the_key_id_and_algorithm()
    {
        var jwk = new AgentCardSigningKey(ECDsa.Create(ECCurve.NamedCurves.nistP384), "k2", "ES384").ToPublicJwk();

        Assert.Equal("EC", jwk.Kty);
        Assert.Equal("k2", jwk.Kid);
        Assert.Equal("ES384", jwk.Alg);
        Assert.Equal("P-384", jwk.Crv);
    }

    // ---- AgentCardSigner ----

    private static Dictionary<string, string> Header(AgentCardSignature signature) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(Base64Url.Decode(signature.Protected))!;

    [Fact]
    public void Signature_header_has_alg_typ_and_kid()
    {
        var signature = AgentCardSigner.CreateSignature(SigningTests.SampleCard(),
            new AgentCardSigningKey(ECDsa.Create(ECCurve.NamedCurves.nistP256), "k1", "ES256"));

        Assert.Equal(new Dictionary<string, string> { ["alg"] = "ES256", ["typ"] = "JOSE", ["kid"] = "k1" }, Header(signature));
    }

    [Fact]
    public void Signature_header_has_jku_when_the_key_has_one()
    {
        var signature = AgentCardSigner.CreateSignature(SigningTests.SampleCard(),
            new AgentCardSigningKey(ECDsa.Create(ECCurve.NamedCurves.nistP256), "k1", "ES256", "https://keys.example/jwks"));

        Assert.Equal("https://keys.example/jwks", Header(signature)["jku"]);
    }

    [Fact]
    public void Signature_covers_the_canonical_card()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var card = SigningTests.SampleCard();
        var signature = AgentCardSigner.CreateSignature(card, new AgentCardSigningKey(ec, "k1", "ES256"));

        var payload = AgentCardCanonicalizer.Canonicalize(AgentCardCanonicalizer.ToNode(card));
        var input = Encoding.ASCII.GetBytes(signature.Protected + "." + Base64Url.Encode(payload));
        Assert.True(JwsAlgorithms.Verify("ES256", ec, input, Base64Url.Decode(signature.Signature)));
    }

    [Fact]
    public void Sign_appends_to_existing_signatures_and_leaves_the_card_unchanged()
    {
        var first = new AgentCardSigningKey(ECDsa.Create(ECCurve.NamedCurves.nistP256), "k1", "ES256");
        var second = new AgentCardSigningKey(ECDsa.Create(ECCurve.NamedCurves.nistP256), "k2", "ES256");
        var once = AgentCardSigner.Sign(SigningTests.SampleCard(), first);

        var twice = AgentCardSigner.Sign(once, second);

        Assert.Single(once.Signatures!);
        Assert.Equal(2, twice.Signatures!.Count);
        Assert.Equal(once.Signatures![0].Protected, twice.Signatures[0].Protected);
        Assert.Equal(once.Signatures[0].Signature, twice.Signatures[0].Signature);
        Assert.Equal("k2", Header(twice.Signatures[1])["kid"]);
    }

    [Fact]
    public void Sign_of_an_unsigned_card_adds_one_signature()
    {
        var card = SigningTests.SampleCard();
        card.Signatures = null;

        var signed = AgentCardSigner.Sign(card, new AgentCardSigningKey(ECDsa.Create(ECCurve.NamedCurves.nistP256), "k1", "ES256"));

        Assert.Single(signed.Signatures!);
        Assert.Null(card.Signatures);
    }

    // ---- JwsAlgorithms ----

    [Fact]
    public void Exactly_the_asymmetric_algorithms_are_supported()
    {
        Assert.Equal(AllAlgorithms.Order(), JwsAlgorithms.All.Order());
        foreach (var alg in AllAlgorithms) Assert.True(JwsAlgorithms.IsSupported(alg), alg);
        foreach (var alg in new[] { "HS256", "none", "EdDSA", "", "es256" }) Assert.False(JwsAlgorithms.IsSupported(alg), alg);
        Assert.False(JwsAlgorithms.IsSupported(null));
    }

    [Theory]
    [InlineData("ES256", "SHA256")]
    [InlineData("RS384", "SHA384")]
    [InlineData("PS512", "SHA512")]
    public void Hash_follows_the_algorithm_suffix(string alg, string hash) =>
        Assert.Equal(new HashAlgorithmName(hash), JwsAlgorithms.Hash(alg));

    [Fact]
    public void Hash_of_another_suffix_is_refused() =>
        Assert.Throws<NotSupportedException>(() => JwsAlgorithms.Hash("ES1"));

    [Theory]
    [InlineData("ES256")]
    [InlineData("ES384")]
    [InlineData("ES512")]
    [InlineData("RS256")]
    [InlineData("RS384")]
    [InlineData("RS512")]
    [InlineData("PS256")]
    [InlineData("PS384")]
    [InlineData("PS512")]
    public void Every_algorithm_signs_and_verifies(string alg)
    {
        using var key = KeyFor(alg);
        var signature = JwsAlgorithms.Sign(alg, key, Data);

        Assert.True(JwsAlgorithms.Verify(alg, key, Data, signature));
        Assert.False(JwsAlgorithms.Verify(alg, key, Encoding.UTF8.GetBytes("other input"), signature));
        var tampered = (byte[])signature.Clone();
        tampered[0] ^= 1;
        Assert.False(JwsAlgorithms.Verify(alg, key, Data, tampered));
    }

    [Theory]
    [InlineData("ES256", 64)]
    [InlineData("ES384", 96)]
    [InlineData("ES512", 132)]
    public void Ec_signatures_are_ieee_p1363(string alg, int length)
    {
        // JWS uses r || s with fixed-size fields, not DER.
        using var key = KeyFor(alg);
        Assert.Equal(length, JwsAlgorithms.Sign(alg, key, Data).Length);
    }

    [Fact]
    public void Rsa_padding_follows_the_algorithm()
    {
        using var rsa = RSA.Create(2048);
        var pkcs1 = JwsAlgorithms.Sign("RS256", rsa, Data);
        var pss = JwsAlgorithms.Sign("PS256", rsa, Data);

        Assert.True(rsa.VerifyData(Data, pkcs1, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.True(rsa.VerifyData(Data, pss, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        Assert.False(JwsAlgorithms.Verify("PS256", rsa, Data, pkcs1));
        Assert.False(JwsAlgorithms.Verify("RS256", rsa, Data, pss));
    }

    [Theory]
    [InlineData("ES256", false)]
    [InlineData("RS256", true)]
    [InlineData("PS256", true)]
    public void Sign_with_a_key_of_another_type_is_refused(string alg, bool ecKey)
    {
        using AsymmetricAlgorithm key = ecKey ? ECDsa.Create() : RSA.Create(2048);
        var ex = Assert.Throws<CryptographicException>(() => JwsAlgorithms.Sign(alg, key, Data));
        Assert.Equal($"Key type {key.GetType().Name} does not match algorithm {alg}.", ex.Message);
    }

    [Fact]
    public void Verify_with_a_key_of_another_type_is_false()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rsa = RSA.Create(2048);
        Assert.False(JwsAlgorithms.Verify("RS256", ec, Data, JwsAlgorithms.Sign("ES256", ec, Data)));
        Assert.False(JwsAlgorithms.Verify("ES256", rsa, Data, JwsAlgorithms.Sign("RS256", rsa, Data)));
        Assert.False(JwsAlgorithms.Verify("HS256", rsa, Data, [1, 2, 3]));
    }

    [Fact]
    public void Verify_that_the_crypto_library_rejects_is_false_not_an_exception()
    {
        // Whether a platform's crypto library throws or returns false varies; a key that always throws pins the contract.
        using var throwing = new ThrowingRsa();
        Assert.False(JwsAlgorithms.Verify("RS256", throwing, Data, new byte[256]));
        Assert.False(JwsAlgorithms.Verify("PS256", throwing, Data, new byte[256]));
        // An EC signature of the wrong size.
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(JwsAlgorithms.Verify("ES256", ec, Data, new byte[3]));
    }

    // An RSA key whose verification always fails with CryptographicException.
    private sealed class ThrowingRsa : RSA
    {
        public override RSAParameters ExportParameters(bool includePrivateParameters) => throw new NotSupportedException();
        public override void ImportParameters(RSAParameters parameters) => throw new NotSupportedException();

        public override bool VerifyHash(byte[] hash, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) =>
            throw new CryptographicException("verification failed");
    }

    [Theory]
    [InlineData("ES256", "EC", "P-256", true)]
    [InlineData("ES384", "EC", "P-384", true)]
    [InlineData("ES512", "EC", "P-521", true)]
    [InlineData("ES256", "EC", "P-384", false)]
    [InlineData("ES512", "EC", "P-256", false)]
    [InlineData("ES256", "RSA", null, false)]
    [InlineData("RS256", "RSA", null, true)]
    [InlineData("PS384", "RSA", null, true)]
    [InlineData("RS256", "EC", "P-256", false)]
    [InlineData("HS256", "RSA", null, false)]
    public void Key_matches_the_algorithm_family_and_curve(string alg, string kty, string? crv, bool expected) =>
        Assert.Equal(expected, JwsAlgorithms.KeyMatches(alg, new Jwk { Kty = kty, Crv = crv }));
}
