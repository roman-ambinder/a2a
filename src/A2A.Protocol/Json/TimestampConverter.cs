using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace A2A.Json;

/// <summary>
/// Writes timestamps as UTC ISO 8601 with millisecond precision (<c>YYYY-MM-DDTHH:mm:ss.sssZ</c>, spec §5.6.1).
/// Reads any ISO 8601 value and normalizes it to UTC.
/// </summary>
public sealed class TimestampConverter : JsonConverter<DateTimeOffset>
{
    /// <summary>The write format: UTC with millisecond precision.</summary>
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Reads an ISO 8601 string and normalizes it to UTC. Throws <see cref="JsonException"/> when it is not a valid timestamp.</summary>
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString() ?? throw new JsonException("Timestamp must be a string.");
        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            throw new JsonException($"Invalid ISO 8601 timestamp '{s}'.");
        return value.ToUniversalTime();
    }

    /// <summary>Writes the timestamp in <see cref="Format"/>.</summary>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
#if NET
        // Formatted on the stack: no string per timestamp.
        Span<char> text = stackalloc char[32];
        if (value.UtcDateTime.TryFormat(text, out var written, Format, CultureInfo.InvariantCulture))
        {
            writer.WriteStringValue(text[..written]);
            return;
        }
#endif
        writer.WriteStringValue(FormatUtc(value));
    }

    /// <summary>Formats a timestamp in <see cref="Format"/>, after converting it to UTC.</summary>
    public static string FormatUtc(DateTimeOffset value)
        => value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);
}
