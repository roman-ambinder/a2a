using System.Text.Json;
using A2A.Server.Agents;
using A2A.Server.Authorization;
using A2A.Skills;

namespace A2A.Server.Pipeline;

/// <summary>Refuses a selected skill the agent does not have, when both the caller and the agent opted in.</summary>
internal static class SkillSelectionValidator
{
    /// <summary>
    /// Runs only when the caller activated the extension and the card sets <see cref="SkillSelection.RejectUnknownSkillsParam"/>.
    /// A message without the key costs one dictionary lookup.
    /// </summary>
    public static async ValueTask ValidateAsync(Message message, ServerCallContext call, RequestCards requestCards, IAgentCardProvider cards,
        A2AAuthorizationHandler authorization, CancellationToken ct)
    {
        if (message.Metadata is not { } metadata || !metadata.TryGetValue(SkillSelection.MetadataKey, out var value)) return;

        // Resolve first: with a card per tenant, this is what fills ActivatedExtensions. A tenant with no agent is refused elsewhere.
        if (await requestCards.ResolveAsync(call, ct).ConfigureAwait(false) is not { } card) return;
        if (!call.ActivatedExtensions.Contains(SkillSelection.ExtensionUri) || !SkillSelection.RejectsUnknownSkills(card)) return;

        if (value.ValueKind != JsonValueKind.String) throw Refuse("metadata.skillId must be a string");
        var id = message.GetRequestedSkillId();
        if (id is null)
        {
            // Null here means empty (accepted: it reads as no selection) or unreadable text such as a lone surrogate (refused).
            if (value.ValueEquals(string.Empty)) return;
            throw Refuse("metadata.skillId must be a string");
        }
        if (HasSkill(card, id)) return;

        // The extended card is a separate operation: a host may allow SendMessage and deny GetExtendedAgentCard. A denial falls
        // through to the same refusal, so it gives the caller no new signal.
        if (card.Capabilities.ExtendedAgentCard == true && call.IsAuthenticated
            && await authorization.AuthorizeAsync(call, A2AOperationDescriptor.For(A2AOperation.GetExtendedAgentCard),
                new GetExtendedAgentCardRequest { Tenant = call.Tenant }, ct).ConfigureAwait(false)
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
