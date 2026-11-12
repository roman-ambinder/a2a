using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using A2A.Server.Coordination;
using A2A.Server.Storage;
using Microsoft.Agents.AI;

namespace A2A.Extensions.AgentFramework;

/// <summary>
///     Which callers have a stored session in a context. Sessions are per caller (<see cref="SessionKeys.For" />), and a
///     context can be shared (<c>A2AServerOptions.ContextAccess</c>), but <see cref="AgentSessionStore" /> cannot list
///     keys. So each context has an index session, under its own key, whose state lists the callers and the pending
///     approval records (<see cref="SessionKeys.ApprovalFor" />); deleting the context deletes every listed caller's
///     session, every listed record, and then the index.
/// </summary>
/// <remarks>
///     <para>
///         The store has no compare-and-set, so two callers saving the index at the same moment could each overwrite the
///         other's entry. With the server's <see cref="ExecutionCoordinator" /> (the executor registered by
///         <c>AddAIAgent</c> has it), the read and save of the index run under an exclusive claim of the coordinator,
///         which the Postgres and Redis coordinators take atomically across instances: no entry is lost. A caller that
///         does not get the claim in time is not recorded by this run; its next run tries again. Without a coordinator
///         (an executor built by hand), two saves at the same moment can still lose one entry, and that caller's session
///         then outlives the context until the store's own expiry.
///     </para>
///     <para>
///         Each process adds a caller once (cached), so the index costs one read and one save (and the claim) per caller
///         and context, not per run.
///     </para>
/// </remarks>
internal static class SessionIndex
{
    internal const string StateKey = "a2a.sessionCallers";

    /// <summary>The state key of the pending approval records of the context (<see cref="SessionKeys.ApprovalFor" />).</summary>
    internal const string ApprovalsKey = "a2a.approvalRecords";

    /// <summary>Most approval records one index lists; the oldest falls out (it then relies on the store's own expiry).</summary>
    internal const int MaxApprovals = 1_000;

    private const int MaxCached = 10_000;

    private static readonly ConcurrentDictionary<(AgentSessionStore Store, string Tenant, string Context, string Caller), byte>
        Known = new();

    /// <summary>The owner of the coordinator claims that guard the index: no principal is prefixed so.</summary>
    internal const string ClaimOwner = "a2a.session-index:";

    /// <summary>
    ///     The prefix of the server's claims that are not executions: an expired one is only removed by the retention sweep,
    ///     no task is read or changed (<c>TaskExecutionEngine.ContinuationClaimPrefix</c>).
    /// </summary>
    internal const string ClaimPrefix = "claim:";

    /// <summary>How long to wait between two tries for the claim.</summary>
    internal static readonly TimeSpan ClaimRetry = TimeSpan.FromMilliseconds(20);

    /// <summary>
    ///     Records that <paramref name="caller" /> has a session in the context, unless this process did already. With
    ///     <paramref name="coordinator" />, under its exclusive claim for the index (<see cref="UpdateAsync" />).
    /// </summary>
    internal static async ValueTask AddAsync(AgentSessionStore store, AIAgent agent, string tenant, string contextId,
        string? generation, string caller, ExecutionCoordinator? coordinator, TimeSpan claimWait,
        CancellationToken cancellationToken)
    {
        var known = (store, tenant, contextId + "/" + generation, caller);
        if (Known.ContainsKey(known)) return;
        await UpdateAsync(store, agent, tenant, contextId, generation, coordinator, claimWait, index =>
        {
            var callers = Read(index);
            if (callers.Contains(caller, StringComparer.Ordinal)) return false;
            callers.Add(caller);
            index.StateBag.SetValue(StateKey, callers, SessionIndexJson.Default.Options);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        if (Known.Count >= MaxCached) Known.Clear();
        Known.TryAdd(known, 0);
    }

    /// <summary>
    ///     Records that the context has a pending approval record of <paramref name="caller" /> for
    ///     <paramref name="taskId" />. One read and one save per request: a request is asked once per approval. With
    ///     <paramref name="coordinator" />, under its exclusive claim for the index (<see cref="UpdateAsync" />).
    /// </summary>
    internal static ValueTask AddApprovalAsync(AgentSessionStore store, AIAgent agent, string tenant,
        string contextId, string? generation, string caller, string taskId, ExecutionCoordinator? coordinator,
        TimeSpan claimWait, CancellationToken cancellationToken)
    {
        return UpdateAsync(store, agent, tenant, contextId, generation, coordinator, claimWait, index =>
        {
            var approvals = ReadApprovals(index);
            var entry = new ApprovalRecordEntry(caller, taskId);
            if (approvals.Contains(entry)) return false;
            approvals.Add(entry);
            if (approvals.Count > MaxApprovals) approvals.RemoveRange(0, approvals.Count - MaxApprovals);
            index.StateBag.SetValue(ApprovalsKey, approvals, SessionIndexJson.Default.Options);
            return true;
        }, cancellationToken);
    }

    /// <summary>
    ///     Reads the index, applies <paramref name="change" /> and saves it when it returns true. With
    ///     <paramref name="coordinator" />, under its exclusive claim for the index: a claim another writer holds is tried
    ///     again for up to <paramref name="claimWait" />, then <see cref="TimeoutException" /> (nothing written).
    /// </summary>
    private static async ValueTask UpdateAsync(AgentSessionStore store, AIAgent agent, string tenant, string contextId,
        string? generation, ExecutionCoordinator? coordinator, TimeSpan claimWait, Func<AgentSession, bool> change,
        CancellationToken cancellationToken)
    {
        var claimKey = new TaskKey(tenant, ClaimOwner, contextId + "/" + generation);
        var claim = ClaimPrefix + Guid.NewGuid().ToString("N");
        try
        {
            if (coordinator is not null)
            {
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (!await coordinator.TryRegisterExclusiveAsync(claimKey, claim, cancellationToken)
                           .ConfigureAwait(false))
                {
                    if (waited.Elapsed >= claimWait)
                        throw new TimeoutException("Another caller holds the context's session index.");
                    await Task.Delay(ClaimRetry, cancellationToken).ConfigureAwait(false);
                }
            }

            var key = SessionKeys.IndexFor(tenant, contextId, generation);
            var index = await store.GetOrCreateSessionAsync(agent, key, cancellationToken).ConfigureAwait(false);
            if (change(index)) await store.SaveSessionAsync(agent, key, index, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Also after a failure or a timeout (a claim call can fail after it was taken): a claim left behind would hold
            // the next writer up to the lease time. Unregistering a claim that was not taken does nothing.
            if (coordinator is not null)
                await coordinator.UnregisterAsync(claimKey, claim, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Deletes the session of <paramref name="owner" /> and of every caller in the context's index, every approval
    ///     record it lists, then the index.
    /// </summary>
    /// <remarks>
    ///     Only those of <paramref name="generation" /> (the deleted context's): a context created again under the same id
    ///     has another generation, so its sessions are never reached (approval records are per task, and its tasks are
    ///     new). The sweep deletes a context only when no task uses it, so no run of that generation adds to its index
    ///     meanwhile.
    /// </remarks>
    internal static async ValueTask DeleteAllAsync(AgentSessionStore store, AIAgent agent, string tenant, string contextId,
        string? generation, string owner, CancellationToken cancellationToken)
    {
        await store.DeleteSessionAsync(agent, SessionKeys.For(tenant, owner, contextId, generation), cancellationToken)
            .ConfigureAwait(false);
        var key = SessionKeys.IndexFor(tenant, contextId, generation);
        if (await store.GetSessionAsync(agent, key, cancellationToken).ConfigureAwait(false) is { } index)
        {
            foreach (var caller in Read(index))
            {
                if (string.Equals(caller, owner, StringComparison.Ordinal)) continue;
                await store.DeleteSessionAsync(agent, SessionKeys.For(tenant, caller, contextId, generation),
                    cancellationToken).ConfigureAwait(false);
            }

            // Answered requests are deleted when they are taken; deleting one that is gone already does nothing.
            foreach (var approval in ReadApprovals(index))
                await store.DeleteSessionAsync(agent, SessionKeys.ApprovalFor(tenant, approval.Caller, contextId,
                    approval.TaskId), cancellationToken).ConfigureAwait(false);

            await store.DeleteSessionAsync(agent, key, cancellationToken).ConfigureAwait(false);
        }

        var cached = contextId + "/" + generation;
        foreach (var k in Known.Keys)
            if (ReferenceEquals(k.Store, store) && k.Tenant == tenant && k.Context == cached)
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

    private static List<ApprovalRecordEntry> ReadApprovals(AgentSession index)
    {
        try
        {
            if (index.StateBag.TryGetValue(ApprovalsKey, out List<ApprovalRecordEntry>? approvals,
                    SessionIndexJson.Default.Options) && approvals is not null)
                return approvals.Where(a => a is not null && !string.IsNullOrEmpty(a.Caller) &&
                                            !string.IsNullOrEmpty(a.TaskId)).ToList();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            // Unreadable: no records known. They then rely on the store's own expiry.
        }

        return [];
    }
}

/// <summary>One pending approval record of a context: whose, and for which task.</summary>
internal sealed record ApprovalRecordEntry(string Caller, string TaskId);

[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<ApprovalRecordEntry>))]
internal sealed partial class SessionIndexJson : JsonSerializerContext;
