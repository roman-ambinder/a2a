using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace A2A.Json;

/// <summary>Shared JSON settings for the A2A wire format (camelCase, ProtoJSON enums, UTC timestamps, nulls omitted).</summary>
public static class A2AJson
{
    /// <summary>Deepest JSON nesting the shared options accept (also the default of <c>A2ALimits.MaxJsonDepth</c>).</summary>
    public const int MaxJsonDepth = 32;

    /// <summary>
    /// Options used for every A2A payload. Do not mutate. Nesting deeper than <see cref="MaxJsonDepth"/> is rejected. On .NET 10
    /// duplicate property names are rejected too; on .NET 8 the ASP.NET Core layer checks request bodies before parsing.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            MaxDepth = MaxJsonDepth,
        };
#if NET10_0_OR_GREATER
        o.AllowDuplicateProperties = false;
#endif
        o.Converters.Add(new TimestampConverter());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static byte[] SerializeToUtf8Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static JsonElement SerializeToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"Null {typeof(T).Name} payload.");

    public static T Deserialize<T>(JsonElement element) =>
        element.Deserialize<T>(Options) ?? throw new JsonException($"Null {typeof(T).Name} payload.");

    /// <summary>Deep-copies a model through its wire form. Used by stores to avoid shared mutable state.</summary>
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Options), Options)!;
}
