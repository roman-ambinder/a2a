using A2A.Server.Pipeline;

namespace A2A.Server.Authorization;

/// <summary>Whether a skill id is on an agent's card: shared by skill selection and skill authorization.</summary>
internal static class KnownSkills
{
    /// <summary>True when <paramref name="card" /> lists a skill with this id (ordinal comparison).</summary>
    public static bool HasSkill(AgentCard card, string id)
    {
        if (card.Skills is not { } skills) return false;
        foreach (var skill in skills)
            if (string.Equals(skill.Id, id, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    ///     True when <paramref name="id" /> is on the public <paramref name="card" />, or on the extended card. The extended
    ///     card counts
    ///     only for an authenticated caller who is authorized for GetExtendedAgentCard (it is a separate operation: a host may
    ///     allow
    ///     SendMessage and deny it). A denial falls through to "not known", so it gives the caller no new signal.
    /// </summary>
    public static async ValueTask<bool> IsKnownAsync(AgentCard card, string id, ServerCallContext call,
        IAgentCardProvider cards,
        A2AAuthorizationHandler authorization, CancellationToken ct)
    {
        if (HasSkill(card, id)) return true;
        return card.Capabilities.ExtendedAgentCard == true && call.IsAuthenticated
                                                           && await authorization.AuthorizeAsync(call,
                                                               A2AOperationDescriptor.For(A2AOperation
                                                                   .GetExtendedAgentCard),
                                                               new GetExtendedAgentCardRequest { Tenant = call.Tenant },
                                                               ct).ConfigureAwait(false)
                                                           && await cards.GetExtendedAsync(call, card, ct)
                                                               .ConfigureAwait(false) is { } extended
                                                           && HasSkill(extended, id);
    }
}