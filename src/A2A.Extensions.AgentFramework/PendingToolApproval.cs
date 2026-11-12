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

    internal static void Write(AgentSession session, PendingToolApproval value) =>
        session.StateBag.SetValue(StateKey, value, PendingApprovalJson.Default.Options);

    /// <summary>Takes the entry out of the session: it is answered or stale either way. Null when there is none or it is unreadable.</summary>
    internal static PendingToolApproval? Take(AgentSession session)
    {
        PendingToolApproval? value = null;
        try
        {
            session.StateBag.TryGetValue(StateKey, out value, PendingApprovalJson.Default.Options);
        }
        catch (JsonException)
        {
            // Unreadable: treated as stale, like no entry.
        }

        session.StateBag.TryRemoveValue(StateKey);
        return value;
    }
}

[JsonSerializable(typeof(PendingToolApproval))]
internal sealed partial class PendingApprovalJson : JsonSerializerContext;
