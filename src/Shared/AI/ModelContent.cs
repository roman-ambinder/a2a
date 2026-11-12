// Shared source, compiled into A2A.Extensions.AI and A2A.Extensions.AgentFramework (linked, not referenced: shipped
// packages do not use InternalsVisibleTo). The namespace differs per assembly so the two internal copies never share a
// full name. All types are internal.

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

#if A2A_AGENT_FRAMEWORK
namespace A2A.Extensions.AgentFramework.Shared;
#else
namespace A2A.Extensions.AI.Shared;
#endif

/// <summary>
///     The conversions behind <c>A2AContentExtensions</c>, and the variants the executors and the client facade use:
///     content for a model, and parts without metadata.
/// </summary>
internal static class ModelContent
{
    private const string OctetStream = "application/octet-stream";
    private const string Json = "application/json";

    /// <summary>
    ///     Content for the model: data parts become text, metadata is not copied, URL parts only when allowed. Null when
    ///     dropped.
    /// </summary>
    internal static AIContent? ToModelContent(Part part, bool allowUrls)
    {
        return Convert(part, true, allowUrls);
    }

    internal static Part? ConvertToPart(AIContent content, bool copyMetadata)
    {
        ArgumentNullException.ThrowIfNull(content);
        var metadata = copyMetadata ? ToMetadata(content.AdditionalProperties) : null;
        switch (content)
        {
            case TextContent text:
                return new TextPart(text.Text ?? string.Empty) { Metadata = metadata };
            case DataContent data:
                if (!TryGetData(data, out var bytes)) return null;
                var mediaType = string.IsNullOrWhiteSpace(data.MediaType) ? OctetStream : data.MediaType;
                if (IsJson(mediaType) && TryParseJson(bytes, out var element))
                    return new DataPart(element) { MediaType = mediaType, Filename = data.Name, Metadata = metadata };
                return new RawPart(bytes) { MediaType = mediaType, Filename = data.Name, Metadata = metadata };
            case UriContent uri:
                return uri.Uri is { IsAbsoluteUri: true } url
                    ? new UrlPart(url)
                    {
                        MediaType = string.IsNullOrWhiteSpace(uri.MediaType) ? OctetStream : uri.MediaType,
                        Metadata = metadata
                    }
                    : null;
            default:
                return null;
        }
    }

    /// <summary>
    ///     Converts a part. With <paramref name="forModel" />, data parts become text and URL parts need
    ///     <paramref name="allowUrls" />. Null when the part is dropped.
    /// </summary>
    internal static AIContent? Convert(Part part, bool forModel, bool allowUrls)
    {
        switch (part)
        {
            case TextPart text:
                return new TextContent(text.Text);
            case RawPart raw:
                return NewData(raw.Raw, raw.MediaType, raw.Filename);
            case DataPart data:
                if (forModel) return new TextContent(data.Data.GetRawText());
                var bytes = WriteJson(data.Data);
                var mediaType = IsJson(data.MediaType) ? data.MediaType : Json;
                return NewData(bytes, mediaType, data.Filename);
            case UrlPart url:
                if (forModel && !allowUrls) return null;
                if (!url.Url.IsAbsoluteUri) return forModel ? null : new AIContent();
                return NewUri(url.Url, url.MediaType);
            default:
                return null;
        }
    }

    internal static AdditionalPropertiesDictionary? ToProperties(Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is not { Count: > 0 }) return null;
        var properties = new AdditionalPropertiesDictionary();
        foreach (var (key, value) in metadata) properties[key] = value;
        return properties;
    }

    internal static Dictionary<string, JsonElement>? ToMetadata(IDictionary<string, object?>? properties)
    {
        if (properties is not { Count: > 0 }) return null;
        Dictionary<string, JsonElement>? result = null;
        foreach (var (key, value) in properties)
            if (TryToJson(value, out var element))
                (result ??= new Dictionary<string, JsonElement>())[key] = element;
        return result;
    }

    private static bool TryGetData(DataContent data, out ReadOnlyMemory<byte> bytes)
    {
        try
        {
            bytes = data.Data;
            return true;
        }
        catch (InvalidOperationException)
        {
            bytes = default;
            return false;
        }
    }

    private static DataContent NewData(ReadOnlyMemory<byte> bytes, string? mediaType, string? name)
    {
        DataContent content;
        try
        {
            content = new DataContent(bytes, string.IsNullOrWhiteSpace(mediaType) ? OctetStream : mediaType);
        }
        catch (ArgumentException)
        {
            // The media type comes from a remote peer and can be malformed.
            content = new DataContent(bytes, OctetStream);
        }

        content.Name = name;
        return content;
    }

    private static UriContent NewUri(Uri uri, string? mediaType)
    {
        try
        {
            return new UriContent(uri, string.IsNullOrWhiteSpace(mediaType) ? OctetStream : mediaType);
        }
        catch (ArgumentException)
        {
            return new UriContent(uri, OctetStream);
        }
    }

    private static bool IsJson([NotNullWhen(true)] string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType)) return false;
        var semicolon = mediaType.IndexOf(';');
        var essence = (semicolon < 0 ? mediaType : mediaType[..semicolon]).Trim();
        return essence.Equals(Json, StringComparison.OrdinalIgnoreCase) ||
               essence.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] WriteJson(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            element.WriteTo(writer);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static bool TryParseJson(ReadOnlyMemory<byte> bytes, out JsonElement element)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    private static bool TryToJson(object? value, out JsonElement element)
    {
        element = default;
        switch (value)
        {
            case JsonElement json when json.ValueKind != JsonValueKind.Undefined:
                element = json.Clone();
                return true;
            case string or bool or sbyte or byte or short or ushort or int or uint or long or ulong or decimal:
            case float f when float.IsFinite(f):
            case double d when double.IsFinite(d):
                break;
            default:
                return false;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            switch (value)
            {
                case string s: writer.WriteStringValue(s); break;
                case bool b: writer.WriteBooleanValue(b); break;
                case decimal m: writer.WriteNumberValue(m); break;
                case float f: writer.WriteNumberValue(f); break;
                case double d: writer.WriteNumberValue(d); break;
                case ulong u: writer.WriteNumberValue(u); break;
                default: writer.WriteNumberValue(System.Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            }
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        element = document.RootElement.Clone();
        return true;
    }
}
