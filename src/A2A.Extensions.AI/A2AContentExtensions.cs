using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace A2A.Extensions.AI;

/// <summary>
/// Conversions between A2A messages, artifacts and parts, and <c>Microsoft.Extensions.AI</c> chat messages and content.
/// </summary>
/// <remarks>
/// Some content has no A2A form (function calls and results, reasoning, errors, usage, hosted files): <see cref="ToPart"/>
/// returns null for it and the message conversions drop it. Text media type and file name are lost through
/// <see cref="TextContent"/>. JSON in a data part is re-encoded: the bytes may differ, the meaning does not. Metadata values
/// that are not JSON elements, strings, booleans or numbers are dropped.
/// </remarks>
public static class A2AContentExtensions
{
    private const string OctetStream = "application/octet-stream";
    private const string Json = "application/json";

    /// <summary>Converts a part to AI content. Never returns null. An unusable media type becomes <c>application/octet-stream</c>; a URL part with a relative URL becomes plain <see cref="AIContent"/> that <see cref="ToPart"/> turns into null.</summary>
    public static AIContent ToAIContent(this Part part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var content = Convert(part, forModel: false, allowUrls: true) ?? new AIContent();
        content.RawRepresentation = part;
        if (part.Metadata is { Count: > 0 } metadata)
        {
            content.AdditionalProperties = [];
            foreach (var (key, value) in metadata) content.AdditionalProperties[key] = value;
        }
        return content;
    }

    /// <summary>Converts AI content to a part, or returns null when A2A has no form for it. Always built from the content, never from <see cref="AIContent.RawRepresentation"/>.</summary>
    public static Part? ToPart(this AIContent content) => ConvertToPart(content, copyMetadata: true);

    /// <summary>Converts an A2A message to a chat message. <c>Agent</c> becomes <c>Assistant</c>, every other role becomes <c>User</c>.</summary>
    public static ChatMessage ToChatMessage(this Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new ChatMessage(message.Role == Role.Agent ? ChatRole.Assistant : ChatRole.User, ToContents(message.Parts))
        {
            MessageId = message.MessageId,
            AdditionalProperties = ToProperties(message.Metadata),
        };
    }

    /// <summary>
    /// Converts a chat message to an A2A message with a new id when the chat message has none. <c>Assistant</c> becomes
    /// <c>Agent</c>; every other role becomes <c>User</c>. Content without an A2A form is dropped.
    /// </summary>
    public static Message ToA2AMessage(this ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new Message
        {
            MessageId = string.IsNullOrEmpty(message.MessageId) ? Message.NewId() : message.MessageId,
            Role = message.Role == ChatRole.Assistant ? Role.Agent : Role.User,
            Parts = ToParts(message.Contents),
            Metadata = ToMetadata(message.AdditionalProperties),
        };
    }

    /// <summary>Converts an artifact to an assistant chat message. The message id is the artifact id.</summary>
    public static ChatMessage ToChatMessage(this Artifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return new ChatMessage(ChatRole.Assistant, ToContents(artifact.Parts))
        {
            MessageId = artifact.ArtifactId,
            AuthorName = artifact.Name,
            AdditionalProperties = ToProperties(artifact.Metadata),
        };
    }

    /// <summary>Converts a chat message to an artifact. <paramref name="artifactId"/> defaults to a new GUID, <paramref name="name"/> to null.</summary>
    public static Artifact ToA2AArtifact(this ChatMessage message, string? artifactId = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new Artifact
        {
            ArtifactId = string.IsNullOrEmpty(artifactId) ? Guid.NewGuid().ToString() : artifactId,
            Name = name,
            Parts = ToParts(message.Contents),
            Metadata = ToMetadata(message.AdditionalProperties),
        };
    }

    // ---- internal: used by the executor and the client facade ----

    /// <summary>Content for the model: data parts become text, metadata is not copied, URL parts only when allowed. Null when dropped.</summary>
    internal static AIContent? ToModelContent(Part part, bool allowUrls) => Convert(part, forModel: true, allowUrls);

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
                    ? new UrlPart(url) { MediaType = string.IsNullOrWhiteSpace(uri.MediaType) ? OctetStream : uri.MediaType, Metadata = metadata }
                    : null;
            default:
                return null;
        }
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

    private static AIContent? Convert(Part part, bool forModel, bool allowUrls)
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

    private static List<AIContent> ToContents(List<Part> parts)
    {
        var list = new List<AIContent>(parts.Count);
        foreach (var part in parts) list.Add(part.ToAIContent());
        return list;
    }

    private static List<Part> ToParts(IList<AIContent> contents)
    {
        var list = new List<Part>(contents.Count);
        foreach (var content in contents)
            if (content.ToPart() is { } part) list.Add(part);
        return list;
    }

    private static bool IsJson([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType)) return false;
        var semicolon = mediaType.IndexOf(';');
        var essence = (semicolon < 0 ? mediaType : mediaType[..semicolon]).Trim();
        return essence.Equals(Json, StringComparison.OrdinalIgnoreCase) || essence.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] WriteJson(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) element.WriteTo(writer);
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

    private static AdditionalPropertiesDictionary? ToProperties(Dictionary<string, JsonElement>? metadata)
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
            if (TryToJson(value, out var element)) (result ??= new())[key] = element;
        return result;
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
