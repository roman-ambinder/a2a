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
    /// duplicate property names are rejected too; on netstandard2.0 they are not (the ASP.NET Core layer checks request bodies before parsing).
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>
    /// <see cref="Options"/> for a caller of an older protocol version (1.0): the members A2A 1.1 added to existing objects are
    /// never written (<c>generation</c>, <c>timeline</c>, artifact <c>startGeneration</c>, <c>endGeneration</c> and
    /// <c>role</c>, the push config <c>protocolVersion</c>), so the output is the 1.0 shape even when the model holds 1.1 data.
    /// Reading is the same as with <see cref="Options"/>. Do not mutate.
    /// </summary>
    public static JsonSerializerOptions V10Options { get; } = CreateOptions(hideV11: true);

    /// <summary>The options for a caller of <paramref name="protocolVersion"/>: <see cref="Options"/> for 1.1 and later, else <see cref="V10Options"/>.</summary>
    public static JsonSerializerOptions OptionsFor(string? protocolVersion) =>
        A2AProtocol.IsVersion11OrLater(protocolVersion) ? Options : V10Options;

    /// <summary>The generated metadata for <typeparamref name="T"/> in <see cref="OptionsFor"/> <paramref name="protocolVersion"/>.</summary>
    public static JsonTypeInfo<T> GetTypeInfo<T>(string? protocolVersion) => (JsonTypeInfo<T>)OptionsFor(protocolVersion).GetTypeInfo(typeof(T));

    /// <summary>Creates a new, mutable copy of the shared options. Use it to add your own converters or type resolvers. Prefer <see cref="Options"/> when you do not need changes.</summary>
    public static JsonSerializerOptions CreateOptions() => CreateOptions(hideV11: false);

    private static JsonSerializerOptions CreateOptions(bool hideV11)
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false, // ProtoJSON member names are exact; case variants must not bind.
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
        // The generated context is the only metadata source: no reflection fallback, so unknown types throw NotSupportedException.
        var resolver = A2AJsonContext.Default.WithAddedModifier(RelaxRequired);
        o.TypeInfoResolver = hideV11 ? resolver.WithAddedModifier(HideV11Members) : resolver;
        o.MakeReadOnly();
        return o;
    }

    /// <summary>
    /// Members that are C# <c>required</c> (construction-time safety) but that a ProtoJSON peer omits when empty, so they must not be
    /// JSON-required on read. The property initializers supply the default. <see cref="Options"/> applies the modifier on top of the
    /// generated context.
    /// </summary>
    private static readonly Dictionary<Type, string[]> OptionalOnWire = new()
    {
        [typeof(AgentCard)] = ["name", "description", "version", "supportedInterfaces", "capabilities", "defaultInputModes", "defaultOutputModes", "skills"],
        [typeof(Artifact)] = ["parts"],
    };

    /// <summary>Members that A2A 1.1 added to objects that 1.0 also has. <see cref="V10Options"/> never writes them.</summary>
    private static readonly Dictionary<Type, string[]> V11Members = new()
    {
        [typeof(AgentTask)] = ["generation", "timeline"],
        [typeof(Artifact)] = ["startGeneration", "endGeneration", "role"],
        [typeof(TaskStatusUpdateEvent)] = ["generation"],
        [typeof(TaskArtifactUpdateEvent)] = ["generation"],
        // A 1.1-only event, but stored in the 1.0 shape like every event: the generation is added for 1.1 readers.
        [typeof(TaskMessageUpdateEvent)] = ["generation"],
        [typeof(TaskPushNotificationConfig)] = ["protocolVersion"],
    };

    private static void HideV11Members(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || !V11Members.TryGetValue(info.Type, out var names)) return;
        foreach (var p in info.Properties)
            if (Array.IndexOf(names, p.Name) >= 0) p.ShouldSerialize = static (_, _) => false;
    }

    /// <summary>A <see cref="JsonTypeInfo"/> modifier that clears <c>IsRequired</c> on <see cref="OptionalOnWire"/> members.</summary>
    public static void RelaxRequired(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || !OptionalOnWire.TryGetValue(info.Type, out var names)) return;
        foreach (var p in info.Properties)
            if (Array.IndexOf(names, p.Name) >= 0) p.IsRequired = false;
    }

    /// <summary>The generated metadata for <typeparamref name="T"/>. Throws <see cref="NotSupportedException"/> for a type outside the context.</summary>
    public static JsonTypeInfo<T> GetTypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    /// <summary>The empty JSON object <c>{}</c>, the result of operations that return nothing (for example delete).</summary>
    public static JsonElement EmptyObject { get; } = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>Serializes <paramref name="value"/> to a JSON string with the generated metadata.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, GetTypeInfo<T>());

    /// <summary>Serializes <paramref name="value"/> to UTF-8 JSON bytes with the generated metadata.</summary>
    public static byte[] SerializeToUtf8Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, GetTypeInfo<T>());

    /// <summary>Serializes <paramref name="value"/> to a <see cref="JsonElement"/> with the generated metadata.</summary>
    public static JsonElement SerializeToElement<T>(T value) => JsonSerializer.SerializeToElement(value, GetTypeInfo<T>());

    /// <summary>Deserializes a JSON string. Throws <see cref="JsonException"/> on invalid or null input.</summary>
    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize(json, GetTypeInfo<T>()) ?? throw new JsonException($"Null {typeof(T).Name} payload.");

    /// <summary>Deserializes a <see cref="JsonElement"/>. Throws <see cref="JsonException"/> on invalid or null input.</summary>
    public static T Deserialize<T>(JsonElement element) =>
        element.Deserialize(GetTypeInfo<T>()) ?? throw new JsonException($"Null {typeof(T).Name} payload.");

    /// <summary>
    /// Deep-copies a model through its wire form. Costs a full serialization; the server uses the structural copies of
    /// <see cref="ModelCopies"/> instead. Kept for the card signer, which signs a detached copy of the card.
    /// </summary>
    internal static T Clone<T>(T value)
    {
        var info = GetTypeInfo<T>();
        return JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(value, info), info)!;
    }
}
