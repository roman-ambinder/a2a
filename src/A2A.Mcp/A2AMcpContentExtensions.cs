using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace A2A.Mcp;

/// <summary>Conversions between MCP content and A2A parts. Custom executors can use them.</summary>
/// <remarks>
///     Only web links (<c>http</c> and <c>https</c>) are converted. Bytes are converted whatever their size: check the
///     size first when the
///     content is untrusted. Media types that are not well formed become <c>application/octet-stream</c>.
/// </remarks>
public static class A2AMcpContentExtensions
{
    /// <summary>
    ///     Converts a content block to a part, or returns null when there is no A2A form: a link that is not <c>http</c> or
    ///     <c>https</c>,
    ///     a tool use or tool result block, or an unknown block. Text is a text part, image and audio are bytes with their
    ///     media type,
    ///     an embedded text resource is a text part with its media type, an embedded blob is bytes, and a link is a URL part.
    /// </summary>
    public static Part? ToPart(this ContentBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        switch (block)
        {
            case TextContentBlock text:
                return Part.FromText(text.Text ?? "");
            case ImageContentBlock image:
                return Part.FromBytes(image.DecodedData, A2AMcpMediaTypes.Normalize(image.MimeType));
            case AudioContentBlock audio:
                return Part.FromBytes(audio.DecodedData, A2AMcpMediaTypes.Normalize(audio.MimeType));
            case EmbeddedResourceBlock { Resource: TextResourceContents textResource }:
                return Part.FromText(textResource.Text ?? "",
                    string.IsNullOrWhiteSpace(textResource.MimeType)
                        ? null
                        : A2AMcpMediaTypes.Normalize(textResource.MimeType));
            case EmbeddedResourceBlock { Resource: BlobResourceContents blob }:
                return Part.FromBytes(blob.DecodedData, A2AMcpMediaTypes.Normalize(blob.MimeType));
            case ResourceLinkBlock link:
                if (!Uri.TryCreate(link.Uri, UriKind.Absolute, out var uri) ||
                    uri.Scheme is not ("http" or "https")) return null;
                return Part.FromUrl(uri,
                    string.IsNullOrWhiteSpace(link.MimeType) ? null : A2AMcpMediaTypes.Normalize(link.MimeType),
                    string.IsNullOrWhiteSpace(link.Name) ? null : link.Name);
            default:
                return null;
        }
    }

    /// <summary>
    ///     Converts a part to a content block, or returns null for a URL part that is not <c>http</c> or <c>https</c>. Text is
    ///     text, JSON data is its
    ///     JSON as text, bytes are an image, audio or embedded blob block (by media type), and a URL is a resource link.
    /// </summary>
    public static ContentBlock? ToContentBlock(this Part part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return part switch
        {
            TextPart text => new TextContentBlock { Text = text.Text },
            DataPart data => new TextContentBlock { Text = data.Data.GetRawText() },
            RawPart raw => RawToBlock(raw, 0),
            UrlPart url => UrlToBlock(url),
            _ => null
        };
    }

    /// <summary>
    ///     Converts a tool result to parts. Content blocks that have no A2A form are dropped. Structured content becomes one
    ///     data part; a text
    ///     block that holds the same JSON is dropped, because the server sent the object twice.
    /// </summary>
    public static IReadOnlyList<Part> ToParts(this CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var parts = new List<Part>();
        var structured = result.StructuredContent;
        var structuredAdded = false;
        if (result.Content is { } blocks)
            foreach (var block in blocks)
            {
                if (structured is { } data && block is TextContentBlock { Text: { } text } && IsSameJson(text, data))
                {
                    if (!structuredAdded) parts.Add(Part.FromData(data.Clone()));
                    structuredAdded = true;
                    continue;
                }

                if (block.ToPart() is { } part) parts.Add(part);
            }

        if (structured is { } rest && !structuredAdded) parts.Add(Part.FromData(rest.Clone()));
        return parts;
    }

    internal static ContentBlock RawToBlock(RawPart raw, int index)
    {
        var mediaType = A2AMcpMediaTypes.Normalize(raw.MediaType);
        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return ImageContentBlock.FromBytes(raw.Raw, mediaType);
        if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return AudioContentBlock.FromBytes(raw.Raw, mediaType);
        return new EmbeddedResourceBlock
            { Resource = BlobResourceContents.FromBytes(raw.Raw, $"a2a://part/{index}", mediaType) };
    }

    /// <summary>
    ///     A resource link, or null for a URL that is not a web link. Such a link would be opened on the machine of the
    ///     MCP caller.
    /// </summary>
    internal static ContentBlock? UrlToBlock(UrlPart part)
    {
        if (!part.Url.IsAbsoluteUri || part.Url.Scheme is not ("http" or "https")) return null;
        return new ResourceLinkBlock
        {
            Uri = part.Url.AbsoluteUri,
            Name = string.IsNullOrWhiteSpace(part.Filename) ? "resource" : part.Filename,
            MimeType = string.IsNullOrWhiteSpace(part.MediaType) ? null : A2AMcpMediaTypes.Normalize(part.MediaType)
        };
    }

    private static bool IsSameJson(string text, JsonElement structured)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonEquals(document.RootElement, structured);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool JsonEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var count = 0;
                foreach (var property in a.EnumerateObject())
                {
                    count++;
                    if (!b.TryGetProperty(property.Name, out var other) || !JsonEquals(property.Value, other))
                        return false;
                }

                return count == b.EnumerateObject().Count();
            case JsonValueKind.Array:
                if (a.GetArrayLength() != b.GetArrayLength()) return false;
                using (var left = a.EnumerateArray().GetEnumerator())
                using (var right = b.EnumerateArray().GetEnumerator())
                {
                    while (left.MoveNext() && right.MoveNext())
                        if (!JsonEquals(left.Current, right.Current))
                            return false;
                }

                return true;
            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return a.TryGetDecimal(out var x) && b.TryGetDecimal(out var y)
                    ? x == y
                    : a.GetDouble() == b.GetDouble();
            default:
                return true; // true, false and null: the kind says it all
        }
    }
}