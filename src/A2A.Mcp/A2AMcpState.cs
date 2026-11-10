namespace A2A.Mcp;

internal static class A2AMcpState
{
    internal static string Name(TaskState state)
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

    /// <summary>The task is still running, so a call that waited long enough returns a handle.</summary>
    internal static bool NeedsWait(TaskState state)
    {
        return !state.IsTerminal() && !state.IsInterrupted() && state != TaskState.Unspecified;
    }

    /// <summary>States that are tool errors: the caller did not get what it asked for.</summary>
    internal static bool IsError(TaskState state)
    {
        return state is TaskState.AuthRequired or TaskState.Failed or TaskState.Rejected or TaskState.Canceled
            or TaskState.Unspecified;
    }
}