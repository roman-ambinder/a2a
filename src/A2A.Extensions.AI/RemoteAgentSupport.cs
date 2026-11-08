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
}
