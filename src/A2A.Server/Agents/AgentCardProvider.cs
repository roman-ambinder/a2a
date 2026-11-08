using A2A.Signing;
using Microsoft.Extensions.Options;

namespace A2A.Server.Agents;

/// <summary>
/// Supplies the Agent Card of the agent that serves a tenant, so one host can serve many agents (one agent per tenant).
/// Register one with <c>AddAgentCardProvider&lt;T&gt;()</c>; without it, <see cref="A2AServerOptions.AgentCard"/> serves every
/// tenant. The server asks for the card on every request (the public card endpoint, and each operation once) and for every
/// stored event of a task (push capability), and never caches it, so a provider can change cards at runtime; cache in the
/// provider if building a card is expensive.
/// </summary>
/// <remarks>
/// <para>
/// A tenant whose card is null has no agent: its public card is HTTP 404; SendMessage, SendStreamingMessage, ListTasks and
/// GetExtendedAgentCard are refused with <see cref="A2AErrorCode.UnsupportedOperation"/> ("No agent is configured for this
/// tenant."); every operation on a task (GetTask, CancelTask, SubscribeToTask, push configs) answers TaskNotFound, exactly as
/// for a missing task.
/// </para>
/// <para>
/// Every capability check of a request uses the card of its tenant: streaming, push notifications, extensions (required and
/// activated) and the extended card. Tenants compare ordinally.
/// </para>
/// </remarks>
public abstract class AgentCardProvider
{
    /// <summary>The public card of the agent serving <paramref name="scope"/>'s tenant, or null when no agent serves it.</summary>
    public abstract ValueTask<AgentCard?> GetCardAsync(AgentRequestScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// The extended card for an authenticated caller of the tenant <see cref="ServerCallContext.Tenant"/> (spec §3.1.11). Only
    /// asked when the tenant's public card sets <c>capabilities.extendedAgentCard</c>. Null answers
    /// <see cref="A2AErrorCode.ExtendedAgentCardNotConfigured"/>. Default: null.
    /// </summary>
    public virtual ValueTask<AgentCard?> GetExtendedCardAsync(ServerCallContext call, CancellationToken cancellationToken) => default;

    /// <summary>
    /// The key that signs the tenant's public card, published at <c>/{tenant}/.well-known/jwks.json</c>. Null (the default)
    /// uses the host's key (<c>A2AHttpOptions.SigningKey</c>), or leaves the card unsigned when there is none.
    /// </summary>
    public virtual ValueTask<AgentCardSigningKey?> GetSigningKeyAsync(string tenant, CancellationToken cancellationToken) => default;
}

/// <summary>
/// The provider used when none is registered: <see cref="A2AServerOptions.AgentCard"/> and
/// <see cref="A2AServerOptions.ExtendedAgentCardProvider"/> for every tenant, and the host's signing key.
/// </summary>
internal sealed class DefaultAgentCardProvider(IOptions<A2AServerOptions> options) : AgentCardProvider
{
    public override ValueTask<AgentCard?> GetCardAsync(AgentRequestScope scope, CancellationToken cancellationToken) =>
        ValueTask.FromResult<AgentCard?>(options.Value.AgentCard);

    public override ValueTask<AgentCard?> GetExtendedCardAsync(ServerCallContext call, CancellationToken cancellationToken) =>
        options.Value.ExtendedAgentCardProvider is { } provider ? provider(call, cancellationToken) : default;
}
