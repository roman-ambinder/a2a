using A2A.Signing;

namespace A2A.Server.Agents;

/// <summary>
///     Supplies the Agent Card of the agent that serves a tenant, so one host can serve many agents (one agent per
///     tenant).
///     Register one with <see cref="A2AServerBuilder.AddTenantCardProvider{T}" />; without it,
///     <see cref="A2AServerOptions.AgentCard" />
///     serves every tenant. The registered <see cref="IAgentCardProvider" /> asks it and completes each card it returns
///     (extension
///     entries, interfaces), so the served card and every capability check use the completed copy. The server asks once
///     per
///     request (the public card endpoint, and each operation once), once per execution when its request did not resolve it
///     already
///     (push capability), and once per system status commit (the retention sweep), never once per event. A card instance
///     is
///     completed once (the completed copy is kept while the instance lives); a provider may return a new card on any call,
///     so it
///     can change cards at runtime. Cache in the provider if building a card is expensive.
/// </summary>
/// <remarks>
///     <para>
///         A tenant whose card is null has no agent: its public card is HTTP 404; SendMessage, SendStreamingMessage,
///         ListTasks and
///         GetExtendedAgentCard are refused with <see cref="A2AErrorCode.UnsupportedOperation" /> ("No agent is configured
///         for this
///         tenant."); every operation on a task (GetTask, CancelTask, SubscribeToTask, push configs) answers TaskNotFound,
///         exactly as
///         for a missing task.
///     </para>
///     <para>
///         Every capability check of a request uses the card of its tenant: streaming, push notifications, extensions
///         (required and
///         activated) and the extended card. Tenants compare ordinally.
///     </para>
/// </remarks>
public abstract class TenantCardProvider
{
    /// <summary>The public card of the agent serving <paramref name="scope" />'s tenant, or null when no agent serves it.</summary>
    /// <param name="scope">The tenant and route values of the request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public abstract ValueTask<AgentCard?> GetCardAsync(AgentRequestScope scope, CancellationToken cancellationToken);

    /// <summary>
    ///     The extended card for an authenticated caller of the tenant <see cref="ServerCallContext.Tenant" /> (spec §3.1.11).
    ///     Only
    ///     asked when the tenant's public card sets <c>capabilities.extendedAgentCard</c>. Null answers
    ///     <see cref="A2AErrorCode.ExtendedAgentCardNotConfigured" />. Default: null.
    /// </summary>
    /// <param name="call">The caller.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public virtual ValueTask<AgentCard?> GetExtendedCardAsync(ServerCallContext call,
        CancellationToken cancellationToken)
    {
        return default;
    }

    /// <summary>
    ///     The key that signs the tenant's public card, published at <c>/{tenant}/.well-known/jwks.json</c>. Null (the
    ///     default)
    ///     uses the host's key (<c>A2AHttpOptions.SigningKey</c>), or leaves the card unsigned when there is none.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public virtual ValueTask<AgentCardSigningKey?> GetSigningKeyAsync(string tenant,
        CancellationToken cancellationToken)
    {
        return default;
    }
}