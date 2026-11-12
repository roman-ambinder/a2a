using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace A2A.Extensions.AgentFramework;

/// <summary>
///     An approval request the agent made, kept in the session until the caller answers. Bound to the task and to the
///     approval hash the caller was shown: sessions are per context, approvals per task, so a later request of another task
///     in the context must never be answered with this task's approval.
/// </summary>
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
    ///     The session's pending requests, one per task, newest last. Each task keeps its own: another task in the context
    ///     neither answers nor removes it.
    /// </summary>
    internal const string ListKey = "a2a.pendingApprovals";

    /// <summary>Most pending requests one session keeps. A task that is never answered falls out once this many are newer.</summary>
    internal const int MaxPending = 16;

    /// <summary>Stores <paramref name="value" /> as its task's pending request, replacing an older one of the same task.</summary>
    internal static void Write(AgentSession session, PendingToolApproval value)
    {
        var list = ReadList(session);
        list.RemoveAll(p => string.Equals(p.TaskId, value.TaskId, StringComparison.Ordinal));
        list.Add(value);
        if (list.Count > MaxPending) list.RemoveRange(0, list.Count - MaxPending);
        session.StateBag.SetValue(ListKey, list, PendingApprovalJson.Default.Options);
    }

    /// <summary>
    ///     Takes the pending request of <paramref name="taskId" /> out of the session: it is answered or stale either way.
    ///     Other tasks' requests stay. Null when the task has none, or its entry is unreadable. An entry in the single slot
    ///     of earlier versions (<see cref="StateKey" />) is taken too when it is this task's, and kept otherwise.
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
