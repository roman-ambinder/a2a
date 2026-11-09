using A2A.Server.Streaming;

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
    public static AgentTask Shape(AgentTask task, int? historyLength, A2ALimits limits, bool includeArtifacts = true, int? timelineLength = null)
    {
        return new AgentTask
        {
            Id = task.Id,
            ContextId = task.ContextId,
            Status = task.Status,
            Artifacts = includeArtifacts ? task.Artifacts : null,
            History = Newest(task.History, historyLength, limits.MaxHistoryLengthReturned),
            Metadata = task.Metadata,
            Generation = task.Generation,
            // A2A 1.1: timelineLength follows the historyLength rule (§3.2.4), with its own cap.
            Timeline = Newest(task.Timeline, timelineLength, limits.MaxTimelineLengthReturned),
        };
    }

    /// <summary>The newest <c>min(requested, cap)</c> items (unset means the cap), the list itself when it is short enough, or null for none.</summary>
    private static List<T>? Newest<T>(List<T>? items, int? requested, int cap)
    {
        var n = Math.Min(requested ?? int.MaxValue, cap);
        if (n <= 0 || items is not { Count: > 0 }) return null;
        return items.Count > n ? items.GetRange(items.Count - n, n) : items;
    }

    /// <summary>
    /// Shapes a stream event: a <see cref="TaskEvent"/> whose history is longer than the limit of <paramref name="historyLength"/>
    /// and the cap is re-encoded with the cut history and the same sequence. Any other event, and a task event that needs no cut,
    /// is returned as it is. The stored event is not changed.
    /// </summary>
    public static SequencedEvent ShapeEvent(SequencedEvent evt, int? historyLength, A2ALimits limits, int? timelineLength = null)
    {
        if (evt.Event is not TaskEvent { Task: var task }) return evt;
        if (!Exceeds(task.History, historyLength, limits.MaxHistoryLengthReturned) && !Exceeds(task.Timeline, timelineLength, limits.MaxTimelineLengthReturned))
            return evt;
        return EventEncoder.Encode(evt.Sequence, StreamResponse.Of(Shape(task, historyLength, limits, timelineLength: timelineLength)));
    }

    private static bool Exceeds<T>(List<T>? items, int? requested, int cap) =>
        items is { Count: > 0 } && items.Count > Math.Max(Math.Min(requested ?? int.MaxValue, cap), 0);
}
