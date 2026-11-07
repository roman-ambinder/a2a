using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace A2A.Json;

/// <summary>Shared JSON settings for the A2A wire format (camelCase, ProtoJSON enums, UTC timestamps, nulls omitted).</summary>
public static class A2AJson
{
    /// <summary>
    /// Deepest JSON nesting the shared options read or write. This is the output ceiling: incoming request bodies are held to the
    /// smaller <c>A2ALimits.MaxJsonDepth</c> (32), so a task built from an accepted request, wrapped in history, a result
    /// envelope or a stream event, always fits.
    /// </summary>
    public const int MaxJsonDepth = 64;

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
        o.TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RelaxRequired } };
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    /// <summary>
    /// Members that are C# <c>required</c> (construction-time safety) but that a ProtoJSON peer omits when empty, so they must not be
    /// JSON-required on read. The property initializers supply the default. A source-generated context (Task 10) must apply the same
    /// modifier or mark these properties not required.
    /// </summary>
    private static readonly Dictionary<Type, string[]> OptionalOnWire = new()
    {
        [typeof(AgentCard)] = ["name", "description", "version", "supportedInterfaces", "capabilities", "defaultInputModes", "defaultOutputModes", "skills"],
        [typeof(Artifact)] = ["parts"],
    };

    /// <summary>A <see cref="JsonTypeInfo"/> modifier that clears <c>IsRequired</c> on <see cref="OptionalOnWire"/> members.</summary>
    public static void RelaxRequired(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || !OptionalOnWire.TryGetValue(info.Type, out var names)) return;
        foreach (var p in info.Properties)
            if (Array.IndexOf(names, p.Name) >= 0) p.IsRequired = false;
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
