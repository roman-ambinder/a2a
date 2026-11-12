using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace A2A.Extensions.AgentFramework;

/// <summary>
///     An approval request the agent made, kept until the caller answers. Bound to the task and to the approval hash the
///     caller was shown: sessions are per context, approvals per task, so a later request of another task in the context
///     must never be answered with this task's approval.
/// </summary>
/// <remarks>
///     Each request is a record of its own in the session store (<see cref="SessionKeys.ApprovalFor" />), not part of the
///     caller's session: two tasks of one context can run at the same time, and the one that saves the session last would
///     otherwise drop the other's request. Requests that earlier versions kept in the session (<see cref="ListKey" />,
///     <see cref="StateKey" />) are still read by <see cref="Take" />.
/// </remarks>
/// <param name="TaskId">The task that asked.</param>
/// <param name="Hash">The <see cref="A2A.Server.PendingApproval.Hash" /> the caller was shown.</param>
/// <param name="Request">The <see cref="ToolApprovalRequestContent" />, as M.E.AI JSON.</param>
internal sealed record PendingToolApproval(string TaskId, string Hash, JsonElement Request)
{
    internal const string StateKey = "a2a.pendingApproval";

    /// <summary>M.E.AI's source-generated contracts: the AIContent hierarchy with its type discriminators (AOT safe).</summary>
    private static JsonSerializerOptions ContentOptions => AIJsonUtilities.DefaultOptions;

    internal static PendingToolApproval Create(string taskId, string hash, ToolApprovalRequestContent request) =>
        new(taskId, hash, JsonSerializer.SerializeToElement(request, ContentOptions.GetTypeInfo(typeof(AIContent))));

    /// <summary>The request, or null when the stored JSON is not one.</summary>
    internal ToolApprovalRequestContent? ReadRequest()
    {
        try
        {
            return JsonSerializer.Deserialize(Request, ContentOptions.GetTypeInfo(typeof(AIContent))) as ToolApprovalRequestContent;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Where the previous version kept a session's pending requests, one per task. Only read now (<see cref="Take" />):
    ///     new requests are records of their own.
    /// </summary>
    internal const string ListKey = "a2a.pendingApprovals";

    /// <summary>
    ///     Takes the pending request of <paramref name="taskId" /> that an earlier version kept in the session out of it:
    ///     it is answered or stale either way. Other tasks' requests stay. Null when the task has none, or its entry is
    ///     unreadable. Reads the list of the previous version (<see cref="ListKey" />) and the single slot of the one
    ///     before (<see cref="StateKey" />), which is taken when it is this task's and kept otherwise.
    /// </summary>
    internal static PendingToolApproval? Take(AgentSession session, string taskId)
    {
        PendingToolApproval? found = null;
        var list = ReadList(session);
        var index = list.FindIndex(p => string.Equals(p.TaskId, taskId, StringComparison.Ordinal));
        if (index >= 0)
        {
            found = list[index];
            list.RemoveAt(index);
            if (list.Count == 0) session.StateBag.TryRemoveValue(ListKey);
            else session.StateBag.SetValue(ListKey, list, PendingApprovalJson.Default.Options);
        }

        PendingToolApproval? legacy = null;
        var readable = true;
        try
        {
            session.StateBag.TryGetValue(StateKey, out legacy, PendingApprovalJson.Default.Options);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            readable = false; // unreadable, or a value of another type: stale, like no entry
        }

        if (!readable || (legacy is not null && string.Equals(legacy.TaskId, taskId, StringComparison.Ordinal)))
        {
            session.StateBag.TryRemoveValue(StateKey);
            found ??= readable ? legacy : null;
        }

        return found;
    }

    /// <summary>The state key of the request in its own record (<see cref="SaveRecordAsync" />).</summary>
    internal const string RecordKey = "a2a.approvalRequest";

    /// <summary>Saves <paramref name="value" /> as the record under <paramref name="key" />, replacing an older one.</summary>
    internal static async ValueTask SaveRecordAsync(AgentSessionStore store, AIAgent agent, AgentSessionStoreKey key,
        PendingToolApproval value, CancellationToken cancellationToken)
    {
        var record = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        record.StateBag.SetValue(RecordKey, value, PendingApprovalJson.Default.Options);
        await store.SaveSessionAsync(agent, key, record, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Takes the record under <paramref name="key" /> out of the store: it is answered or stale either way. Null when
    ///     there is none, or it is unreadable (it is deleted then too).
    /// </summary>
    internal static async ValueTask<PendingToolApproval?> TakeRecordAsync(AgentSessionStore store, AIAgent agent,
        AgentSessionStoreKey key, CancellationToken cancellationToken)
    {
        if (await store.GetSessionAsync(agent, key, cancellationToken).ConfigureAwait(false) is not { } record)
            return null;
        PendingToolApproval? value = null;
        try
        {
            record.StateBag.TryGetValue(RecordKey, out value, PendingApprovalJson.Default.Options);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            value = null; // unreadable, or a value of another type: stale, like no record
        }

        await store.DeleteSessionAsync(agent, key, cancellationToken).ConfigureAwait(false);
        return value;
    }

    private static List<PendingToolApproval> ReadList(AgentSession session)
    {
        try
        {
            if (session.StateBag.TryGetValue(ListKey, out List<PendingToolApproval>? list, PendingApprovalJson.Default.Options) &&
                list is not null)
                return list.Where(p => p is not null).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Unreadable, or a value of another type: treated as stale, like no entries.
        }

        return [];
    }
}

[JsonSerializable(typeof(PendingToolApproval))]
[JsonSerializable(typeof(List<PendingToolApproval>))]
internal sealed partial class PendingApprovalJson : JsonSerializerContext;
