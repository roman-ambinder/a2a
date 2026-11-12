using System.Globalization;
using Microsoft.Agents.AI;

namespace A2A.Extensions.AgentFramework;

/// <summary>The one place that builds the <see cref="AgentSessionStoreKey" /> of a stored session.</summary>
internal static class SessionKeys
{
    internal const string TenantPartition = "a2a.tenant";
    internal const string OwnerPartition = "a2a.owner";
    internal const string ContextPartition = "a2a.context";

    /// <summary>
    ///     Which incarnation of a context a session belongs to: the context's creation time, in whole microseconds (the
    ///     finest any store keeps). The retention sweep deletes a context and a new message can create it again under the
    ///     same id; its sessions then have other keys, so deleting the old context's sessions never reaches them.
    /// </summary>
    internal static string Generation(DateTimeOffset createdAt)
    {
        return "g:" + (createdAt.UtcTicks / 10).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     The key of the session for one context of one caller. Tenant and owner are partitions, so the same context id
    ///     never reaches another caller's session. The values are prefixed because <c>WithPartition</c> refuses an empty
    ///     value, and the default tenant is empty.
    /// </summary>
    /// <remarks>
    ///     The generation is the context's <see cref="Generation" />, or null for the key without one (an executor built
    ///     without the task store).
    /// </remarks>
    internal static AgentSessionStoreKey For(string tenant, string owner, string contextId, string? generation)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrEmpty(contextId);
        return WithGeneration(new AgentSessionStoreKey(contextId)
            .WithPartition(TenantPartition, "t:" + tenant)
            .WithPartition(OwnerPartition, "o:" + owner), generation);
    }

    /// <summary>
    ///     The key of a context's <see cref="SessionIndex" />. Its owner partition is <c>i:</c>, which no caller's key can
    ///     have: those are <c>o:</c> and the principal.
    /// </summary>
    internal static AgentSessionStoreKey IndexFor(string tenant, string contextId, string? generation)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrEmpty(contextId);
        return WithGeneration(new AgentSessionStoreKey(contextId)
            .WithPartition(TenantPartition, "t:" + tenant)
            .WithPartition(OwnerPartition, "i:"), generation);
    }

    private static AgentSessionStoreKey WithGeneration(AgentSessionStoreKey key, string? generation)
    {
        return generation is null ? key : key.WithPartition(ContextPartition, generation);
    }
}
