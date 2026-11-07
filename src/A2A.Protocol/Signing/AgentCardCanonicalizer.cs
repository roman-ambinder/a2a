using System.Text.Json.Nodes;
using A2A.Json;

namespace A2A.Signing;

/// <summary>
/// Produces the canonical signing payload of an Agent Card (spec §8.4.1).
/// </summary>
/// <remarks>
/// Two forms exist:
/// <list type="bullet">
/// <item><b>Normalized</b>: default-valued fields removed per §8.4.1, then RFC 8785. This is what the signer signs.</item>
/// <item><b>Verbatim</b>: the received JSON as-is (minus <c>signatures</c>), then RFC 8785.</item>
/// </list>
/// Both are computed from the raw JSON rather than a re-serialized model, so fields this SDK does not know about
/// (newer protocol versions, extensions) stay in the payload and the signature still verifies. The verifier accepts either form.
/// </remarks>
public static class AgentCardCanonicalizer
{
    // Fields marked REQUIRED in a2a.proto anywhere in the AgentCard tree: always kept, even when empty.
    private static readonly HashSet<string> RequiredKeys = new(StringComparer.Ordinal)
    {
        "name", "description", "supportedInterfaces", "version", "capabilities", "defaultInputModes",
        "defaultOutputModes", "skills", "url", "protocolBinding", "protocolVersion", "organization", "id", "tags",
        "protected", "signature", "location", "scheme", "flows", "openIdConnectUrl", "authorizationUrl", "tokenUrl",
        "scopes", "deviceAuthorizationUrl",
    };

    // Fields declared with the proto 'optional' keyword: kept when explicitly present, even at default value.
    private static readonly HashSet<string> OptionalPresenceKeys = new(StringComparer.Ordinal)
    {
        "documentationUrl", "iconUrl", "streaming", "pushNotifications", "extendedAgentCard",
    };

    // Free-form maps (google.protobuf.Struct / map fields): user data, never normalized internally.
    private static readonly HashSet<string> OpaqueMapKeys = new(StringComparer.Ordinal)
    {
        "params", "metadata", "header", "scopes",
    };

    public static byte[] Normalized(AgentCard card) => Normalized(ToNode(card));

    public static byte[] Normalized(JsonObject card)
    {
        var copy = (JsonObject)card.DeepClone();
        copy.Remove("signatures");
        StripDefaults(copy);
        return JsonCanonicalizer.Canonicalize(copy);
    }

    public static byte[] Verbatim(JsonObject card)
    {
        var copy = (JsonObject)card.DeepClone();
        copy.Remove("signatures");
        return JsonCanonicalizer.Canonicalize(copy);
    }

    public static JsonObject ToNode(AgentCard card) =>
        JsonNode.Parse(A2AJson.Serialize(card))!.AsObject();

    private static void StripDefaults(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var value = obj[key];
                    if (!OpaqueMapKeys.Contains(key)) StripDefaults(value);
                    if (RequiredKeys.Contains(key) || OptionalPresenceKeys.Contains(key)) continue;
                    if (IsDefault(value)) obj.Remove(key);
                }
                break;
            case JsonArray arr:
                foreach (var item in arr) StripDefaults(item);
                break;
        }
    }

    private static bool IsDefault(JsonNode? value) => value switch
    {
        null => true,
        JsonArray a => a.Count == 0,
        JsonObject o => o.Count == 0,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length == 0,
        JsonValue v when v.TryGetValue<bool>(out var b) => !b,
        JsonValue v when v.TryGetValue<double>(out var d) => d == 0,
        _ => false,
    };
}
