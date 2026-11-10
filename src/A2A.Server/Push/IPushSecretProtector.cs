using System.Security.Cryptography;
using System.Text;
using A2A.Signing;

namespace A2A.Server.Push;

/// <summary>
///     Protects push config secrets (token, credentials) before they reach the store. Unprotect runs only at delivery
///     time.
/// </summary>
public interface IPushSecretProtector
{
    /// <summary>Encrypts a secret for storage and returns the protected text.</summary>
    string Protect(string plaintext);

    /// <exception cref="CryptographicException">The value was not produced by this protector.</exception>
    string Unprotect(string protectedValue);
}

/// <summary>
///     Default protector: AES-256-GCM with a random key generated per process. Values do not survive a restart,
///     so use a durable store only together with a protector whose keys are persisted (for example
///     <c>AddA2ADataProtectionPushSecrets</c> in A2A.Server.AspNetCore).
///     Format: <c>e1:</c> + base64url(nonce(12) || ciphertext || tag(16)).
/// </summary>
public sealed class EphemeralPushSecretProtector : IPushSecretProtector
{
    private const string Prefix = "e1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    // AesGcm instances are not safe for concurrent use, so each call creates its own from the key.
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var buffer = new byte[NonceSize + data.Length + TagSize];
        var nonce = buffer.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, data, buffer.AsSpan(NonceSize, data.Length),
            buffer.AsSpan(NonceSize + data.Length, TagSize));
        return Prefix + Base64Url.Encode(buffer);
    }

    /// <inheritdoc />
    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Unrecognized protected value.");
        byte[] buffer;
        try
        {
            buffer = Base64Url.Decode(protectedValue[Prefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Malformed protected value.", ex);
        }

        if (buffer.Length < NonceSize + TagSize) throw new CryptographicException("Malformed protected value.");
        var length = buffer.Length - NonceSize - TagSize;
        var plain = new byte[length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(buffer.AsSpan(0, NonceSize), buffer.AsSpan(NonceSize, length),
            buffer.AsSpan(NonceSize + length, TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }
}