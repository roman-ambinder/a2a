using System.Text.Json;
using A2A.Server.Agents;
using A2A.Skills;

namespace A2A.Server.Pipeline;

/// <summary>Refuses a selected skill the agent does not have, when both the caller and the agent opted in.</summary>
internal static class SkillSelectionValidator
{
    /// <summary>
    /// Runs only when the caller activated the extension and the card sets <see cref="SkillSelection.RejectUnknownSkillsParam"/>.
    /// A message without the key costs one dictionary lookup.
    /// </summary>
    public static async ValueTask ValidateAsync(Message message, ServerCallContext call, RequestCards requestCards, IAgentCardProvider cards, CancellationToken ct)
    {
        if (message.Metadata is not { } metadata || !metadata.TryGetValue(SkillSelection.MetadataKey, out var value)) return;

        // Resolve first: with a card per tenant, this is what fills ActivatedExtensions. A tenant with no agent is refused elsewhere.
        if (await requestCards.ResolveAsync(call, ct).ConfigureAwait(false) is not { } card) return;
        if (!call.ActivatedExtensions.Contains(SkillSelection.ExtensionUri) || !SkillSelection.RejectsUnknownSkills(card)) return;

        if (value.ValueKind != JsonValueKind.String) throw Refuse("metadata.skillId must be a string");
        var id = value.GetString();
        if (string.IsNullOrEmpty(id) || HasSkill(card, id)) return;

        if (card.Capabilities.ExtendedAgentCard == true && call.IsAuthenticated
            && await cards.GetExtendedAsync(call, card, ct).ConfigureAwait(false) is { } extended
            && HasSkill(extended, id)) return;

        throw Refuse("metadata.skillId does not name a skill of this agent");
    }

    private static bool HasSkill(AgentCard card, string id)
    {
        if (card.Skills is not { } skills) return false;
        foreach (var skill in skills)
            if (string.Equals(skill.Id, id, StringComparison.Ordinal)) return true;
        return false;
    }

    private static A2AException Refuse(string text) => new(A2AErrorCode.InvalidParams, text)
    {
        Metadata = new() { ["extension"] = SkillSelection.ExtensionUri },
    };
}
