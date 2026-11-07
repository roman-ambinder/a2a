namespace A2A.Server.Pipeline;

/// <summary>Shapes a stored task for a response: history length and artifact inclusion.</summary>
internal static class TaskShaper
{
    /// <summary>
    /// Applies historyLength (0 = none, n = last n) and artifact inclusion. The history is capped at
    /// <see cref="A2ALimits.MaxHistoryLengthReturned"/> also when <paramref name="historyLength"/> is null or larger.
    /// </summary>
    /// <remarks>
    /// Returns a new <see cref="AgentTask"/> that shares everything it does not change with <paramref name="task"/>, which the
    /// store owns: the status, the metadata, the messages and artifacts, and the history and artifact lists when they are kept
    /// whole (a new history list only when it is trimmed). That is safe because the result is serialized at once and never
    /// kept or changed.
    /// </remarks>
    public static AgentTask Shape(AgentTask task, int? historyLength, A2ALimits limits, bool includeArtifacts = true)
    {
        var n = Math.Min(historyLength ?? int.MaxValue, limits.MaxHistoryLengthReturned);
        var history = task.History;
        if (n <= 0 || history is not { Count: > 0 }) history = null;
        else if (history.Count > n) history = history.GetRange(history.Count - n, n);
        return new AgentTask
        {
            Id = task.Id,
            ContextId = task.ContextId,
            Status = task.Status,
            Artifacts = includeArtifacts ? task.Artifacts : null,
            History = history,
            Metadata = task.Metadata,
        };
    }
}
