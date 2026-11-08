using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace A2A.Json;

/// <summary>
/// Accept-side ProtoJSON field names. ProtoJSON parsers must accept the original proto field name (<c>history_length</c>)
/// as well as the JSON name (<c>historyLength</c>). The SDK models bind only JSON names, so this rewrites proto names first.
/// </summary>
/// <remarks>
/// Keys inside free-form values are user data and are never renamed: <c>metadata</c>, a part's <c>data</c>, an
/// extension's <c>params</c>, OAuth <c>scopes</c>, and the scheme-name maps <c>securitySchemes</c> and <c>schemes</c>.
/// An object that has both spellings of one field (for example <c>historyLength</c> and <c>history_length</c>) is
/// rejected, so two parsers can never read different values from the same message.
/// </remarks>
public static class ProtoJsonFieldNames
{
    private static readonly HashSet<string> FreeForm = new(StringComparer.Ordinal)
    {
        "metadata", "data", "params", "scopes", "securitySchemes", "schemes",
    };

    /// <summary>
    /// Returns <paramref name="element"/> with proto field names rewritten to JSON names. Returns the same element,
    /// without copying, when no field name contains <c>'_'</c>.
    /// </summary>
    /// <exception cref="JsonException">An object has both the proto name and the JSON name of one field.</exception>
    public static JsonElement Normalize(JsonElement element)
    {
        if (!HasProtoNames(element)) return element;
        var node = JsonNode.Parse(element.GetRawText(), documentOptions: new JsonDocumentOptions { MaxDepth = 1024 });
        Rewrite(node);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) node!.WriteTo(writer);
        using var doc = JsonDocument.Parse(buffer.WrittenMemory, new JsonDocumentOptions { MaxDepth = 1024 });
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Deserializes a JSON value as <typeparamref name="T"/>, accepting proto field names (see <see cref="Normalize(JsonElement)"/>).
    /// A value without any property name that contains <c>'_'</c> (the usual case) binds straight from <paramref name="utf8Json"/>
    /// with no intermediate document; only a value with such a name is normalized first.
    /// </summary>
    /// <exception cref="JsonException">Malformed JSON, a value that does not bind, or both spellings of one field.</exception>
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (!MayHaveProtoNames(utf8Json)) return JsonSerializer.Deserialize(utf8Json, typeInfo);
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = 1024 });
        try { return Normalize(JsonElement.ParseValue(ref reader)).Deserialize(typeInfo); }
        catch (InvalidOperationException e) { throw new JsonException("A property name is not valid text.", e); } // a lone surrogate escape
    }

    /// <summary>
    /// False when no property name in <paramref name="utf8Json"/> contains <c>'_'</c>, so the value needs no normalization. True
    /// otherwise (also for names inside free-form values, which <see cref="Normalize(JsonElement)"/> then leaves alone).
    /// </summary>
    private static bool MayHaveProtoNames(ReadOnlySpan<byte> utf8Json)
    {
        // No underscore and no escape (which could spell one as \u005f) anywhere: nothing to normalize.
        if (utf8Json.IndexOfAny((byte)'_', (byte)'\\') < 0) return false;
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = 1024 });
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                if (reader.ValueIsEscaped ? reader.GetString()!.Contains('_', StringComparison.Ordinal) : reader.ValueSpan.IndexOf((byte)'_') >= 0)
                    return true;
            }
            return false;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return true; // let the slow path report the error as it always did
        }
    }

    /// <summary>Converts a proto field name (<c>history_length</c>) to its JSON name (<c>historyLength</c>).</summary>
    public static string ToJsonName(string protoName)
    {
        var chars = new char[protoName.Length];
        var n = 0;
        var upper = false;
        foreach (var c in protoName)
        {
            if (c == '_') { upper = n > 0; continue; }
            chars[n++] = upper ? char.ToUpperInvariant(c) : c;
            upper = false;
        }
        return new string(chars, 0, n);
    }

    private static bool HasProtoNames(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name.Contains('_')) return true;
                    if (!FreeForm.Contains(p.Name) && HasProtoNames(p.Value)) return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    if (HasProtoNames(item)) return true;
                return false;
            default:
                return false;
        }
    }

    private static void Rewrite(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj.ToList())
                {
                    var jsonName = name.Contains('_') ? ToJsonName(name) : name;
                    if (!string.Equals(jsonName, name, StringComparison.Ordinal))
                    {
                        if (obj.ContainsKey(jsonName))
                            throw new JsonException($"Both '{name}' and '{jsonName}' are present; send only one.");
                        obj.Remove(name);
                        obj[jsonName] = value;
                    }
                    if (!FreeForm.Contains(jsonName)) Rewrite(value);
                }
                break;
            case JsonArray arr:
                foreach (var item in arr) Rewrite(item);
                break;
        }
    }
}
