namespace A2A.Server.Agents;

/// <summary>
/// What an <see cref="IAgentCardProvider"/> (and a <see cref="TenantCardProvider"/>) knows about a request when it picks the agent's card. One agent serves one tenant,
/// so <see cref="Tenant"/> alone must identify the agent: background work (retention, cancel requests from other instances,
/// push delivery) has only the tenant.
/// </summary>
/// <param name="Tenant">The request's tenant (<see cref="ServerCallContext.Tenant"/>). <c>""</c> is the default tenant.</param>
/// <param name="RouteValues">
/// The route values of the HTTP request (for example <c>tenant</c>), as strings. Empty for background work and for hosts
/// without routes.
/// </param>
public sealed record AgentRequestScope(string Tenant, IReadOnlyDictionary<string, string?> RouteValues)
{
    /// <summary>No route values.</summary>
    public static readonly IReadOnlyDictionary<string, string?> NoRouteValues = new Dictionary<string, string?>();

    /// <summary>A scope with only a tenant, as background work sees it.</summary>
    public static AgentRequestScope ForTenant(string tenant) => new(tenant, NoRouteValues);
}
