using System.Text.Json;
using System.Text.Json.Serialization;
using A2A.Json;

namespace A2A;

/// <summary>
/// A unit of content (spec §4.1.6). A closed hierarchy: exactly one of <see cref="TextPart"/>, <see cref="RawPart"/>,
/// <see cref="UrlPart"/> or <see cref="DataPart"/>. On the wire a part has exactly one of text, raw, url, data.
/// </summary>
[JsonConverter(typeof(PartConverter))]
public abstract record Part
{
    private protected Part() { }

    /// <summary>Optional extension metadata.</summary>
    public Dictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>Optional file name.</summary>
    public string? Filename { get; init; }

    /// <summary>Optional media type.</summary>
    public string? MediaType { get; init; }

    /// <summary>Creates a text part.</summary>
    public static TextPart FromText(string text, string? mediaType = null) => new(text) { MediaType = mediaType };

    /// <summary>Creates an inline bytes part (base64 on the wire).</summary>
    public static RawPart FromBytes(ReadOnlyMemory<byte> bytes, string mediaType, string? filename = null) =>
        new(bytes) { MediaType = mediaType, Filename = filename };

    /// <summary>Creates a URL part.</summary>
    public static UrlPart FromUrl(Uri url, string? mediaType = null, string? filename = null) =>
        new(url) { MediaType = mediaType, Filename = filename };

    /// <summary>Creates a structured data part. Callers serialize their own types to a <see cref="JsonElement"/> first.</summary>
    public static DataPart FromData(JsonElement data, string? mediaType = "application/json") => new(data) { MediaType = mediaType };
}

/// <summary>A text part.</summary>
public sealed record TextPart(string Text) : Part;

/// <summary>Inline bytes; base64 on the wire.</summary>
public sealed record RawPart(ReadOnlyMemory<byte> Raw) : Part;

/// <summary>A reference to content by URL.</summary>
public sealed record UrlPart(Uri Url) : Part;

/// <summary>Structured JSON data.</summary>
public sealed record DataPart(JsonElement Data) : Part;
