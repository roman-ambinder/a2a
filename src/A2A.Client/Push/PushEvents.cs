namespace A2A.Client.Push;

/// <summary>What <see cref="PushTaskTracker"/> and the client outbox both need to know about a push event.</summary>
internal static class PushEvents
{
    /// <summary>Terminal or interrupted: no more notifications come until the caller acts. Same rule as <see cref="A2AClient.WaitForTaskAsync"/>.</summary>
    public static bool IsFinal(TaskState state) => state.IsTerminal() || state.IsInterrupted();

    /// <summary>The task id of an event and the state it carries (null when the event has no state).</summary>
    public static (string? TaskId, TaskState? State) Inspect(StreamResponse evt) => evt switch
    {
        TaskEvent e => (e.Task.Id, e.Task.Status.State),
        StatusUpdateEvent e => (e.StatusUpdate.TaskId, e.StatusUpdate.Status.State),
        ArtifactUpdateEvent e => (e.ArtifactUpdate.TaskId, null),
        MessageUpdateEvent e => (e.MessageUpdate.TaskId, null),
        _ => (null, null),
    };
}
