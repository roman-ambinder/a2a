using System.Text.Json;
using System.Text.Json.Serialization;

namespace A2A.Json;

/// <summary>
/// Strict converter for <see cref="Part"/>. Reads by detecting which content member is present; zero or more than one is a
/// <see cref="JsonException"/>. Uses only reader/writer primitives, so it works with source-generated contexts.
/// </summary>
public sealed class PartConverter : JsonConverter<Part>
{
    public override Part Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Part must be a JSON object");

        string? text = null, url = null, filename = null, mediaType = null;
        byte[]? raw = null;
        JsonElement? data = null;
        Dictionary<string, JsonElement>? metadata = null;
        var found = new List<string>(2);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            var name = reader.GetString()!;
            reader.Read();
            var isNull = reader.TokenType == JsonTokenType.Null;
            switch (name)
            {
                case "text":
                    if (isNull) break;
                    text = ReadString(ref reader, name); found.Add(name); break;
                case "raw":
                    if (isNull) break;
                    var b64 = ReadString(ref reader, name);
                    try { raw = Convert.FromBase64String(b64); }
                    catch (FormatException) { throw new JsonException("Part raw must be valid base64"); }
                    found.Add(name); break;
                case "url":
                    if (isNull) break;
                    url = ReadString(ref reader, name); found.Add(name); break;
                case "data":
                    if (isNull) break;
                    data = JsonElement.ParseValue(ref reader); found.Add(name); break;
                case "metadata":
                    if (isNull) break;
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
                    if (isNull) break;
                    filename = ReadString(ref reader, name); break;
                case "mediaType":
                    if (isNull) break;
                    mediaType = ReadString(ref reader, name); break;
                default:
                    reader.Skip(); break;
            }
        }

        if (found.Count == 0)
            throw new JsonException("Part must set exactly one of text, raw, url, data; found none");
        if (found.Count > 1)
            throw new JsonException($"Part must set exactly one of text, raw, url, data; found: {string.Join(", ", found)}");

        Part part = found[0] switch
        {
            "text" => new TextPart(text!),
            "raw" => new RawPart(raw!),
            "url" => new UrlPart(Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri)
                ? uri : throw new JsonException("Part url is not a valid URI")),
            _ => new DataPart(data!.Value),
        };
        return part with { Metadata = metadata, Filename = filename, MediaType = mediaType };
    }

    public override void Write(Utf8JsonWriter writer, Part value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case TextPart t: writer.WriteString("text", t.Text); break;
            case RawPart r: writer.WriteBase64String("raw", r.Raw.Span); break;
            case UrlPart u: writer.WriteString("url", u.Url.OriginalString); break;
            case DataPart d: writer.WritePropertyName("data"); d.Data.WriteTo(writer); break;
            default: throw new JsonException($"Unknown Part type {value.GetType().Name}");
        }
        if (value.Metadata is { } metadata)
        {
            writer.WriteStartObject("metadata");
            foreach (var (k, v) in metadata) { writer.WritePropertyName(k); v.WriteTo(writer); }
            writer.WriteEndObject();
        }
        if (value.Filename is not null) writer.WriteString("filename", value.Filename);
        if (value.MediaType is not null) writer.WriteString("mediaType", value.MediaType);
        writer.WriteEndObject();
    }

    private static string ReadString(ref Utf8JsonReader reader, string member) =>
        reader.TokenType == JsonTokenType.String
            ? reader.GetString()!
            : throw new JsonException($"Part {member} must be a string");
}
