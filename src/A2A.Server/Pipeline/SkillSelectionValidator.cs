using System.Text.Json;
using A2A.Server.Agents;
using A2A.Server.Authorization;
using A2A.Skills;

namespace A2A.Server.Pipeline;

/// <summary>Refuses a selected skill the agent does not have, when both the caller and the agent opted in.</summary>
internal static class SkillSelectionValidator
{
    /// <summary>
    ///     Runs only when the caller activated the extension and the card sets
    ///     <see cref="SkillSelection.RejectUnknownSkillsParam" />.
    ///     A message without the key costs one dictionary lookup.
    /// </summary>
    public static async ValueTask ValidateAsync(Message message, ServerCallContext call, RequestCards requestCards,
        IAgentCardProvider cards,
        A2AAuthorizationHandler authorization, CancellationToken ct)
    {
        if (message.Metadata is not { } metadata ||
            !metadata.TryGetValue(SkillSelection.MetadataKey, out var value)) return;

        // Resolve first: with a card per tenant, this is what fills ActivatedExtensions. A tenant with no agent is refused elsewhere.
        if (await requestCards.ResolveAsync(call, ct).ConfigureAwait(false) is not { } card) return;
        if (!call.ActivatedExtensions.Contains(SkillSelection.ExtensionUri) ||
            !SkillSelection.RejectsUnknownSkills(card)) return;

        if (value.ValueKind != JsonValueKind.String) throw Refuse("metadata.skillId must be a string");
        var id = message.GetRequestedSkillId();
        if (id is null)
        {
            // Null here means empty (accepted: it reads as no selection) or unreadable text such as a lone surrogate (refused).
            if (value.ValueEquals(string.Empty)) return;
            throw Refuse("metadata.skillId must be a string");
        }

        if (await KnownSkills.IsKnownAsync(card, id, call, cards, authorization, ct).ConfigureAwait(false)) return;

        throw Refuse("metadata.skillId does not name a skill of this agent");
    }

    private static A2AException Refuse(string text)
    {
        return new A2AException(A2AErrorCode.InvalidParams, text)
        {
            Metadata = new Dictionary<string, string> { ["extension"] = SkillSelection.ExtensionUri }
        };
    }
}