using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace A2A.Push;

/// <summary>
/// Signing of webhook deliveries as in the Standard Webhooks specification: the headers <c>webhook-id</c>,
/// <c>webhook-timestamp</c> (unix seconds) and <c>webhook-signature</c> (<c>v1,</c> + base64 of
/// HMAC-SHA256 over <c>{id}.{timestamp}.{body}</c>). A client turns signing on by registering a push config with
/// <c>authentication.scheme = "webhook-signature"</c> and <c>credentials = "whsec_" + base64(secret)</c> (at least 16 bytes).
/// </summary>
public static class WebhookSignature
{
    /// <summary>The <c>authentication.scheme</c> that carries a signing secret. It is never sent as an <c>Authorization</c> header.</summary>
    public const string Scheme = "webhook-signature";

    /// <summary>Prefix of the signing secret in <c>authentication.credentials</c>; the rest is the base64 of the key.</summary>
    public const string SecretPrefix = "whsec_";

    /// <summary>Header with the message id (the delivery id; the same on every retry).</summary>
    public const string IdHeader = "webhook-id";

    /// <summary>Header with the signing time in unix seconds. It changes on every attempt.</summary>
    public const string TimestampHeader = "webhook-timestamp";

    /// <summary>Header with one or more space-separated signatures, <c>v1,&lt;base64&gt;</c> each.</summary>
    public const string SignatureHeader = "webhook-signature";

    /// <summary>The only signature version.</summary>
    internal const string Version = "v1";

    /// <summary>Shortest accepted secret, in bytes.</summary>
    public const int MinSecretLength = 16;

    /// <summary>Returns <c>v1,</c> + base64(HMAC-SHA256(<paramref name="secret"/>, <c>{id}.{unixSeconds}.{body}</c>)).</summary>
    public static string Sign(ReadOnlySpan<byte> secret, string id, long unixSeconds, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(id);
        var prefix = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{id}.{unixSeconds}."));
        var content = new byte[prefix.Length + body.Length];
        prefix.CopyTo(content, 0);
        body.CopyTo(content.AsSpan(prefix.Length));
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(secret, content, mac);
        return Version + "," + Convert.ToBase64String(mac);
    }

    /// <summary>Reads <c>whsec_&lt;base64&gt;</c>. False when the prefix or the base64 is wrong or the key is shorter than <see cref="MinSecretLength"/> bytes.</summary>
    public static bool TryParseSecret(string? credentials, out byte[] secret)
    {
        secret = [];
        if (credentials is null || !credentials.StartsWith(SecretPrefix, StringComparison.Ordinal)) return false;
        var bytes = new byte[credentials.Length]; // always enough for the base64 text after the prefix
        if (!Convert.TryFromBase64String(credentials[SecretPrefix.Length..], bytes, out var written) || written < MinSecretLength) return false;
        secret = bytes.AsSpan(0, written).ToArray();
        return true;
    }

    /// <summary>
    /// True when <paramref name="signatureHeader"/> holds a <c>v1</c> signature equal to the one computed for the message.
    /// Signatures of other versions are skipped; every candidate is compared in constant time.
    /// </summary>
    internal static bool Matches(string signatureHeader, ReadOnlySpan<byte> secret, string id, long unixSeconds, ReadOnlySpan<byte> body)
    {
        var expected = Encoding.UTF8.GetBytes(Sign(secret, id, unixSeconds, body));
        var matched = false;
        foreach (var part in signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!part.StartsWith(Version + ",", StringComparison.Ordinal)) continue;
            matched |= CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(part), expected);
        }
        return matched;
    }
}
