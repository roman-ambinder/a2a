using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Json;

namespace A2A.V0_3Compat;

/// <summary>
/// Small helpers for reading and writing <see cref="JsonNode"/> trees. Readers throw <see cref="JsonException"/> with a short
/// message: a server turns it into InvalidParams, a client into an invalid-response error.
/// </summary>
internal static class V03Json
{
    /// <summary>Parses UTF-8 JSON into a fully materialized tree (a duplicate member is a <see cref="JsonException"/>, also on .NET 8).</summary>
    public static JsonNode Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var node = JsonNode.Parse(utf8, documentOptions: new JsonDocumentOptions { MaxDepth = A2AJson.MaxJsonDepth })
                ?? throw new JsonException("JSON value must not be null");
            Materialize(node);
            return node;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    private static void Materialize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, value) in obj) Materialize(value);
                break;
            case JsonArray array:
                foreach (var item in array) Materialize(item);
                break;
        }
    }

    /// <summary>UTF-8 bytes of <paramref name="node"/>, with the same escaping as the SDK's own JSON.</summary>
    public static byte[] ToUtf8(JsonNode node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            node.WriteTo(writer);
        return buffer.WrittenSpan.ToArray();
    }

    public static JsonElement ToElement(JsonNode node)
    {
        using var doc = JsonDocument.Parse(ToUtf8(node), new JsonDocumentOptions { MaxDepth = A2AJson.MaxJsonDepth });
        return doc.RootElement.Clone();
    }

    public static JsonNode ToNode(JsonElement element) =>
        JsonNode.Parse(element.GetRawText(), documentOptions: new JsonDocumentOptions { MaxDepth = A2AJson.MaxJsonDepth })!;

    // ---- readers ----

    public static JsonObject Obj(JsonNode? node, string what) =>
        node as JsonObject ?? throw new JsonException($"{what} must be a JSON object");

    public static JsonObject? OptObj(JsonObject o, string name) => o[name] switch
    {
        null => null,
        JsonObject obj => obj,
        _ when o[name]!.GetValueKind() == JsonValueKind.Null => null,
        _ => throw new JsonException($"{name} must be a JSON object"),
    };

    public static string? Str(JsonObject o, string name)
    {
        if (o[name] is not { } node || node.GetValueKind() == JsonValueKind.Null) return null;
        return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new JsonException($"{name} must be a string");
    }

    public static string ReqStr(JsonObject o, string name) =>
        Str(o, name) is { } s ? s : throw new JsonException($"{name} is required");

    public static bool? Bool(JsonObject o, string name)
    {
        if (o[name] is not { } node || node.GetValueKind() == JsonValueKind.Null) return null;
        return node.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new JsonException($"{name} must be true or false"),
        };
    }

    public static int? Int(JsonObject o, string name)
    {
        if (o[name] is not { } node || node.GetValueKind() == JsonValueKind.Null) return null;
        return node is JsonValue v && v.TryGetValue<int>(out var i) ? i : throw new JsonException($"{name} must be an integer");
    }

    public static JsonArray? Arr(JsonObject o, string name)
    {
        if (o[name] is not { } node || node.GetValueKind() == JsonValueKind.Null) return null;
        return node as JsonArray ?? throw new JsonException($"{name} must be an array");
    }

    public static List<string>? StrList(JsonObject o, string name)
    {
        if (Arr(o, name) is not { } array) return null;
        var list = new List<string>(array.Count);
        foreach (var item in array)
            list.Add(item is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new JsonException($"{name} must be an array of strings"));
        return list;
    }

    public static List<T>? List<T>(JsonObject o, string name, Func<JsonNode?, T> read)
    {
        if (Arr(o, name) is not { } array) return null;
        var list = new List<T>(array.Count);
        foreach (var item in array) list.Add(read(item));
        return list;
    }

    public static Dictionary<string, JsonElement>? Metadata(JsonObject o, string name = "metadata")
    {
        if (OptObj(o, name) is not { } obj) return null;
        var map = new Dictionary<string, JsonElement>(obj.Count, StringComparer.Ordinal);
        foreach (var (key, value) in obj) map[key] = value is null ? default : ToElement(value);
        return map;
    }

    public static DateTimeOffset? Timestamp(JsonObject o, string name)
    {
        if (Str(o, name) is not { } s) return null;
        return DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)
            ? t.ToUniversalTime()
            : throw new JsonException($"{name} is not a valid ISO 8601 timestamp");
    }

    // ---- writers ----

    public static void Set(JsonObject o, string name, string? value)
    {
        if (value is not null) o[name] = value;
    }

    public static void SetMetadata(JsonObject o, Dictionary<string, JsonElement>? metadata, string name = "metadata")
    {
        if (metadata is null) return;
        var obj = new JsonObject();
        foreach (var (key, value) in metadata) obj[key] = ToNode(value);
        o[name] = obj;
    }

    public static JsonArray StrArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var v in values) Append(array, JsonValue.Create(v));
        return array;
    }

    /// <summary>Adds a node to an array. Avoids the generic <c>JsonArray.Add&lt;T&gt;</c>, which is not trim and AOT safe.</summary>
    public static void Append(JsonArray array, JsonNode? node) => array.Add(node);

    public static string FormatTimestamp(DateTimeOffset t) => TimestampConverter.FormatUtc(t);
}
