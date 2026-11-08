using A2A.Client;

namespace A2A.Extensions.AI;

internal static class RemoteAgentSupport
{
    internal static string StateName(TaskState state) => state switch
    {
        TaskState.Submitted => "submitted",
        TaskState.Working => "working",
        TaskState.Completed => "completed",
        TaskState.Failed => "failed",
        TaskState.Canceled => "canceled",
        TaskState.Rejected => "rejected",
        TaskState.InputRequired => "input-required",
        TaskState.AuthRequired => "auth-required",
        _ => "unknown",
    };

    internal static bool NeedsWait(TaskState state) => !state.IsTerminal() && !state.IsInterrupted();

    /// <summary>What the caller has already seen of a task: its artifact ids and the id of its current status message.</summary>
    internal sealed record KnownTask(HashSet<string> ArtifactIds, string? StatusMessageId)
    {
        internal static KnownTask Empty => new(new HashSet<string>(StringComparer.Ordinal), null);
    }

    /// <summary>
    /// A task keeps the artifacts of every turn, and replies and stream snapshots show all of them. When continuing a task, the
    /// caller has seen the earlier ones, so look them up first. Best effort: a failed lookup is ignored (the send then reports
    /// the real error), unless <paramref name="callerToken"/> was cancelled. <paramref name="token"/> is the token used for the call.
    /// </summary>
    internal static async Task<KnownTask> LookupKnownAsync(A2AClient client, string? taskId, A2ARequestOptions? options, CancellationToken token, CancellationToken callerToken)
    {
        if (string.IsNullOrEmpty(taskId)) return KnownTask.Empty;
        try
        {
            var existing = await client.GetTaskAsync(taskId, 0, options, token).ConfigureAwait(false);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (existing.Artifacts is { } artifacts)
                foreach (var artifact in artifacts) ids.Add(artifact.ArtifactId);
            return new KnownTask(ids, existing.Status.Message?.MessageId);
        }
        catch (Exception) when (!callerToken.IsCancellationRequested)
        {
            return KnownTask.Empty;
        }
    }
}
