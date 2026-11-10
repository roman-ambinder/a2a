using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace A2A.Json;

/// <summary>
///     ProtoJSON form of a 64-bit integer (<c>int64</c>): written as a decimal string (<c>"42"</c>), read from a string or
///     a number.
///     Protobuf JSON writes 64-bit values as strings because JavaScript numbers lose precision above 2^53.
/// </summary>
public sealed class ProtoInt64Converter : JsonConverter<long>
{
    /// <summary>
    ///     Reads a JSON number or a decimal string. Anything else, or a value out of range, is a
    ///     <see cref="JsonException" />.
    /// </summary>
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt64(out var n)) return n;
            throw new JsonException("The value is not a 64-bit integer.");
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var span = reader.HasValueSequence
                ? BuffersExtensions.ToArray(reader.ValueSequence)
                : reader.ValueSpan.ToArray();
            if (Utf8Parser.TryParse(span, out long value, out var consumed) && consumed == span.Length) return value;
            throw new JsonException("The value is not a 64-bit integer.");
        }

        throw new JsonException("A 64-bit integer must be a JSON number or a string.");
    }

    /// <summary>Writes the value as a decimal string.</summary>
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
    {
        Span<byte> buffer = stackalloc byte[20];
        Utf8Formatter.TryFormat(value, buffer, out var written);
        writer.WriteStringValue(buffer.Slice(0, written));
    }
}

/// <summary>
///     <see cref="ProtoInt64Converter" /> for an optional 64-bit integer: null is omitted on write (the shared options
///     skip nulls)
///     and read as null.
/// </summary>
public sealed class ProtoNullableInt64Converter : JsonConverter<long?>
{
    private static readonly ProtoInt64Converter Inner = new();

    /// <summary>Reads null, a JSON number or a decimal string.</summary>
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeof(long), options);
    }

    /// <summary>Writes the value as a decimal string, or null.</summary>
    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is { } v) Inner.Write(writer, v, options);
        else writer.WriteNullValue();
    }
}