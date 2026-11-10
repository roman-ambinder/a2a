using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace A2A.Signing;

/// <summary>Minimal public JSON Web Key (RFC 7517) for EC (P-256/384/521) and RSA keys.</summary>
public sealed class Jwk
{
    /// <summary>Key type: <c>EC</c> or <c>RSA</c>.</summary>
    [JsonPropertyName("kty")]
    public string Kty { get; set; } = string.Empty;

    /// <summary>Key id. Null when not set.</summary>
    [JsonPropertyName("kid")]
    public string? Kid { get; set; }

    /// <summary>Intended use, for example <c>sig</c>. Null when not set.</summary>
    [JsonPropertyName("use")]
    public string? Use { get; set; }

    /// <summary>Algorithm the key is for, for example <c>ES256</c>. Null when not set.</summary>
    [JsonPropertyName("alg")]
    public string? Alg { get; set; }

    /// <summary>EC curve name, for example <c>P-256</c>. Null for RSA keys.</summary>
    [JsonPropertyName("crv")]
    public string? Crv { get; set; }

    /// <summary>EC public point X coordinate (base64url). Null for RSA keys.</summary>
    [JsonPropertyName("x")]
    public string? X { get; set; }

    /// <summary>EC public point Y coordinate (base64url). Null for RSA keys.</summary>
    [JsonPropertyName("y")]
    public string? Y { get; set; }

    /// <summary>RSA modulus (base64url). Null for EC keys.</summary>
    [JsonPropertyName("n")]
    public string? N { get; set; }

    /// <summary>RSA public exponent (base64url). Null for EC keys.</summary>
    [JsonPropertyName("e")]
    public string? E { get; set; }

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
                    X = Base64Url.Encode(p.Q.X!), Y = Base64Url.Encode(p.Q.Y!)
                };
            }
            case RSA rsa:
            {
                var p = rsa.ExportParameters(false);
                return new Jwk
                {
                    Kty = "RSA", Kid = kid, Use = "sig", Alg = alg, N = Base64Url.Encode(p.Modulus!),
                    E = Base64Url.Encode(p.Exponent!)
                };
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
                    _ => throw new NotSupportedException($"Unsupported curve '{Crv}'.")
                };
                return ECDsa.Create(new ECParameters
                {
                    Curve = curve,
                    Q = new ECPoint
                    {
                        X = Base64Url.Decode(X ?? throw new FormatException("Missing x")),
                        Y = Base64Url.Decode(Y ?? throw new FormatException("Missing y"))
                    }
                });
            case "RSA":
                var rsa = CreateRsa();
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = Base64Url.Decode(N ?? throw new FormatException("Missing n")),
                    Exponent = Base64Url.Decode(E ?? throw new FormatException("Missing e"))
                });
                return rsa;
            default:
                throw new NotSupportedException($"Unsupported key type '{Kty}'.");
        }
    }

    private static RSA CreateRsa()
    {
#if NETSTANDARD2_0
        // On .NET Framework RSA.Create() returns RSACryptoServiceProvider, which cannot do PSS. RSACng can.
        // If RSACng is missing, PSS verification fails closed (JwsAlgorithms.Verify catches the exception).
        if (System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.Ordinal))
        {
            try
            {
                var type =
 Type.GetType("System.Security.Cryptography.RSACng, System.Core, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                if (type is not null && Activator.CreateInstance(type) is RSA cng) return cng;
            }
            catch (Exception ex) when (ex is TypeLoadException or MissingMethodException or System.Reflection.TargetInvocationException or CryptographicException or PlatformNotSupportedException)
            {
            }
        }
#endif
        return RSA.Create();
    }

    private static string CurveName(ECCurve curve)
    {
        return curve.Oid.FriendlyName switch
        {
            "nistP256" or "ECDSA_P256" => "P-256",
            "nistP384" or "ECDSA_P384" => "P-384",
            "nistP521" or "ECDSA_P521" => "P-521",
            _ => curve.Oid.Value switch
            {
                "1.2.840.10045.3.1.7" => "P-256",
                "1.3.132.0.34" => "P-384",
                "1.3.132.0.35" => "P-521",
                _ => throw new NotSupportedException($"Unsupported curve {curve.Oid.FriendlyName}.")
            }
        };
    }
}

/// <summary>JSON Web Key Set.</summary>
public sealed class JwkSet
{
    /// <summary>The keys in the set. Empty by default.</summary>
    [JsonPropertyName("keys")]
    public List<Jwk> Keys { get; set; } = new();
}

/// <summary>Base64url without padding (RFC 7515 §2).</summary>
public static class Base64Url
{
    /// <summary>Encodes bytes as base64url without padding.</summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        // Encoded once, in place: no padded string and no Replace copies.
        var bytes = new byte[EncodedLength(data.Length)];
        EncodeToUtf8(data, bytes);
        return Encoding.ASCII.GetString(bytes);
    }

    /// <summary>The length of the base64url text (no padding) of <paramref name="length" /> bytes.</summary>
    internal static int EncodedLength(int length)
    {
        return (length / 3 * 4) + ((length % 3) switch { 0 => 0, 1 => 2, _ => 3 });
    }

    /// <summary>
    ///     Writes the base64url text (no padding) of <paramref name="data" /> into <paramref name="destination" />, which
    ///     is <see cref="EncodedLength" /> long.
    /// </summary>
    internal static void EncodeToUtf8(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        var padded = (data.Length + 2) / 3 * 4;
        var length = EncodedLength(data.Length);
        var scratch = padded <= 256 ? stackalloc byte[padded] : new byte[padded];
        Base64.EncodeToUtf8(data, scratch, out _, out _);
        for (var i = 0; i < length; i++)
        {
            var c = scratch[i];
            destination[i] = c == (byte)'+' ? (byte)'-' : c == (byte)'/' ? (byte)'_' : c;
        }
    }

    /// <summary>
    ///     Decodes base64url text, with or without padding. Throws <see cref="FormatException" /> when the length is
    ///     invalid.
    /// </summary>
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