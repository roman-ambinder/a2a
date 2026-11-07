using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Json;

namespace A2A.Signing;

/// <summary>
/// Produces the canonical signing payload of an Agent Card: the same bytes a2a-python 1.2.1 and a2a-js 1.3.0 sign and verify.
/// </summary>
/// <remarks>
/// <para>
/// Those SDKs parse the card into the A2A v1.0 protobuf schema, write it back as proto JSON, drop <c>signatures</c>,
/// remove empty values recursively and apply RFC 8785. This class reproduces that on the JSON tree:
/// </para>
/// <list type="bullet">
/// <item>Top-level <c>signatures</c> is removed.</item>
/// <item>Members not in the v1.0 Agent Card schema are removed (the SDKs parse with unknown fields ignored).
/// This also removes the v0.3 compatibility fields a2a-python adds when it serves a card.</item>
/// <item><c>false</c> is removed from the two booleans without proto field presence: <c>AgentExtension.required</c>
/// and <c>AuthorizationCodeOAuthFlow.pkceRequired</c>. Booleans with presence (<c>streaming</c>, <c>pushNotifications</c>,
/// <c>extendedAgentCard</c>) keep an explicit <c>false</c>.</item>
/// <item><c>null</c>, <c>""</c>, <c>[]</c> and <c>{}</c> are removed recursively, including array elements and values inside
/// free-form <c>params</c>; a container left empty by this is removed too. REQUIRED fields get no exception.</item>
/// </list>
/// <para>Inside <c>params</c> (a <c>google.protobuf.Struct</c>) every key is kept and <c>false</c>/<c>0</c> are data.</para>
/// <para>See <c>tests/A2A.Tests/Vectors/README.md</c> for the source this rule was taken from.</para>
/// </remarks>
public static class AgentCardCanonicalizer
{
    private enum Shape { Scalar, ImplicitBool, Struct, Message }

    private enum Cardinality { Single, Repeated, Map }

    private sealed record Field(Shape Shape, Cardinality Cardinality = Cardinality.Single, IReadOnlyDictionary<string, Field>? Fields = null);

    private static readonly Field Scalar = new(Shape.Scalar);
    private static readonly Field RepeatedScalar = new(Shape.Scalar, Cardinality.Repeated);
    private static readonly Field ScalarMap = new(Shape.Scalar, Cardinality.Map);
    private static readonly Field ImplicitBool = new(Shape.ImplicitBool);
    private static readonly Field Struct = new(Shape.Struct);

    // The A2A v1.0 AgentCard message tree (lf.a2a.v1 in a2a.proto), keyed by proto JSON name.
    private static readonly IReadOnlyDictionary<string, Field> AgentCardFields = BuildAgentCardSchema();

    /// <summary>
    /// Returns the RFC 8785 bytes that Agent Card signatures cover. Works on raw JSON so that a card is canonicalized
    /// exactly as received; <paramref name="card"/> is not modified.
    /// </summary>
    /// <remarks>
    /// <b>Security:</b> members outside the A2A v1.0 Agent Card schema are removed before signing, as a2a-python and
    /// a2a-js do. They are not covered by the signature and must not be trusted from a signed card. The same holds for
    /// every value <see cref="PrunedPaths"/> reports, e.g. a security requirement with no scopes.
    /// </remarks>
    public static byte[] Canonicalize(JsonObject card) => JsonCanonicalizer.Canonicalize(SignedContent(card));

    /// <summary>
    /// Returns the tree that <see cref="Canonicalize"/> serializes: a pruned copy of <paramref name="card"/> holding only
    /// v1.0 schema members, without <c>signatures</c>. Build trusted models from this tree, not from the raw card.
    /// </summary>
    public static JsonObject SignedContent(JsonObject card) => Prune(card, new PruneLog());

    /// <summary>
    /// Returns the JSON paths (e.g. <c>$.skills[0].description</c>) of the values <see cref="Canonicalize"/> removes.
    /// Those values are not covered by a signature. Containers emptied only by removing their children are not listed.
    /// </summary>
    public static IReadOnlyList<string> PrunedPaths(JsonObject card)
    {
        var log = new PruneLog();
        Prune(card, log);
        return log.Removed;
    }

    /// <summary>
    /// Returns the JSON paths of members whose name matches a v1.0 schema member only when case is ignored
    /// (e.g. <c>$.IconUrl</c>, or <c>$.Name</c> next to <c>$.name</c>).
    /// </summary>
    /// <remarks>
    /// Such members are outside the signed content, but a case-insensitive JSON reader (such as the SDK's own
    /// <c>A2AJson</c> options) binds them to model properties. A signed card that contains one must be rejected.
    /// a2a-python and a2a-js never emit them.
    /// </remarks>
    public static IReadOnlyList<string> CaseVariantMembers(JsonObject card)
    {
        var log = new PruneLog();
        Prune(card, log);
        return log.CaseVariants;
    }

    private sealed class PruneLog
    {
        public List<string> Removed { get; } = new();
        public List<string> CaseVariants { get; } = new();
    }

    private static JsonObject Prune(JsonObject card, PruneLog log)
    {
        var copy = (JsonObject)card.DeepClone();
        copy.Remove("signatures");
        PruneMembers(copy, AgentCardFields, "$", log);
        return copy;
    }

    /// <summary>Serializes a card model to a JSON tree with the SDK's wire options.</summary>
    public static JsonObject ToNode(AgentCard card) =>
        JsonNode.Parse(A2AJson.Serialize(card))!.AsObject();

    private static IReadOnlyDictionary<string, Field> BuildAgentCardSchema()
    {
        static Field Message(Cardinality cardinality, params (string Name, Field Field)[] fields) =>
            new(Shape.Message, cardinality, fields.ToDictionary(f => f.Name, f => f.Field, StringComparer.Ordinal));

        var stringList = Message(Cardinality.Single, ("list", RepeatedScalar));
        var securityRequirements = Message(Cardinality.Repeated, ("schemes", stringList with { Cardinality = Cardinality.Map }));
        var flows = Message(Cardinality.Single,
            ("authorizationCode", Message(Cardinality.Single,
                ("authorizationUrl", Scalar), ("tokenUrl", Scalar), ("refreshUrl", Scalar), ("scopes", ScalarMap), ("pkceRequired", ImplicitBool))),
            ("clientCredentials", Message(Cardinality.Single, ("tokenUrl", Scalar), ("refreshUrl", Scalar), ("scopes", ScalarMap))),
            ("implicit", Message(Cardinality.Single, ("authorizationUrl", Scalar), ("refreshUrl", Scalar), ("scopes", ScalarMap))),
            ("password", Message(Cardinality.Single, ("tokenUrl", Scalar), ("refreshUrl", Scalar), ("scopes", ScalarMap))),
            ("deviceCode", Message(Cardinality.Single,
                ("deviceAuthorizationUrl", Scalar), ("tokenUrl", Scalar), ("refreshUrl", Scalar), ("scopes", ScalarMap))));
        var securityScheme = Message(Cardinality.Map,
            ("apiKeySecurityScheme", Message(Cardinality.Single, ("description", Scalar), ("location", Scalar), ("name", Scalar))),
            ("httpAuthSecurityScheme", Message(Cardinality.Single, ("description", Scalar), ("scheme", Scalar), ("bearerFormat", Scalar))),
            ("oauth2SecurityScheme", Message(Cardinality.Single, ("description", Scalar), ("flows", flows), ("oauth2MetadataUrl", Scalar))),
            ("openIdConnectSecurityScheme", Message(Cardinality.Single, ("description", Scalar), ("openIdConnectUrl", Scalar))),
            ("mtlsSecurityScheme", Message(Cardinality.Single, ("description", Scalar))));

        return Message(Cardinality.Single,
            ("name", Scalar),
            ("description", Scalar),
            ("supportedInterfaces", Message(Cardinality.Repeated,
                ("url", Scalar), ("protocolBinding", Scalar), ("tenant", Scalar), ("protocolVersion", Scalar))),
            ("provider", Message(Cardinality.Single, ("url", Scalar), ("organization", Scalar))),
            ("version", Scalar),
            ("documentationUrl", Scalar),
            ("capabilities", Message(Cardinality.Single,
                ("streaming", Scalar), ("pushNotifications", Scalar), ("extendedAgentCard", Scalar),
                ("extensions", Message(Cardinality.Repeated,
                    ("uri", Scalar), ("description", Scalar), ("required", ImplicitBool), ("params", Struct))))),
            ("securitySchemes", securityScheme),
            ("securityRequirements", securityRequirements),
            ("defaultInputModes", RepeatedScalar),
            ("defaultOutputModes", RepeatedScalar),
            ("skills", Message(Cardinality.Repeated,
                ("id", Scalar), ("name", Scalar), ("description", Scalar), ("tags", RepeatedScalar), ("examples", RepeatedScalar),
                ("inputModes", RepeatedScalar), ("outputModes", RepeatedScalar), ("securityRequirements", securityRequirements))),
            ("signatures", Message(Cardinality.Repeated, ("protected", Scalar), ("signature", Scalar), ("header", Struct))),
            ("iconUrl", Scalar)).Fields!;
    }

    // The pruning rule. Returns true when the value must be removed from its parent.
    // Removals of values that were themselves prunable are recorded; containers emptied by their children are not.
    private static bool PruneField(JsonNode? node, Field field, string path, PruneLog log) =>
        (field.Cardinality, node) switch
        {
            (Cardinality.Repeated, JsonArray array) => PruneElements(array, field with { Cardinality = Cardinality.Single }, path, log),
            (Cardinality.Map, JsonObject map) => PruneMap(map, field with { Cardinality = Cardinality.Single }, path, log),
            _ => PruneValue(node, field, path, log),
        };

    private static bool PruneValue(JsonNode? node, Field field, string path, PruneLog log)
    {
        switch (node)
        {
            case JsonObject obj when field is { Shape: Shape.Message, Fields: { } fields }:
                return PruneMembers(obj, fields, path, log);
            case JsonObject obj:
                return PruneMap(obj, Struct, path, log);
            case JsonArray array:
                return PruneElements(array, Struct, path, log);
            case JsonValue value when IsEmptyScalar(value, field.Shape):
            case null:
                log.Removed.Add(path);
                return true;
            default:
                return false;
        }
    }

    private static bool IsEmptyScalar(JsonValue value, Shape shape) => value.GetValueKind() switch
    {
        JsonValueKind.Null => true,
        JsonValueKind.String => value.GetValue<string>().Length == 0,
        JsonValueKind.False => shape == Shape.ImplicitBool,
        _ => false,
    };

    private static bool PruneMembers(JsonObject obj, IReadOnlyDictionary<string, Field> fields, string path, PruneLog log)
    {
        var wasEmpty = obj.Count == 0;
        foreach (var (key, value) in obj.ToList())
        {
            var memberPath = MemberPath(path, key);
            if (!fields.TryGetValue(key, out var field))
            {
                log.Removed.Add(memberPath);
                if (fields.Keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))) log.CaseVariants.Add(memberPath);
                obj.Remove(key);
            }
            else if (PruneField(value, field, memberPath, log))
            {
                obj.Remove(key);
            }
        }
        return Emptied(obj.Count, wasEmpty, path, log);
    }

    private static bool PruneMap(JsonObject map, Field valueField, string path, PruneLog log)
    {
        var wasEmpty = map.Count == 0;
        foreach (var (key, value) in map.ToList())
        {
            if (PruneField(value, valueField, MemberPath(path, key), log)) map.Remove(key);
        }
        return Emptied(map.Count, wasEmpty, path, log);
    }

    private static bool PruneElements(JsonArray array, Field elementField, string path, PruneLog log)
    {
        var wasEmpty = array.Count == 0;
        var drop = new List<int>();
        for (var i = 0; i < array.Count; i++)
        {
            if (PruneField(array[i], elementField, $"{path}[{i}]", log)) drop.Add(i);
        }
        for (var i = drop.Count - 1; i >= 0; i--) array.RemoveAt(drop[i]);
        return Emptied(array.Count, wasEmpty, path, log);
    }

    private static bool Emptied(int count, bool wasEmpty, string path, PruneLog log)
    {
        if (count > 0) return false;
        if (wasEmpty) log.Removed.Add(path);
        return true;
    }

    private static string MemberPath(string parent, string key) =>
        key.Length > 0 && (char.IsAsciiLetter(key[0]) || key[0] == '_') && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            ? $"{parent}.{key}"
            : $"{parent}[{JsonSerializer.Serialize(key)}]";
}
