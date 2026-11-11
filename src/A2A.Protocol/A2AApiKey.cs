using System.Security.Cryptography;
using System.Text;

namespace A2A;

/// <summary>
///     An A2A API key and its hash. A key is <c>a2a_</c> followed by 43 base64url characters (32 random bytes). The agent
///     stores only the hash (<c>sha256:</c> and 64 lowercase hex digits), so a leaked configuration file or environment
///     variable holds no usable key. The caller keeps the key.
/// </summary>
/// <remarks>
///     SHA-256 without a salt is enough because every key is 256 random bits: there is nothing to guess. A key in any other
///     form is refused (<see cref="IsWellFormed" />), so a short, guessable key cannot be hashed and registered by mistake.
/// </remarks>
public sealed class A2AApiKey
{
    /// <summary>The prefix of every key.</summary>
    public const string Prefix = "a2a_";

    /// <summary>The prefix of every hash.</summary>
    public const string HashPrefix = "sha256:";

    private const int RandomBytes = 32;
    private const int EncodedLength = 43; // base64url of 32 bytes, no padding

    private A2AApiKey(string key)
    {
        Key = key;
        Hash = ComputeHash(key);
    }

    /// <summary>The key. Give it to the caller; never store it on the agent.</summary>
    public string Key { get; }

    /// <summary>The hash of <see cref="Key" />. Store this on the agent.</summary>
    public string Hash { get; }

    /// <summary>Creates a new random key.</summary>
    public static A2AApiKey Create()
    {
#if NETSTANDARD2_0
        var bytes = new byte[RandomBytes];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
#else
        var bytes = RandomNumberGenerator.GetBytes(RandomBytes);
#endif

        return new A2AApiKey(Prefix + Base64Url(bytes));
    }

    /// <summary>True when <paramref name="key" /> has the form of an A2A API key.</summary>
    public static bool IsWellFormed(string? key)
    {
        if (key is null || key.Length != Prefix.Length + EncodedLength ||
            !key.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        for (var i = Prefix.Length; i < key.Length; i++)
        {
            var c = key[i];
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')) return false;
        }

        // The last character carries 4 bits of padding, which must be zero (one key, one spelling).
        return (Base64UrlValue(key[key.Length - 1]) & 0x3) == 0;
    }

    /// <summary>True when <paramref name="hash" /> has the form of a key hash (<c>sha256:</c> and 64 lowercase hex digits).</summary>
    public static bool IsWellFormedHash(string? hash)
    {
        if (hash is null || hash.Length != HashPrefix.Length + 64 ||
            !hash.StartsWith(HashPrefix, StringComparison.Ordinal)) return false;
        for (var i = HashPrefix.Length; i < hash.Length; i++)
            if (!(hash[i] is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    /// <summary>The hash of <paramref name="key" />.</summary>
    /// <exception cref="ArgumentException"><paramref name="key" /> is not a well-formed key (<see cref="IsWellFormed" />).</exception>
    public static string ComputeHash(string key)
    {
        if (!IsWellFormed(key))
            throw new ArgumentException(
                $"Not an A2A API key: a key is '{Prefix}' and 43 base64url characters. Make one with A2AApiKey.Create() or 'a2a keys new'.",
                nameof(key));
#if NETSTANDARD2_0
        byte[] digest;
        using (var sha = SHA256.Create())
        {
            digest = sha.ComputeHash(Encoding.ASCII.GetBytes(key));
        }
#else
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(key));
#endif

        var sb = new StringBuilder(HashPrefix, HashPrefix.Length + 64);
        foreach (var b in digest) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>Returns <see cref="Hash" />, never the key, so the key does not end up in a log by accident.</summary>
    public override string ToString()
    {
        return Hash;
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static int Base64UrlValue(char c)
    {
        return c switch
        {
            >= 'A' and <= 'Z' => c - 'A',
            >= 'a' and <= 'z' => c - 'a' + 26,
            >= '0' and <= '9' => c - '0' + 52,
            '-' => 62,
            _ => 63
        };
    }
}
