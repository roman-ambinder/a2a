using A2A.Server.Agents;
using A2A.Signing;

namespace A2A.Server;

/// <summary>
///     Supplies the Agent Card the server serves publicly and checks capabilities against (streaming, push notifications,
///     the
///     extended card, declared extensions): the host's card (<see cref="Get" />), and the card of each tenant's agent
///     (<see cref="GetAsync" />) when one host serves many agents.
/// </summary>
/// <remarks>
///     The card is the host's <see cref="A2AServerOptions.AgentCard" /> completed once: a copy, never the host's instance,
///     with one
///     entry in <see cref="AgentCapabilities.Extensions" /> for every registered extension operation that the card does
///     not list.
///     The ASP.NET Core package also fills an empty <see cref="AgentCard.SupportedInterfaces" /> from the mapped bindings.
///     Implementations must return the same instance on every call once the host has started (the ASP.NET Core completer
///     may build a provisional card before start, when the listen addresses are unknown, and does not keep it), and must
///     not change it after returning it: it is shared by
///     every request and by the cached, signed card.
/// </remarks>
public interface IAgentCardProvider
{
    /// <summary>The completed Agent Card of the host. The same instance on every call. Callers must not change it.</summary>
    AgentCard Get();

    /// <summary>
    ///     The completed card of the agent that serves <paramref name="scope" />'s tenant, or null when no agent serves it.
    ///     Callers
    ///     must not change it. Default: <see cref="Get" /> for every tenant (one agent serves every tenant).
    /// </summary>
    /// <param name="scope">The tenant and route values of the request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<AgentCard?> GetAsync(AgentRequestScope scope, CancellationToken cancellationToken)
    {
        return new ValueTask<AgentCard?>(Get());
    }

    /// <summary>
    ///     The extended card for an authenticated caller of <see cref="ServerCallContext.Tenant" /> (spec §3.1.11), or null
    ///     when none
    ///     is configured. <paramref name="publicCard" /> is the tenant's completed public card (from <see cref="GetAsync" />):
    ///     copy it,
    ///     do not change it. Default: null.
    /// </summary>
    /// <param name="call">The caller.</param>
    /// <param name="publicCard">The tenant's completed public card.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<AgentCard?> GetExtendedAsync(ServerCallContext call, AgentCard publicCard,
        CancellationToken cancellationToken)
    {
        return default;
    }

    /// <summary>
    ///     The key that signs <paramref name="tenant" />'s public card, or null to use the host's key (or none). Default:
    ///     null.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<AgentCardSigningKey?> GetSigningKeyAsync(string tenant, CancellationToken cancellationToken)
    {
        return default;
    }
}