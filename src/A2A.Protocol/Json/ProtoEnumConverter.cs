using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace A2A.Json;

/// <summary>Declares the ProtoJSON wire name of an enum member (e.g. <c>TASK_STATE_WORKING</c>).</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ProtoNameAttribute(string name) : Attribute
{
    /// <summary>The ProtoJSON name written to and read from the wire.</summary>
    public string Name { get; } = name;
}

/// <summary>
///     Serializes enums using their ProtoJSON names (spec §5.5). Unknown names read as the zero value
///     (the <c>*_UNSPECIFIED</c> member) so that newer peers do not break older ones. Integer values are accepted on read.
/// </summary>
public sealed class
    ProtoEnumConverter<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> ToName = new();

    private static readonly Dictionary<string, TEnum> FromName = new(StringComparer.Ordinal);

    // The same names, encoded once: reads compare the reader's bytes and writes copy them, with no string per value.
    private static readonly Dictionary<TEnum, JsonEncodedText> ToEncoded = new();
    private static readonly (JsonEncodedText Name, TEnum Value)[] Encoded;

    static ProtoEnumConverter()
    {
        var encoded = new List<(JsonEncodedText, TEnum)>();
        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (TEnum)field.GetValue(null)!;
            var name = field.GetCustomAttribute<ProtoNameAttribute>()?.Name ?? field.Name;
            ToName[value] = name;
            FromName[name] = value;
            var text = JsonEncodedText.Encode(name);
            ToEncoded[value] = text;
            encoded.Add((text, value));
        }

        Encoded = encoded.ToArray();
    }

    /// <summary>
    ///     Returns the ProtoJSON name of <paramref name="value" />, or its C# name when it has no
    ///     <see cref="ProtoNameAttribute" />.
    /// </summary>
    public static string ToProtoName(TEnum value)
    {
        return ToName.TryGetValue(value, out var n) ? n : value.ToString();
    }

    /// <summary>Parses a ProtoJSON name. Returns false when the name is unknown.</summary>
    public static bool TryParse(string? name, out TEnum value)
    {
        if (name is not null && FromName.TryGetValue(name, out value)) return true;
        if (int.TryParse(name, out var i))
        {
            value = (TEnum)Enum.ToObject(typeof(TEnum), i);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Reads a ProtoJSON name or an integer. An unknown name reads as the zero value.</summary>
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                foreach (var (name, value) in Encoded)
                    if (reader.ValueTextEquals(name.EncodedUtf8Bytes))
                        return value;
                return default; // an unknown name
            case JsonTokenType.Number when reader.TryGetInt32(out var i):
                return (TEnum)Enum.ToObject(typeof(TEnum), i);
            default:
                throw new JsonException($"Expected a string for enum {typeof(TEnum).Name}.");
        }
    }

    /// <summary>Writes the ProtoJSON name of the value.</summary>
    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (ToEncoded.TryGetValue(value, out var encoded)) writer.WriteStringValue(encoded);
        else writer.WriteStringValue(value.ToString());
    }
}