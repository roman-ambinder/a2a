using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using A2A.Json;

namespace A2A.Signing;

/// <summary>A private key used to sign Agent Cards.</summary>
public sealed class AgentCardSigningKey
{
    public AgentCardSigningKey(AsymmetricAlgorithm key, string keyId, string algorithm, string? jwksUrl = null)
    {
        if (!JwsAlgorithms.IsSupported(algorithm)) throw new ArgumentException($"Unsupported JWS algorithm '{algorithm}'.", nameof(algorithm));
        Key = key;
        KeyId = string.IsNullOrWhiteSpace(keyId) ? throw new ArgumentException("Key id is required.", nameof(keyId)) : keyId;
        Algorithm = algorithm;
        JwksUrl = jwksUrl;
    }

    public AsymmetricAlgorithm Key { get; }
    public string KeyId { get; }
    public string Algorithm { get; }

    /// <summary>Optional <c>jku</c> header: HTTPS URL of a JWKS holding the public key.</summary>
    public string? JwksUrl { get; }

    public Jwk ToPublicJwk() => Jwk.FromPublicKey(Key, KeyId, Algorithm);
}

/// <summary>Signs Agent Cards with JWS (spec §8.4.2).</summary>
public static class AgentCardSigner
{
    /// <summary>Returns a copy of <paramref name="card"/> with a new signature appended.</summary>
    public static AgentCard Sign(AgentCard card, AgentCardSigningKey key)
    {
        var signed = A2AJson.Clone(card);
        var signature = CreateSignature(card, key);
        signed.Signatures = [.. card.Signatures ?? [], signature];
        return signed;
    }

    /// <summary>Creates a detached JWS signature over <see cref="AgentCardCanonicalizer.Canonicalize"/> of <paramref name="card"/>.</summary>
    /// <remarks>
    /// Values the canonicalization prunes (see <see cref="AgentCardCanonicalizer.PrunedPaths"/>) are signed around, not
    /// refused: they stay in the served card but the signature does not cover them.
    /// </remarks>
    public static AgentCardSignature CreateSignature(AgentCard card, AgentCardSigningKey key)
    {
        var payload = AgentCardCanonicalizer.Canonicalize(AgentCardCanonicalizer.ToNode(card));
        var header = new Dictionary<string, string> { ["alg"] = key.Algorithm, ["typ"] = "JOSE", ["kid"] = key.KeyId };
        if (key.JwksUrl is not null) header["jku"] = key.JwksUrl;
        var protectedB64 = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(header));
        var signingInput = Encoding.ASCII.GetBytes(protectedB64 + "." + Base64Url.Encode(payload));
        var sig = JwsAlgorithms.Sign(key.Algorithm, key.Key, signingInput);
        return new AgentCardSignature { Protected = protectedB64, Signature = Base64Url.Encode(sig) };
    }
}

/// <summary>Supported JWS algorithms. Symmetric (HS*) and "none" are deliberately excluded.</summary>
public static class JwsAlgorithms
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "ES256", "ES384", "ES512", "RS256", "RS384", "RS512", "PS256", "PS384", "PS512",
    };

    public static bool IsSupported(string? alg) => alg is not null && All.Contains(alg);

    public static HashAlgorithmName Hash(string alg) => alg[2..] switch
    {
        "256" => HashAlgorithmName.SHA256,
        "384" => HashAlgorithmName.SHA384,
        "512" => HashAlgorithmName.SHA512,
        _ => throw new NotSupportedException(alg),
    };

    public static byte[] Sign(string alg, AsymmetricAlgorithm key, byte[] data) => (alg[0], key) switch
    {
        ('E', ECDsa ec) => ec.SignData(data, Hash(alg), DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
        ('R', RSA rsa) => rsa.SignData(data, Hash(alg), RSASignaturePadding.Pkcs1),
        ('P', RSA rsa) => rsa.SignData(data, Hash(alg), RSASignaturePadding.Pss),
        _ => throw new CryptographicException($"Key type {key.GetType().Name} does not match algorithm {alg}."),
    };

    public static bool Verify(string alg, AsymmetricAlgorithm key, byte[] data, byte[] signature)
    {
        try
        {
            return (alg[0], key) switch
            {
                ('E', ECDsa ec) => ec.VerifyData(data, signature, Hash(alg), DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
                ('R', RSA rsa) => rsa.VerifyData(data, signature, Hash(alg), RSASignaturePadding.Pkcs1),
                ('P', RSA rsa) => rsa.VerifyData(data, signature, Hash(alg), RSASignaturePadding.Pss),
                _ => false,
            };
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Checks that the key matches the algorithm family and curve.</summary>
    public static bool KeyMatches(string alg, Jwk jwk) => alg[0] switch
    {
        'E' => jwk.Kty == "EC" && jwk.Crv == alg switch { "ES256" => "P-256", "ES384" => "P-384", _ => "P-521" },
        'R' or 'P' => jwk.Kty == "RSA",
        _ => false,
    };
}
