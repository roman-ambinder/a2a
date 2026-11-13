using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace A2A.Json;

/// <summary>Shared JSON settings for the A2A wire format (camelCase, ProtoJSON enums, UTC timestamps, nulls omitted).</summary>
public static class A2AJson
{
    /// <summary>
    ///     Deepest JSON nesting the shared options read or write. This is the output ceiling: incoming request bodies are held
    ///     to the
    ///     smaller <c>A2ALimits.MaxJsonDepth</c> (32), so a task built from an accepted request, wrapped in history, a result
    ///     envelope or a stream event, always fits.
    /// </summary>
    public const int MaxJsonDepth = 64;

    /// <summary>
    ///     Members that are C# <c>required</c> (construction-time safety) but that a ProtoJSON peer omits when empty, so they
    ///     must not be
    ///     JSON-required on read. The property initializers supply the default. <see cref="Options" /> applies the modifier on
    ///     top of the
    ///     generated context.
    /// </summary>
    private static readonly Dictionary<Type, string[]> OptionalOnWire = new()
    {
        [typeof(AgentCard)] =
        [
            "name", "description", "version", "supportedInterfaces", "capabilities", "defaultInputModes",
            "defaultOutputModes", "skills"
        ],
        [typeof(Artifact)] = ["parts"]
    };

    /// <summary>Members that A2A 1.1 added to objects that 1.0 also has. <see cref="V10Options" /> never writes them.</summary>
    private static readonly Dictionary<Type, string[]> V11Members = new()
    {
        [typeof(AgentTask)] = ["generation", "timeline"],
        [typeof(Artifact)] = ["startGeneration", "endGeneration", "role"],
        [typeof(TaskStatusUpdateEvent)] = ["generation"],
        [typeof(TaskArtifactUpdateEvent)] = ["generation"],
        // A 1.1-only event, but stored in the 1.0 shape like every event: the generation is added for 1.1 readers.
        [typeof(TaskMessageUpdateEvent)] = ["generation"],
        [typeof(TaskPushNotificationConfig)] = ["protocolVersion"]
    };

    /// <summary>
    ///     Options used for every A2A payload. Do not mutate. Nesting deeper than <see cref="MaxJsonDepth" /> is rejected. On
    ///     .NET 10
    ///     duplicate property names are rejected too; on netstandard2.0 they are not (the ASP.NET Core layer checks request
    ///     bodies before parsing).
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>
    ///     <see cref="Options" /> for a caller of an older protocol version (1.0): the members A2A 1.1 added to existing
    ///     objects are
    ///     never written (<c>generation</c>, <c>timeline</c>, artifact <c>startGeneration</c>, <c>endGeneration</c> and
    ///     <c>role</c>, the push config <c>protocolVersion</c>), so the output is the 1.0 shape even when the model holds 1.1
    ///     data.
    ///     Reading is the same as with <see cref="Options" />. Do not mutate.
    /// </summary>
    public static JsonSerializerOptions V10Options { get; } = CreateOptions(true);

    /// <summary>The empty JSON object <c>{}</c>, the result of operations that return nothing (for example delete).</summary>
    public static JsonElement EmptyObject { get; } = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>
    ///     The options for a caller of <paramref name="protocolVersion" />: <see cref="Options" /> for 1.1 and later,
    ///     else <see cref="V10Options" />.
    /// </summary>
    public static JsonSerializerOptions OptionsFor(string? protocolVersion)
    {
        return A2AProtocol.IsVersion11OrLater(protocolVersion) ? Options : V10Options;
    }

    /// <summary>
    ///     The generated metadata for <typeparamref name="T" /> in <see cref="OptionsFor" />
    ///     <paramref name="protocolVersion" />.
    /// </summary>
    public static JsonTypeInfo<T> GetTypeInfo<T>(string? protocolVersion)
    {
        return (JsonTypeInfo<T>)OptionsFor(protocolVersion).GetTypeInfo(typeof(T));
    }

    /// <summary>
    ///     Creates a new, mutable copy of the shared options. Use it to add your own converters or type resolvers. Prefer
    ///     <see cref="Options" /> when you do not need changes.
    /// </summary>
    public static JsonSerializerOptions CreateOptions()
    {
        return CreateOptions(false);
    }

    /// <summary>
    ///     The shared options. <paramref name="writeSharedLists" /> false leaves out <see cref="WriteSharedLists" />, so the
    ///     task lists are written through their public getters (the contract before it): tests compare the two.
    /// </summary>
    internal static JsonSerializerOptions CreateOptions(bool hideV11, bool writeSharedLists = true)
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false, // ProtoJSON member names are exact; case variants must not bind.
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            MaxDepth = MaxJsonDepth
        };
#if NET10_0_OR_GREATER
        o.AllowDuplicateProperties = false;
#endif
        o.Converters.Add(new TimestampConverter());
        // The generated context is the only metadata source: no reflection fallback, so unknown types throw NotSupportedException.
        var resolver = writeSharedLists
            ? A2AJsonContext.Default.WithAddedModifier(WriteSharedLists).WithAddedModifier(RelaxRequired)
            : A2AJsonContext.Default.WithAddedModifier(RelaxRequired);
        o.TypeInfoResolver = hideV11 ? resolver.WithAddedModifier(HideV11Members) : resolver;
        o.MakeReadOnly();
        return o;
    }

    private static void HideV11Members(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || !V11Members.TryGetValue(info.Type, out var names)) return;
        foreach (var p in info.Properties)
            if (Array.IndexOf(names, p.Name) >= 0)
                p.ShouldSerialize = static (_, _) => false;
    }

    /// <summary>
    ///     ADR-004: <see cref="AgentTask.History" />, <see cref="AgentTask.Timeline" /> and <see cref="Artifact.Parts" />
    ///     build their <see cref="List{T}" /> on the first read, and the generated contract reads them through those
    ///     getters, so serializing a version of a task would build every list of it. This replaces each of the three
    ///     properties with one of the same JSON name, place and settings whose value is an <see cref="IReadOnlyList{T}" />:
    ///     the list when it exists, else the shared version (<see cref="LazyList{T}.View" />). The array is written by the
    ///     same element converter, so the bytes do not change. Reading gets a <see cref="List{T}" /> (what the generated
    ///     converter makes for <see cref="IReadOnlyList{T}" />) and sets the public property with it, as before.
    /// </summary>
    /// <remarks>
    ///     The new properties come from <see cref="JsonMetadataServices.CreatePropertyInfo{T}" />, the trimming and AOT safe
    ///     factory the generated code uses (<see cref="JsonTypeInfo.CreateJsonPropertyInfo(Type, string)" /> needs reflection).
    ///     <see cref="Artifact.Parts" /> is a C# <c>required</c> member, which the generated contract binds as a constructor
    ///     argument of its own type; the replacement cannot bind to it, so <see cref="Artifact" /> is created by a plain
    ///     delegate instead, and every member, <c>parts</c> included, is set after creation. <c>artifactId</c> stays
    ///     JSON-required, and <see cref="IJsonOnDeserialized" /> still runs.
    /// </remarks>
    private static void WriteSharedLists(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;
        if (info.Type == typeof(AgentTask))
        {
            ReplaceList<AgentTask, Message>(info, "history", nameof(AgentTask.History),
                static t => t.HistoryStore.View, static (t, v) => t.History = v);
            ReplaceList<AgentTask, TimelineEntry>(info, "timeline", nameof(AgentTask.Timeline),
                static t => t.TimelineStore.View, static (t, v) => t.Timeline = v);
        }
        else if (info.Type == typeof(Artifact))
        {
            ReplaceList<Artifact, Part>(info, "parts", nameof(Artifact.Parts),
                static a => a.PartsStore.View, static (a, v) => a.Parts = v!);
            // Both are set from the JSON after creation; a missing "parts" leaves null, which OnDeserialized makes [].
            info.CreateObject = static () => new Artifact { ArtifactId = null!, Parts = null! };
        }
    }

    private static void ReplaceList<TModel, T>(JsonTypeInfo info, string name, string memberName,
        Func<TModel, IReadOnlyList<T>?> get, Action<TModel, List<T>?> set)
    {
        var properties = info.Properties;
        for (var i = 0; i < properties.Count; i++)
        {
            var old = properties[i];
            if (old.Name != name) continue;
            var values = new JsonPropertyInfoValues<IReadOnlyList<T>?>
            {
                IsProperty = true,
                IsPublic = true,
                DeclaringType = typeof(TModel),
                Getter = obj => get((TModel)obj),
                // The generated IReadOnlyList<T> converter reads into a List<T>; anything else is copied into one.
                Setter = (obj, value) => set((TModel)obj, value is null ? null : value as List<T> ?? [.. value]),
                PropertyName = memberName,
                JsonPropertyName = old.Name
            };
            var replacement = JsonMetadataServices.CreatePropertyInfo(info.Options, values);
            replacement.IsRequired = old.IsRequired;
            replacement.Order = old.Order;
            replacement.NumberHandling = old.NumberHandling;
            replacement.ObjectCreationHandling = old.ObjectCreationHandling;
            replacement.ShouldSerialize = old.ShouldSerialize;
            replacement.AttributeProvider = old.AttributeProvider;
#if NET
            replacement.IsGetNullable = old.IsGetNullable;
            replacement.IsSetNullable = old.IsSetNullable;
#endif
            properties[i] = replacement;
            return;
        }

        throw new InvalidOperationException($"The generated contract of {typeof(TModel).Name} has no '{name}' property.");
    }

    /// <summary>A <see cref="JsonTypeInfo" /> modifier that clears <c>IsRequired</c> on <see cref="OptionalOnWire" /> members.</summary>
    public static void RelaxRequired(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || !OptionalOnWire.TryGetValue(info.Type, out var names)) return;
        foreach (var p in info.Properties)
            if (Array.IndexOf(names, p.Name) >= 0)
                p.IsRequired = false;
    }

    /// <summary>
    ///     The generated metadata for <typeparamref name="T" />. Throws <see cref="NotSupportedException" /> for a type
    ///     outside the context.
    /// </summary>
    public static JsonTypeInfo<T> GetTypeInfo<T>()
    {
        return (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));
    }

    /// <summary>Serializes <paramref name="value" /> to a JSON string with the generated metadata.</summary>
    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, GetTypeInfo<T>());
    }

    /// <summary>Serializes <paramref name="value" /> to UTF-8 JSON bytes with the generated metadata.</summary>
    public static byte[] SerializeToUtf8Bytes<T>(T value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, GetTypeInfo<T>());
    }

    /// <summary>Serializes <paramref name="value" /> to a <see cref="JsonElement" /> with the generated metadata.</summary>
    public static JsonElement SerializeToElement<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value, GetTypeInfo<T>());
    }

    /// <summary>Deserializes a JSON string. Throws <see cref="JsonException" /> on invalid or null input.</summary>
    public static T Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize(json, GetTypeInfo<T>()) ??
               throw new JsonException($"Null {typeof(T).Name} payload.");
    }

    /// <summary>Deserializes a <see cref="JsonElement" />. Throws <see cref="JsonException" /> on invalid or null input.</summary>
    public static T Deserialize<T>(JsonElement element)
    {
        return element.Deserialize(GetTypeInfo<T>()) ?? throw new JsonException($"Null {typeof(T).Name} payload.");
    }

    /// <summary>
    ///     Deep-copies a model through its wire form. Costs a full serialization; the server uses the structural copies of
    ///     <see cref="ModelCopies" /> instead. Kept for the card signer, which signs a detached copy of the card.
    /// </summary>
    internal static T Clone<T>(T value)
    {
        var info = GetTypeInfo<T>();
        return JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(value, info), info)!;
    }
}