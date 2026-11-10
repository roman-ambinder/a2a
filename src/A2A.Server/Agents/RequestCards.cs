using A2A.Server.Pipeline;

namespace A2A.Server.Agents;

/// <summary>
///     The card of a request's tenant, from the <see cref="IAgentCardProvider" />, resolved at most once per request and
///     tenant
///     (<see cref="ServerCallContext.ResolvedCard" />). Every capability check of an operation uses it. With per-tenant
///     cards
///     (<see cref="TenantCardProvider" />), the first resolution also checks the request's extensions against the tenant's
///     card;
///     with one card for every tenant, <see cref="Pipeline.CallValidator" /> has already done that.
/// </summary>
internal sealed class RequestCards(IAgentCardProvider cards)
{
    /// <summary>The refusal text for a tenant that no agent serves (its card is null).</summary>
    internal const string NoAgentText = "No agent is configured for this tenant.";

    /// <summary>True when each tenant has its own card (extensions are then checked here, not in the call validator).</summary>
    public bool PerTenant { get; } = IsPerTenant(cards);

    /// <summary>True when <paramref name="cards" /> supplies a card per tenant.</summary>
    public static bool IsPerTenant(IAgentCardProvider cards)
    {
        return cards is AgentCardProvider { HasTenantCards: true };
    }

    /// <summary>The card of the call's tenant, or null when no agent serves the tenant.</summary>
    public async ValueTask<AgentCard?> ResolveAsync(ServerCallContext call, CancellationToken ct)
    {
        if (call.ResolvedCard is { } resolved && string.Equals(resolved.Tenant, call.Tenant, StringComparison.Ordinal))
            return resolved.Card;
        var card = await cards.GetAsync(new AgentRequestScope(call.Tenant, call.RouteValues), ct).ConfigureAwait(false);
        if (card is not null && PerTenant) CallValidator.ApplyExtensions(card, call);
        call.ResolvedCard = (call.Tenant, card);
        return card;
    }

    /// <summary>
    ///     The tenant's card; a tenant no agent serves is <see cref="A2AErrorCode.UnsupportedOperation" /> (
    ///     <see cref="NoAgentText" />).
    /// </summary>
    public async ValueTask<AgentCard> RequireAgentAsync(ServerCallContext call, CancellationToken ct)
    {
        return await ResolveAsync(call, ct).ConfigureAwait(false) ??
               throw new A2AException(A2AErrorCode.UnsupportedOperation, NoAgentText);
    }

    /// <summary>
    ///     The tenant's card for an operation on task <paramref name="taskId" />; a tenant no agent serves looks like a
    ///     missing task.
    /// </summary>
    public async ValueTask<AgentCard> RequireAgentForTaskAsync(ServerCallContext call, string? taskId,
        CancellationToken ct)
    {
        return await ResolveAsync(call, ct).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(taskId ?? "");
    }

    /// <summary>Throws UnsupportedOperation unless <paramref name="card" /> declares streaming.</summary>
    public static void RequireStreaming(AgentCard card)
    {
        if (card.Capabilities.Streaming != true)
            throw new A2AException(A2AErrorCode.UnsupportedOperation, "Streaming is not supported by this agent.");
    }

    /// <summary>Throws PushNotificationNotSupported unless <paramref name="card" /> declares push notifications.</summary>
    public static void RequirePush(AgentCard card)
    {
        if (card.Capabilities.PushNotifications != true)
            throw new A2AException(A2AErrorCode.PushNotificationNotSupported);
    }
}