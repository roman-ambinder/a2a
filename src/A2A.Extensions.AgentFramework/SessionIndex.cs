using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;

namespace A2A.Extensions.AgentFramework;

/// <summary>
///     Which callers have a stored session in a context. Sessions are per caller (<see cref="SessionKeys.For" />), and a
///     context can be shared (<c>A2AServerOptions.ContextAccess</c>), but <see cref="AgentSessionStore" /> cannot list
///     keys. So each context has an index session, under its own key, whose state lists the callers; deleting the context
///     deletes every listed caller's session and then the index.
/// </summary>
/// <remarks>
///     The store has no compare-and-set, so two callers saving the index at the same moment can lose one entry, and that
///     caller's session then outlives the context. Each process adds a caller once (cached), so the index costs one read
///     and one save per caller and context, not per run.
/// </remarks>
internal static class SessionIndex
{
    internal const string StateKey = "a2a.sessionCallers";
    private const int MaxCached = 10_000;

    private static readonly ConcurrentDictionary<(AgentSessionStore Store, string Tenant, string Context, string Caller), byte>
        Known = new();

    /// <summary>Records that <paramref name="caller" /> has a session in the context, unless this process did already.</summary>
    internal static async ValueTask AddAsync(AgentSessionStore store, AIAgent agent, string tenant, string contextId,
        string caller, CancellationToken cancellationToken)
    {
        var known = (store, tenant, contextId, caller);
        if (Known.ContainsKey(known)) return;
        var key = SessionKeys.IndexFor(tenant, contextId);
        var index = await store.GetOrCreateSessionAsync(agent, key, cancellationToken).ConfigureAwait(false);
        var callers = Read(index);
        if (!callers.Contains(caller, StringComparer.Ordinal))
        {
            callers.Add(caller);
            index.StateBag.SetValue(StateKey, callers, SessionIndexJson.Default.Options);
            await store.SaveSessionAsync(agent, key, index, cancellationToken).ConfigureAwait(false);
        }

        if (Known.Count >= MaxCached) Known.Clear();
        Known.TryAdd(known, 0);
    }

    /// <summary>
    ///     Deletes the session of <paramref name="owner" /> and of every caller in the context's index, then the index.
    ///     Returns the callers whose sessions were deleted.
    /// </summary>
    internal static async ValueTask DeleteAllAsync(AgentSessionStore store, AIAgent agent, string tenant, string contextId,
        string owner, CancellationToken cancellationToken)
    {
        await store.DeleteSessionAsync(agent, SessionKeys.For(tenant, owner, contextId), cancellationToken)
            .ConfigureAwait(false);
        var key = SessionKeys.IndexFor(tenant, contextId);
        if (await store.GetSessionAsync(agent, key, cancellationToken).ConfigureAwait(false) is not { } index) return;
        foreach (var caller in Read(index))
        {
            if (string.Equals(caller, owner, StringComparison.Ordinal)) continue;
            await store.DeleteSessionAsync(agent, SessionKeys.For(tenant, caller, contextId), cancellationToken)
                .ConfigureAwait(false);
        }

        await store.DeleteSessionAsync(agent, key, cancellationToken).ConfigureAwait(false);
        foreach (var k in Known.Keys)
            if (ReferenceEquals(k.Store, store) && k.Tenant == tenant && k.Context == contextId)
                Known.TryRemove(k, out _);
    }

    private static List<string> Read(AgentSession index)
    {
        try
        {
            if (index.StateBag.TryGetValue(StateKey, out List<string>? callers, SessionIndexJson.Default.Options) &&
                callers is not null)
                return callers.Where(c => !string.IsNullOrEmpty(c)).ToList();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            // Unreadable: no callers known. Their sessions then rely on the store's own expiry.
        }

        return [];
    }
}

[JsonSerializable(typeof(List<string>))]
internal sealed partial class SessionIndexJson : JsonSerializerContext;
