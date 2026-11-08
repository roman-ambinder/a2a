using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace A2A.Signing;

/// <summary>Minimal public JSON Web Key (RFC 7517) for EC (P-256/384/521) and RSA keys.</summary>
public sealed class Jwk
{
    /// <summary>Key type: <c>EC</c> or <c>RSA</c>.</summary>
    [JsonPropertyName("kty")] public string Kty { get; set; } = string.Empty;
    /// <summary>Key id. Null when not set.</summary>
    [JsonPropertyName("kid")] public string? Kid { get; set; }
    /// <summary>Intended use, for example <c>sig</c>. Null when not set.</summary>
    [JsonPropertyName("use")] public string? Use { get; set; }
    /// <summary>Algorithm the key is for, for example <c>ES256</c>. Null when not set.</summary>
    [JsonPropertyName("alg")] public string? Alg { get; set; }
    /// <summary>EC curve name, for example <c>P-256</c>. Null for RSA keys.</summary>
    [JsonPropertyName("crv")] public string? Crv { get; set; }
    /// <summary>EC public point X coordinate (base64url). Null for RSA keys.</summary>
    [JsonPropertyName("x")] public string? X { get; set; }
    /// <summary>EC public point Y coordinate (base64url). Null for RSA keys.</summary>
    [JsonPropertyName("y")] public string? Y { get; set; }
    /// <summary>RSA modulus (base64url). Null for EC keys.</summary>
    [JsonPropertyName("n")] public string? N { get; set; }
    /// <summary>RSA public exponent (base64url). Null for EC keys.</summary>
    [JsonPropertyName("e")] public string? E { get; set; }

    /// <summary>Builds a public JWK from an EC or RSA key. Throws for other key types.</summary>
    public static Jwk FromPublicKey(AsymmetricAlgorithm key, string kid, string? alg = null)
    {
        switch (key)
        {
            case ECDsa ec:
            {
                var p = ec.ExportParameters(false);
                return new Jwk
                {
                    Kty = "EC", Kid = kid, Use = "sig", Alg = alg,
                    Crv = CurveName(p.Curve),
                    X = Base64Url.Encode(p.Q.X!), Y = Base64Url.Encode(p.Q.Y!),
                };
            }
            case RSA rsa:
            {
                var p = rsa.ExportParameters(false);
                return new Jwk { Kty = "RSA", Kid = kid, Use = "sig", Alg = alg, N = Base64Url.Encode(p.Modulus!), E = Base64Url.Encode(p.Exponent!) };
            }
            default:
                throw new NotSupportedException($"Unsupported key type {key.GetType().Name}.");
        }
    }

    /// <summary>Creates a verification key. Caller disposes it.</summary>
    public AsymmetricAlgorithm ToPublicKey()
    {
        switch (Kty)
        {
            case "EC":
                var curve = Crv switch
                {
                    "P-256" => ECCurve.NamedCurves.nistP256,
                    "P-384" => ECCurve.NamedCurves.nistP384,
                    "P-521" => ECCurve.NamedCurves.nistP521,
                    _ => throw new NotSupportedException($"Unsupported curve '{Crv}'."),
                };
                return ECDsa.Create(new ECParameters
                {
                    Curve = curve,
                    Q = new ECPoint { X = Base64Url.Decode(X ?? throw new FormatException("Missing x")), Y = Base64Url.Decode(Y ?? throw new FormatException("Missing y")) },
                });
            case "RSA":
                var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = Base64Url.Decode(N ?? throw new FormatException("Missing n")),
                    Exponent = Base64Url.Decode(E ?? throw new FormatException("Missing e")),
                });
                return rsa;
            default:
                throw new NotSupportedException($"Unsupported key type '{Kty}'.");
        }
    }

    private static string CurveName(ECCurve curve) => curve.Oid.FriendlyName switch
    {
        "nistP256" or "ECDSA_P256" => "P-256",
        "nistP384" or "ECDSA_P384" => "P-384",
        "nistP521" or "ECDSA_P521" => "P-521",
        _ => curve.Oid.Value switch
        {
            "1.2.840.10045.3.1.7" => "P-256",
            "1.3.132.0.34" => "P-384",
            "1.3.132.0.35" => "P-521",
            _ => throw new NotSupportedException($"Unsupported curve {curve.Oid.FriendlyName}."),
        },
    };
}

/// <summary>JSON Web Key Set.</summary>
public sealed class JwkSet
{
    /// <summary>The keys in the set. Empty by default.</summary>
    [JsonPropertyName("keys")] public List<Jwk> Keys { get; set; } = new();
}

/// <summary>Base64url without padding (RFC 7515 §2).</summary>
public static class Base64Url
{
    /// <summary>Encodes bytes as base64url without padding.</summary>
    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes base64url text, with or without padding. Throws <see cref="FormatException"/> when the length is invalid.</summary>
    public static byte[] Decode(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        switch (t.Length % 4)
        {
            case 2: t += "=="; break;
            case 3: t += "="; break;
            case 1: throw new FormatException("Invalid base64url length.");
        }
        return Convert.FromBase64String(t);
    }
}
