using System.Text.Json;
using System.Text.Json.Serialization;

namespace A2A.Json;

/// <summary>
/// Strict converter for <see cref="Part"/>. Reads by detecting which content member is present; zero or more than one is a
/// <see cref="JsonException"/>. Uses only reader/writer primitives, so it works with source-generated contexts.
/// </summary>
public sealed class PartConverter : JsonConverter<Part>
{
    private static readonly PartConverter Shared = new();

    internal static Part ReadPart(ref Utf8JsonReader reader) => Shared.Read(ref reader, typeof(Part), null!);

    internal static void WritePart(Utf8JsonWriter writer, Part value) => Shared.Write(writer, value, null!);

    /// <summary>Reads a part from its wire object. The member that is present decides the kind.</summary>
    public override Part Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Part must be a JSON object");

        string? text = null, url = null, filename = null, mediaType = null;
        byte[]? raw = null;
        JsonElement? data = null;
        Dictionary<string, JsonElement>? metadata = null;
        string? first = null; // the first content member found
        List<string>? more = null; // content members after the first: only for the error message

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            // Matched on the reader's bytes: no string per property name.
            var name = reader.ValueTextEquals("text"u8) ? "text"
                : reader.ValueTextEquals("raw"u8) ? "raw"
                : reader.ValueTextEquals("url"u8) ? "url"
                : reader.ValueTextEquals("data"u8) ? "data"
                : reader.ValueTextEquals("metadata"u8) ? "metadata"
                : reader.ValueTextEquals("filename"u8) ? "filename"
                : reader.ValueTextEquals("mediaType"u8) ? "mediaType"
                : null;
            reader.Read();
            if (reader.TokenType == JsonTokenType.Null && name is not null) continue; // null counts as absent
            switch (name)
            {
                case "text":
                    text = ReadString(ref reader, name); Found(name); break;
                case "raw":
                    var b64 = ReadString(ref reader, name);
                    try { raw = Convert.FromBase64String(b64); }
                    catch (FormatException) { throw new JsonException("Part raw must be valid base64"); }
                    Found(name); break;
                case "url":
                    url = ReadString(ref reader, name); Found(name); break;
                case "data":
                    data = JsonElement.ParseValue(ref reader); Found(name); break;
                case "metadata":
                    if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Part metadata must be an object");
                    metadata = new Dictionary<string, JsonElement>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        var key = reader.GetString()!;
                        reader.Read();
                        metadata[key] = JsonElement.ParseValue(ref reader);
                    }
                    break;
                case "filename":
                    filename = ReadString(ref reader, name); break;
                case "mediaType":
                    mediaType = ReadString(ref reader, name); break;
                default:
                    reader.Skip(); break;
            }
        }

        if (first is null)
            throw new JsonException("Part must set exactly one of text, raw, url, data; found none");
        if (more is not null)
            throw new JsonException($"Part must set exactly one of text, raw, url, data; found: {string.Join(", ", more)}");

        // Built once, with its optional fields: no second instance from a 'with' copy.
        return first switch
        {
            "text" => new TextPart(text!) { Metadata = metadata, Filename = filename, MediaType = mediaType },
            "raw" => new RawPart(raw!) { Metadata = metadata, Filename = filename, MediaType = mediaType },
            "url" => new UrlPart(Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri)
                ? uri : throw new JsonException("Part url is not a valid URI")) { Metadata = metadata, Filename = filename, MediaType = mediaType },
            _ => new DataPart(data!.Value) { Metadata = metadata, Filename = filename, MediaType = mediaType },
        };

        void Found(string member)
        {
            if (first is null) first = member;
            else (more ??= [first]).Add(member);
        }
    }

    /// <summary>Writes a part as a wire object with exactly one content member.</summary>
    public override void Write(Utf8JsonWriter writer, Part value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case TextPart t: writer.WriteString("text"u8, t.Text); break;
            case RawPart r: writer.WriteBase64String("raw"u8, r.Raw.Span); break;
            case UrlPart u: writer.WriteString("url"u8, u.Url.OriginalString); break;
            case DataPart d: writer.WritePropertyName("data"u8); d.Data.WriteTo(writer); break;
            default: throw new JsonException($"Unknown Part type {value.GetType().Name}");
        }
        if (value.Metadata is { } metadata)
        {
            writer.WriteStartObject("metadata"u8);
            foreach (var (k, v) in metadata) { writer.WritePropertyName(k); v.WriteTo(writer); }
            writer.WriteEndObject();
        }
        if (value.Filename is not null) writer.WriteString("filename"u8, value.Filename);
        if (value.MediaType is not null) writer.WriteString("mediaType"u8, value.MediaType);
        writer.WriteEndObject();
    }

    private static string ReadString(ref Utf8JsonReader reader, string member) =>
        reader.TokenType == JsonTokenType.String
            ? reader.GetString()!
            : throw new JsonException($"Part {member} must be a string");
}

/// <summary>
/// Converter for one concrete <see cref="Part"/> subtype. System.Text.Json does not apply the base type's converter when
/// the declared type is a derived record, so each subtype names this converter. It shares the logic of <see cref="PartConverter"/>.
/// </summary>
public sealed class PartConverter<TPart> : JsonConverter<TPart> where TPart : Part
{
    /// <summary>Reads a part and requires it to be of type <typeparamref name="TPart"/>.</summary>
    public override TPart Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var part = PartConverter.ReadPart(ref reader);
        return part as TPart ?? throw new JsonException($"Part content does not match {typeof(TPart).Name}");
    }

    /// <summary>Writes a part of type <typeparamref name="TPart"/>.</summary>
    public override void Write(Utf8JsonWriter writer, TPart value, JsonSerializerOptions options) =>
        PartConverter.WritePart(writer, value);
}
