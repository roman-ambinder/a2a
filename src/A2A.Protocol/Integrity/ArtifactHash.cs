using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Signing;

namespace A2A.Integrity;

/// <summary>
/// The optional integrity hash of an artifact. It detects corruption and bad reassembly of chunks (lost, repeated or reordered).
/// It does not prove who wrote the content: anyone who can change the content can change an unsigned hash.
/// </summary>
/// <remarks>
/// The hash is SHA-256 over the content bytes of the parts, in order, with nothing between them. A text part is its UTF-8 text.
/// A raw part is its decoded bytes. A URL part is the UTF-8 URL string as sent (the content behind the URL is not fetched). A data
/// part is the RFC 8785 form of its JSON. Part metadata, file name and media type, and the artifact's own fields are not hashed.
/// The value is <c>sha256:</c> and 64 lowercase hex digits.
/// </remarks>
[global::System.Diagnostics.CodeAnalysis.Experimental("A2AEXP003", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public static class ArtifactHash
{
    /// <summary>The artifact metadata key that holds the hash of the artifact so far: <c>sha256</c>.</summary>
    public const string MetadataKey = "sha256";

    private const string Prefix = "sha256:";

    // Deeper than the server accepts, so a data part the server stored can always be hashed.
    private static readonly JsonDocumentOptions DataOptions = new() { MaxDepth = 1024 };

    /// <summary>Returns the hash value of <paramref name="parts"/>, as <see cref="Format"/> writes it.</summary>
    /// <param name="parts">The parts, in order.</param>
    /// <exception cref="ArgumentException">A part is null.</exception>
    /// <exception cref="InvalidOperationException">A data part cannot be put in canonical JSON form, for example a number outside the double range.</exception>
    public static string Compute(IEnumerable<Part> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Update(hash, parts);
        return Format(hash.GetHashAndReset());
    }

    /// <summary>
    /// Adds the content bytes of <paramref name="parts"/> to <paramref name="hash"/>. When it throws, nothing was added.
    /// </summary>
    /// <param name="hash">A SHA-256 hash.</param>
    /// <param name="parts">The parts, in order.</param>
    /// <exception cref="ArgumentException">A part is null.</exception>
    /// <exception cref="InvalidOperationException">A data part cannot be put in canonical JSON form, for example a number outside the double range.</exception>
    public static void Update(IncrementalHash hash, IEnumerable<Part> parts)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(parts);
        // Every part is converted first, so a part that cannot be hashed leaves the hash as it was.
        var chunks = new List<ReadOnlyMemory<byte>>();
        foreach (var part in parts) chunks.Add(Bytes(part));
        foreach (var chunk in chunks) hash.AppendData(chunk.Span);
    }

    /// <summary>Formats a SHA-256 digest as <c>sha256:</c> followed by 64 lowercase hex digits.</summary>
    /// <param name="digest">The 32 bytes of the digest.</param>
    /// <exception cref="ArgumentException"><paramref name="digest"/> is not 32 bytes long.</exception>
    public static string Format(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != SHA256.HashSizeInBytes) throw new ArgumentException("A SHA-256 digest is 32 bytes.", nameof(digest));
        return Prefix + Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static ReadOnlyMemory<byte> Bytes(Part? part)
    {
        switch (part)
        {
            case TextPart text:
                return Encoding.UTF8.GetBytes(text.Text);
            case RawPart raw:
                return raw.Raw;
            case UrlPart url:
                return Encoding.UTF8.GetBytes(url.Url.OriginalString);
            case DataPart data:
                try
                {
                    // Parse from text: no reflection, so this works with trimming and AOT.
                    var node = JsonNode.Parse(data.Data.GetRawText(), documentOptions: DataOptions);
                    return JsonCanonicalizer.Canonicalize(node);
                }
                catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException or OverflowException)
                {
                    throw new InvalidOperationException("A data part cannot be put in canonical JSON form: " + ex.Message, ex);
                }
            default:
                throw new ArgumentException("A part is null.", nameof(part));
        }
    }
}
