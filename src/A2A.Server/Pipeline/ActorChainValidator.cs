using System.Globalization;
using System.Text.Json;
using A2A.Delegation;

namespace A2A.Server.Pipeline;

/// <summary>Hop limit and shape checks for the actor chain in message metadata.</summary>
internal static class ActorChainValidator
{
    /// <summary>
    ///     Refuses a chain with more hops than <see cref="A2ALimits.MaxActorChainHops" />, then a malformed chain when the
    ///     caller
    ///     activated the actor-chain extension. A message without the key costs one dictionary lookup.
    /// </summary>
    public static void Validate(Message message, A2ALimits limits, ServerCallContext call)
    {
        var metadata = message.Metadata;
        if (metadata is null || !metadata.TryGetValue(ActorChain.MetadataKey, out var value)) return;
        // Counts hops whenever actors is an array, even if its entries are malformed: a loop must stop either way.
        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("actors", out var actors)
            && actors.ValueKind == JsonValueKind.Array)
        {
            var max = limits.MaxActorChainHops;
            if (actors.GetArrayLength() > max)
                throw new A2AException(A2AErrorCode.InvalidRequest, $"The actor chain has more than {max} hops")
                {
                    Metadata = new Dictionary<string, string>
                        { ["maxActorChainHops"] = max.ToString(CultureInfo.InvariantCulture) }
                };
        }

        if (call.ActivatedExtensions.Contains(ActorChain.ExtensionUri) && WellFormedChain(message) is null)
            throw A2AException.InvalidParams("metadata.actorChain is not a valid actor chain");
    }

    /// <summary>The raw chain value from the message metadata when it is well-formed, else null. Not re-serialized.</summary>
    public static JsonElement? WellFormedChain(Message message)
    {
        var metadata = message.Metadata;
        if (metadata is null || !metadata.TryGetValue(ActorChain.MetadataKey, out var value)) return null;
        return ActorChain.TryReadActorChain(metadata, out _) ? value : null;
    }
}