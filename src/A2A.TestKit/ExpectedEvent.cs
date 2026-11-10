namespace A2A.TestKit;

/// <summary>
///     Describes one event you expect in a stream. Use it with <see cref="A2AAssert.EventOrder" /> and
///     <see cref="A2AAssert.ContainsEventOrder" />.
/// </summary>
public sealed class ExpectedEvent
{
    private readonly string _description;
    private readonly Func<StreamResponse, bool> _predicate;

    private ExpectedEvent(string description, Func<StreamResponse, bool> predicate)
    {
        _description = description;
        _predicate = predicate;
    }

    /// <summary>A task event (the first event of a new task), in <paramref name="state" /> when given.</summary>
    public static ExpectedEvent Task(TaskState? state = null)
    {
        return new ExpectedEvent(
            $"Task({(state is { } s ? TestEvents.StateName(s) : "any")})",
            e => e is TaskEvent t && (state is null || t.Task.Status.State == state));
    }

    /// <summary>A message event.</summary>
    public static ExpectedEvent Message()
    {
        return new ExpectedEvent("Message(any)", e => e is MessageEvent);
    }

    /// <summary>A status update event with <paramref name="state" />.</summary>
    public static ExpectedEvent Status(TaskState state)
    {
        return new ExpectedEvent(
            $"Status({TestEvents.StateName(state)})",
            e => e is StatusUpdateEvent u && u.StatusUpdate.Status.State == state);
    }

    /// <summary>
    ///     An artifact update event. <paramref name="nameOrId" /> matches the artifact name or id; null matches any
    ///     artifact.
    /// </summary>
    public static ExpectedEvent Artifact(string? nameOrId = null)
    {
        return new ExpectedEvent(
            $"Artifact({nameOrId ?? "any"})",
            e => e is ArtifactUpdateEvent a && (nameOrId is null || a.ArtifactUpdate.Artifact.Name == nameOrId ||
                                                a.ArtifactUpdate.Artifact.ArtifactId == nameOrId));
    }

    /// <summary>
    ///     An event that satisfies <paramref name="predicate" />. <paramref name="description" /> is shown in failure
    ///     messages.
    /// </summary>
    public static ExpectedEvent Matching(string description, Func<StreamResponse, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(predicate);
        return new ExpectedEvent(description, predicate);
    }

    internal bool Matches(StreamResponse response)
    {
        return _predicate(response);
    }

    /// <summary>A readable description, such as <c>Status(COMPLETED)</c>.</summary>
    public override string ToString()
    {
        return _description;
    }
}