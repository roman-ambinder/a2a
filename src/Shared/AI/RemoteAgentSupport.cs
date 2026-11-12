// Shared source, compiled into A2A.Extensions.AI and A2A.Extensions.AgentFramework (linked, not referenced: shipped
// packages do not use InternalsVisibleTo). The namespace differs per assembly so the two internal copies never share a
// full name. All types are internal.

using A2A.Client;
using Microsoft.Extensions.AI;

#if A2A_AGENT_FRAMEWORK
namespace A2A.Extensions.AgentFramework.Shared;
#else
namespace A2A.Extensions.AI.Shared;
#endif

/// <summary>Rules shared by the clients that call a remote A2A agent: what to send, and how to read task states.</summary>
internal static class RemoteAgentSupport
{
    /// <summary>The key of the task id in <c>ChatOptions.AdditionalProperties</c> (same value as <c>A2AChatKeys.TaskId</c>).</summary>
    internal const string TaskIdKey = "a2a.taskId";

    /// <summary>Throws for a state that ended the task without a reply. The text names the state only, never remote text.</summary>
    internal static void ThrowIfFailed(TaskState state)
    {
        if (state is TaskState.Failed or TaskState.Rejected or TaskState.Canceled)
            throw new InvalidOperationException(
                $"The remote agent ended the task in state '{StateName(state)}'.");
    }

    /// <summary>
    ///     The message to send: the trailing run of user messages merged into one A2A message. System and tool messages and
    ///     all message and content properties stay local. <see cref="ChatOptions.ConversationId" /> is the context id, and
    ///     <see cref="TaskIdKey" /> in its additional properties the task id.
    /// </summary>
    /// <exception cref="ArgumentException">There is no trailing user message, or none with content A2A can carry.</exception>
    internal static Message BuildMessage(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var start = list.Count;
        while (start > 0 && list[start - 1].Role == ChatRole.User) start--;
        if (start == list.Count)
            throw new ArgumentException("The messages must end with at least one user message.", nameof(messages));

        var result = new Message { MessageId = Message.NewId(), Role = Role.User, Parts = [] };
        // Message and content properties are caller side data (they can hold anything). They are never sent to the remote agent.
        for (var i = start; i < list.Count; i++)
            foreach (var content in list[i].Contents)
                if (ModelContent.ConvertToPart(content, false) is { } part)
                    result.Parts.Add(part);
        if (result.Parts.Count == 0)
            throw new ArgumentException("The user messages have no content that A2A can carry.", nameof(messages));

        result.ContextId = string.IsNullOrEmpty(options?.ConversationId) ? null : options.ConversationId;
        if (options?.AdditionalProperties is { } extra && extra.TryGetValue(TaskIdKey, out var taskId) &&
            taskId is string id && id.Length > 0)
            result.TaskId = id;
        return result;
    }

    internal static string StateName(TaskState state)
    {
        return state switch
        {
            TaskState.Submitted => "submitted",
            TaskState.Working => "working",
            TaskState.Completed => "completed",
            TaskState.Failed => "failed",
            TaskState.Canceled => "canceled",
            TaskState.Rejected => "rejected",
            TaskState.InputRequired => "input-required",
            TaskState.AuthRequired => "auth-required",
            _ => "unknown"
        };
    }

    internal static bool NeedsWait(TaskState state)
    {
        return !state.IsTerminal() && !state.IsInterrupted();
    }

    /// <summary>
    ///     A task keeps the artifacts of every turn, and replies and stream snapshots show all of them. When continuing a
    ///     task, the
    ///     caller has seen the earlier ones, so look them up first. Best effort: a failed lookup is ignored (the send then
    ///     reports
    ///     the real error), unless <paramref name="callerToken" /> was cancelled. <paramref name="token" /> is the token used
    ///     for the call.
    /// </summary>
    internal static async Task<KnownTask> LookupKnownAsync(A2AClient client, string? taskId, A2ARequestOptions? options,
        CancellationToken token, CancellationToken callerToken)
    {
        if (string.IsNullOrEmpty(taskId)) return KnownTask.Empty;
        try
        {
            var existing = await client.GetTaskAsync(taskId, 0, options, token).ConfigureAwait(false);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (existing.Artifacts is { } artifacts)
                foreach (var artifact in artifacts)
                    ids.Add(artifact.ArtifactId);
            return new KnownTask(ids, existing.Status.Message?.MessageId);
        }
        catch (Exception) when (!callerToken.IsCancellationRequested)
        {
            return KnownTask.Empty;
        }
    }

    /// <summary>What the caller has already seen of a task: its artifact ids and the id of its current status message.</summary>
    internal sealed record KnownTask(HashSet<string> ArtifactIds, string? StatusMessageId)
    {
        internal static KnownTask Empty => new(new HashSet<string>(StringComparer.Ordinal), null);
    }
}