using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using A2A.Signing;

namespace A2A.Tests;

/// <summary><see cref="Jwk" /> and <see cref="Base64Url" />: building public JWKs from keys, importing them back, and the errors.</summary>
public class JwkTests
{
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("signed data");

    public static TheoryData<string, string, int> Curves => new()
    {
        { "nistP256", "P-256", 32 },
        { "nistP384", "P-384", 48 },
        { "nistP521", "P-521", 66 }
    };

    private static ECCurve Named(string name) => name switch
    {
        "nistP256" => ECCurve.NamedCurves.nistP256,
        "nistP384" => ECCurve.NamedCurves.nistP384,
        _ => ECCurve.NamedCurves.nistP521
    };

    [Fact]
    public void New_jwk_has_an_empty_key_type() => Assert.Equal(string.Empty, new Jwk().Kty);

    [Theory]
    [MemberData(nameof(Curves))]
    public void Ec_key_becomes_a_public_jwk_with_its_curve(string curve, string crv, int coordinateBytes)
    {
        using var key = ECDsa.Create(Named(curve));
        var jwk = Jwk.FromPublicKey(key, "k1", "ES256");

        Assert.Equal("EC", jwk.Kty);
        Assert.Equal("k1", jwk.Kid);
        Assert.Equal("sig", jwk.Use);
        Assert.Equal("ES256", jwk.Alg);
        Assert.Equal(crv, jwk.Crv);
        Assert.Equal(coordinateBytes, Base64Url.Decode(jwk.X!).Length);
        Assert.Equal(coordinateBytes, Base64Url.Decode(jwk.Y!).Length);
        Assert.Null(jwk.N);
        Assert.Null(jwk.E);
    }

    [Fact]
    public void Ec_key_on_a_curve_known_only_by_oid_gets_its_name()
    {
        // ECCurve.CreateFromValue gives a curve without a friendly name; the OID decides.
        foreach (var (oid, crv) in new[] { ("1.2.840.10045.3.1.7", "P-256"), ("1.3.132.0.34", "P-384"), ("1.3.132.0.35", "P-521") })
        {
            using var key = ECDsa.Create(ECCurve.CreateFromValue(oid));
            Assert.Equal(crv, Jwk.FromPublicKey(key, "k1").Crv);
        }
    }

    [Fact]
    public void Ec_key_on_another_curve_is_refused()
    {
        ECDsa key;
        try
        {
            key = ECDsa.Create(ECCurve.NamedCurves.brainpoolP256r1);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or CryptographicException)
        {
            Assert.Skip("brainpoolP256r1 is not available on this platform.");
            return;
        }

        using (key)
        {
            var ex = Assert.Throws<NotSupportedException>(() => Jwk.FromPublicKey(key, "k1"));
            Assert.StartsWith("Unsupported curve ", ex.Message);
        }
    }

    [Fact]
    public void Rsa_key_becomes_a_public_jwk()
    {
        using var key = RSA.Create(2048);
        var jwk = Jwk.FromPublicKey(key, "r1", "PS256");
        var parameters = key.ExportParameters(false);

        Assert.Equal("RSA", jwk.Kty);
        Assert.Equal("r1", jwk.Kid);
        Assert.Equal("sig", jwk.Use);
        Assert.Equal("PS256", jwk.Alg);
        Assert.Equal(parameters.Modulus, Base64Url.Decode(jwk.N!));
        Assert.Equal(parameters.Exponent, Base64Url.Decode(jwk.E!));
        Assert.Null(jwk.Crv);
        Assert.Null(jwk.X);
    }

    [Fact]
    public void Alg_is_left_out_when_not_given() => Assert.Null(Jwk.FromPublicKey(ECDsa.Create(), "k1").Alg);

    [Fact]
    public void Other_key_type_is_refused()
    {
        using var key = ECDiffieHellman.Create();
        var ex = Assert.Throws<NotSupportedException>(() => Jwk.FromPublicKey(key, "k1"));
        Assert.Equal($"Unsupported key type {key.GetType().Name}.", ex.Message);
    }

    [Theory]
    [InlineData("nistP256")]
    [InlineData("nistP384")]
    [InlineData("nistP521")]
    public void Ec_jwk_imports_as_the_same_public_key(string curve)
    {
        using var key = ECDsa.Create(Named(curve));
        var signature = key.SignData(Data, HashAlgorithmName.SHA256);

        using var imported = (ECDsa)Jwk.FromPublicKey(key, "k1").ToPublicKey();

        Assert.True(imported.VerifyData(Data, signature, HashAlgorithmName.SHA256));
    }

    [Fact]
    public void Rsa_jwk_imports_as_the_same_public_key()
    {
        using var key = RSA.Create(2048);
        var signature = key.SignData(Data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        using var imported = (RSA)Jwk.FromPublicKey(key, "r1").ToPublicKey();

        Assert.True(imported.VerifyData(Data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    }

    [Theory]
    [InlineData("P-999")]
    [InlineData("p-256")] // names are case-sensitive
    [InlineData(null)]
    public void Ec_jwk_with_an_unknown_curve_is_refused(string? crv)
    {
        var jwk = Jwk.FromPublicKey(ECDsa.Create(ECCurve.NamedCurves.nistP256), "k1");
        jwk.Crv = crv;
        var ex = Assert.Throws<NotSupportedException>(() => jwk.ToPublicKey());
        Assert.Equal($"Unsupported curve '{crv}'.", ex.Message);
    }

    [Theory]
    [InlineData("x", "Missing x")]
    [InlineData("y", "Missing y")]
    public void Ec_jwk_without_a_coordinate_is_refused(string missing, string message)
    {
        var jwk = Jwk.FromPublicKey(ECDsa.Create(ECCurve.NamedCurves.nistP256), "k1");
        if (missing == "x") jwk.X = null;
        else jwk.Y = null;
        Assert.Equal(message, Assert.Throws<FormatException>(() => jwk.ToPublicKey()).Message);
    }

    [Theory]
    [InlineData("n", "Missing n")]
    [InlineData("e", "Missing e")]
    public void Rsa_jwk_without_modulus_or_exponent_is_refused(string missing, string message)
    {
        var jwk = Jwk.FromPublicKey(RSA.Create(2048), "r1");
        if (missing == "n") jwk.N = null;
        else jwk.E = null;
        Assert.Equal(message, Assert.Throws<FormatException>(() => jwk.ToPublicKey()).Message);
    }

    [Theory]
    [InlineData("oct")]
    [InlineData("OKP")]
    [InlineData("ec")] // case-sensitive
    [InlineData("")]
    public void Jwk_of_another_key_type_is_refused(string kty)
    {
        var ex = Assert.Throws<NotSupportedException>(() => new Jwk { Kty = kty }.ToPublicKey());
        Assert.Equal($"Unsupported key type '{kty}'.", ex.Message);
    }

    [Fact]
    public void Jwk_uses_the_rfc_7517_member_names()
    {
        var jwk = new Jwk { Kty = "EC", Kid = "k", Use = "sig", Alg = "ES256", Crv = "P-256", X = "x", Y = "y", N = "n", E = "e" };
        var json = JsonSerializer.Serialize(jwk);

        Assert.Equal("""{"kty":"EC","kid":"k","use":"sig","alg":"ES256","crv":"P-256","x":"x","y":"y","n":"n","e":"e"}""", json);
        Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize<Jwk>(json)));
    }

    [Fact]
    public void Jwk_set_reads_its_keys_member()
    {
        var set = JsonSerializer.Deserialize<JwkSet>("""{"keys":[{"kty":"EC","kid":"a"},{"kty":"RSA","kid":"b"}]}""")!;
        Assert.Equal(["a", "b"], set.Keys.Select(k => k.Kid));
        Assert.Empty(new JwkSet().Keys);
    }

    // ---- Base64Url ----

    [Theory]
    [InlineData("", new byte[0])]
    [InlineData("AA", new byte[] { 0 })]
    [InlineData("AAA", new byte[] { 0, 0 })]
    [InlineData("AAAA", new byte[] { 0, 0, 0 })]
    [InlineData("AA==", new byte[] { 0 })]   // padding is accepted
    [InlineData("-_8", new byte[] { 0xFB, 0xFF })] // url alphabet
    public void Decode_reads_base64url_with_or_without_padding(string text, byte[] expected) =>
        Assert.Equal(expected, Base64Url.Decode(text));

    [Theory]
    [InlineData("A")]
    [InlineData("AAAAA")]
    public void Decode_refuses_an_impossible_length(string text) =>
        Assert.Equal("Invalid base64url length.", Assert.Throws<FormatException>(() => Base64Url.Decode(text)).Message);

    [Fact]
    public void Encode_uses_the_url_alphabet_without_padding() => Assert.Equal("-_8", Base64Url.Encode([0xFB, 0xFF]));

    [Theory]
    [InlineData(191)]
    [InlineData(192)]
    [InlineData(193)]
    [InlineData(1000)]
    public void Encode_round_trips_around_the_stack_buffer_size(int length)
    {
        var data = Enumerable.Range(0, length).Select(i => (byte)(i * 37)).ToArray();
        var text = Base64Url.Encode(data);

        Assert.Equal(Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_'), text);
        Assert.Equal(data, Base64Url.Decode(text));
    }
}
