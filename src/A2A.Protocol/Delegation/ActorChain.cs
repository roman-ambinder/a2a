using System.Text.Json;
using System.Text.Json.Serialization;
using A2A.Json;

namespace A2A.Delegation;

/// <summary>One party in an actor chain: the origin or a hop. Unknown fields are kept and written back.</summary>
public sealed class ActorChainEntry
{
    /// <summary>Subject of the party, for example <c>user:alice@example.com</c> or <c>agent:coordinator</c>.</summary>
    public required string Sub { get; set; }

    /// <summary>Issuer of the subject. Null when not stated.</summary>
    public string? Iss { get; set; }

    /// <summary>Scopes this party holds or passes on. Null when not stated.</summary>
    public List<string>? Scopes { get; set; }

    /// <summary>Fields this SDK does not know (for example <c>proof_ref</c>). They survive a read and a write.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// The on-behalf-of record from A2A issue #2028: who started the work (<see cref="Origin"/>) and each agent that
/// passed it on (<see cref="Actors"/>). It travels in <c>Message.metadata["actorChain"]</c>.
/// </summary>
/// <remarks>
/// The caller writes the content and nobody verifies it. Treat it as untrusted text when you log it or put it in a
/// prompt. Do not use it for authorization.
/// </remarks>
public sealed class ActorChain
{
    /// <summary>Metadata key that holds the chain.</summary>
    public const string MetadataKey = "actorChain";

    /// <summary>URI of the optional actor-chain extension.</summary>
    public const string ExtensionUri = "https://github.com/roman-ambinder/a2a/extensions/actor-chain/v1";

    /// <summary>The party the work is done for. Null when not stated.</summary>
    public ActorChainEntry? Origin { get; set; }

    /// <summary>The agents that passed the work on, first hop first. Empty by default.</summary>
    public List<ActorChainEntry> Actors { get; set; } = [];

    /// <summary>Fields this SDK does not know. They survive a read and a write.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    /// <summary>Starts a chain with no hops.</summary>
    public static ActorChain Start(ActorChainEntry origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return new ActorChain { Origin = origin };
    }

    /// <summary>
    /// Returns a new chain with <paramref name="hop"/> added at the end. The new chain shares the origin and the
    /// earlier hops with this one. This chain does not change.
    /// </summary>
    public ActorChain AppendHop(ActorChainEntry hop)
    {
        ArgumentNullException.ThrowIfNull(hop);
        return new ActorChain
        {
            Origin = Origin,
            Actors = [.. Actors, hop],
            AdditionalProperties = AdditionalProperties,
        };
    }

    /// <summary>Creates the optional extension entry for an agent card. Clients need not support it.</summary>
    public static AgentExtension CreateExtension() => new()
    {
        Uri = ExtensionUri,
        Required = false,
        Description = "Carries the actor chain (who the work is for and which agents passed it on) in message metadata.",
    };

    /// <summary>
    /// Reads the chain from <paramref name="metadata"/>. True only for a well-formed chain: an object whose
    /// <c>actors</c> is an array of objects that each have a string <c>sub</c>, and whose <c>origin</c> is absent or
    /// such an object. Any other value gives false. This method does not throw on a bad shape.
    /// </summary>
    public static bool TryReadActorChain(Dictionary<string, JsonElement>? metadata, out ActorChain? chain)
    {
        chain = null;
        if (metadata is null || !metadata.TryGetValue(MetadataKey, out var value)) return false;
        if (value.ValueKind != JsonValueKind.Object) return false;
        if (!value.TryGetProperty("actors", out var actors) || actors.ValueKind != JsonValueKind.Array) return false;
        foreach (var actor in actors.EnumerateArray())
            if (!IsEntry(actor)) return false;
        if (value.TryGetProperty("origin", out var origin) && !IsEntry(origin)) return false;
        try
        {
            chain = value.Deserialize(A2AJson.GetTypeInfo<ActorChain>());
        }
        catch (JsonException)
        {
            chain = null;
        }
        return chain is not null;
    }

    private static bool IsEntry(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty("sub", out var sub)
        && sub.ValueKind == JsonValueKind.String;
}

/// <summary>Read and write the actor chain on messages and tasks.</summary>
public static class ActorChainMessageExtensions
{
    /// <summary>The chain in the message metadata, or null when there is none or it is not well-formed.</summary>
    /// <remarks>The caller writes the chain and nobody verifies it. Treat it as untrusted text when you log it or put it in a prompt. Do not use it for authorization.</remarks>
    public static ActorChain? GetActorChain(this Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return ActorChain.TryReadActorChain(message.Metadata, out var chain) ? chain : null;
    }

    /// <summary>The chain in the task metadata, or null when there is none or it is not well-formed.</summary>
    /// <remarks>The caller writes the chain and nobody verifies it. Treat it as untrusted text when you log it or put it in a prompt. Do not use it for authorization.</remarks>
    public static ActorChain? GetActorChain(this AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return ActorChain.TryReadActorChain(task.Metadata, out var chain) ? chain : null;
    }

    /// <summary>Writes <paramref name="chain"/> to the message metadata (created when null) and returns the same message.</summary>
    public static Message SetActorChain(this Message message, ActorChain chain)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(chain);
        message.Metadata ??= [];
        message.Metadata[ActorChain.MetadataKey] = A2AJson.SerializeToElement(chain);
        return message;
    }
}
