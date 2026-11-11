using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace A2A.Client.Identity.Aws;

/// <summary>
///     The SigV4 algorithm on plain values: canonical request, string to sign, signing key and signature. No I/O, so
///     tests can pin it to reference signatures.
/// </summary>
internal static class SigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";
    public const string DateFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>SHA-256 of an empty body, in lower-case hex.</summary>
    public static readonly string EmptyPayloadHash = Hex(SHA256.HashData([]));

    /// <summary>
    ///     Returns the <c>Authorization</c> header value. <paramref name="headers" /> are the headers to sign, with
    ///     lower-case names; their values are trimmed here.
    /// </summary>
    public static string Authorization(string method, Uri uri, IEnumerable<KeyValuePair<string, string>> headers,
        string payloadHash, string accessKey, string secretKey, string region, string service, DateTimeOffset when)
    {
        var amzDate = when.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture);
        var day = amzDate[..8];
        var sorted = headers
            .GroupBy(h => h.Key, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Value: string.Join(',', g.Select(h => Trim(h.Value)))))
            .ToList();
        var signedHeaders = string.Join(';', sorted.Select(h => h.Name));

        var canonical = new StringBuilder()
            .Append(method.ToUpperInvariant()).Append('\n')
            .Append(CanonicalPath(uri.AbsolutePath, !string.Equals(service, "s3", StringComparison.Ordinal))).Append('\n')
            .Append(CanonicalQuery(uri.Query)).Append('\n');
        foreach (var (name, value) in sorted) canonical.Append(name).Append(':').Append(value).Append('\n');
        canonical.Append('\n').Append(signedHeaders).Append('\n').Append(payloadHash);

        var scope = $"{day}/{region}/{service}/aws4_request";
        var stringToSign = $"{Algorithm}\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))}";

        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), day);
        key = Hmac(key, region);
        key = Hmac(key, service);
        key = Hmac(key, "aws4_request");
        var signature = Hex(Hmac(key, stringToSign));
        return $"{Algorithm} Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    /// <summary>
    ///     The path as sent, URI-encoded again (every service but S3 encodes it twice). An empty path is <c>/</c>.
    /// </summary>
    public static string CanonicalPath(string escapedPath, bool encodeAgain)
    {
        if (escapedPath.Length == 0) return "/";
        return encodeAgain ? Encode(escapedPath, keepSlash: true) : escapedPath;
    }

    /// <summary>
    ///     Query names and values decoded, encoded with the SigV4 rules, and sorted by name, then value.
    ///     <paramref name="query" /> may start with <c>?</c>.
    /// </summary>
    public static string CanonicalQuery(string query)
    {
        if (query.StartsWith('?')) query = query[1..];
        if (query.Length == 0) return "";
        var pairs = new List<(string Name, string Value)>();
        foreach (var part in query.Split('&'))
        {
            if (part.Length == 0) continue;
            var eq = part.IndexOf('=');
            var name = eq < 0 ? part : part[..eq];
            var value = eq < 0 ? "" : part[(eq + 1)..];
            pairs.Add((Encode(Uri.UnescapeDataString(name), keepSlash: false),
                Encode(Uri.UnescapeDataString(value), keepSlash: false)));
        }

        return string.Join('&', pairs
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => p.Name + "=" + p.Value));
    }

    /// <summary>
    ///     SigV4 URI encoding: <c>A-Z a-z 0-9 - _ . ~</c> are kept, every other UTF-8 byte becomes <c>%XX</c> in upper
    ///     case. <c>/</c> is kept when <paramref name="keepSlash" /> is true.
    /// </summary>
    public static string Encode(string value, bool keepSlash)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~'
                || (keepSlash && c == '/'))
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>Trims the value and collapses each run of spaces inside it to one space.</summary>
    public static string Trim(string value)
    {
        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value.Trim())
        {
            if (c == ' ')
            {
                if (!space) sb.Append(c);
                space = true;
            }
            else
            {
                sb.Append(c);
                space = false;
            }
        }

        return sb.ToString();
    }

    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexStringLower(bytes);
    }

    private static byte[] Hmac(byte[] key, string data)
    {
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
    }
}
