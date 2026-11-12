namespace A2A.Server;

/// <summary>Writes the A2A 1.1 timeline of a stored task (<see cref="AgentTask.Timeline" />).</summary>
internal static class TaskTimeline
{
    /// <summary>
    ///     Appends <paramref name="entry" /> to the timeline of <paramref name="task" />, then drops the oldest entries beyond
    ///     <paramref name="maxEntries" /> (<see cref="A2ALimits.MaxTimelineEntries" />; zero or negative keeps them all).
    ///     <paramref name="task" /> must be a copy the caller owns (<see cref="ModelCopies.CopyForUpdate" />): its timeline list
    ///     is changed in place. The kept entries keep their generations, and a dropped entry's message stays in the history.
    ///     O(1) amortized (ADR-004): the timeline list is not built (<see cref="ModelCopies.AppendToTimeline" />), and dropping
    ///     the oldest entries only moves the start of the shared version.
    /// </summary>
    public static void Append(AgentTask task, TimelineEntry entry, int maxEntries)
    {
        task.AppendToTimeline(entry, maxEntries);
    }
}
