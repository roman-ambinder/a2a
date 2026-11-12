using Microsoft.Agents.AI;

namespace A2A.Extensions.AgentFramework;

/// <summary>The one place that builds the <see cref="AgentSessionStoreKey" /> of a stored session.</summary>
internal static class SessionKeys
{
    internal const string TenantPartition = "a2a.tenant";
    internal const string OwnerPartition = "a2a.owner";

    /// <summary>
    ///     The key of the session for one context of one caller. Tenant and owner are partitions, so the same context id
    ///     never reaches another caller's session. The values are prefixed because <c>WithPartition</c> refuses an empty
    ///     value, and the default tenant is empty.
    /// </summary>
    internal static AgentSessionStoreKey For(string tenant, string owner, string contextId)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrEmpty(contextId);
        return new AgentSessionStoreKey(contextId)
            .WithPartition(TenantPartition, "t:" + tenant)
            .WithPartition(OwnerPartition, "o:" + owner);
    }

    /// <summary>
    ///     The key of a context's <see cref="SessionIndex" />. Its owner partition is <c>i:</c>, which no caller's key can
    ///     have: those are <c>o:</c> and the principal.
    /// </summary>
    internal static AgentSessionStoreKey IndexFor(string tenant, string contextId)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrEmpty(contextId);
        return new AgentSessionStoreKey(contextId)
            .WithPartition(TenantPartition, "t:" + tenant)
            .WithPartition(OwnerPartition, "i:");
    }
}
