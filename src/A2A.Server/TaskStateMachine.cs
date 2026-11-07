namespace A2A.Server;

/// <summary>Which task states accept another user message on the same task id.</summary>
public enum FollowUpPolicy
{
    /// <summary>Only <c>InputRequired</c> and <c>AuthRequired</c> tasks accept follow-up messages (spec default).</summary>
    InterruptedOnly,

    /// <summary>Also lets callers steer <c>Submitted</c> and <c>Working</c> tasks.</summary>
    AllowSteering,
}

/// <summary>What to do with a contextId the client sends for a new task when the server has never seen it.</summary>
public enum ClientContextIdPolicy
{
    /// <summary>Reject with InvalidParams "contextId not found".</summary>
    Reject,

    /// <summary>Create the context for the caller and use the client's id.</summary>
    AdoptIfUnknown,
}

/// <summary>Task lifecycle rules. Terminal states are final: first writer wins.</summary>
public static class TaskStateMachine
{
    private static bool IsFinal(TaskState s) => s is TaskState.Completed or TaskState.Failed or TaskState.Canceled or TaskState.Rejected;

    /// <summary>True when a task in <paramref name="from"/> may move to <paramref name="to"/>. Nothing leaves a terminal state.</summary>
    public static bool CanTransition(TaskState from, TaskState to) => !IsFinal(from);

    /// <summary>True when a task in <paramref name="state"/> accepts a new user message under <paramref name="policy"/>.</summary>
    public static bool AcceptsFollowUp(TaskState state, FollowUpPolicy policy) => state switch
    {
        TaskState.InputRequired or TaskState.AuthRequired => true,
        TaskState.Submitted or TaskState.Working => policy == FollowUpPolicy.AllowSteering,
        _ => false,
    };
}
