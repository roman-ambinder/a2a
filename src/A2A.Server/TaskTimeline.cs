namespace A2A.Server;

/// <summary>Writes the A2A 1.1 timeline of a stored task (<see cref="AgentTask.Timeline" />).</summary>
internal static class TaskTimeline
{
    /// <summary>
    ///     Appends <paramref name="entry" /> to the timeline of <paramref name="task" />, then drops the oldest entries beyond
    ///     <paramref name="maxEntries" /> (<see cref="A2ALimits.MaxTimelineEntries" />; zero or negative keeps them all).
    ///     <paramref name="task" /> must be a copy the caller owns (<see cref="ModelCopies.CopyForUpdate" />): its timeline list
    ///     is changed in place. The kept entries keep their generations, and a dropped entry's message stays in the history.
    /// </summary>
    public static void Append(AgentTask task, TimelineEntry entry, int maxEntries)
    {
        var timeline = task.Timeline ??= [];
        timeline.Add(entry);
        if (maxEntries > 0 && timeline.Count > maxEntries) timeline.RemoveRange(0, timeline.Count - maxEntries);
    }
}
