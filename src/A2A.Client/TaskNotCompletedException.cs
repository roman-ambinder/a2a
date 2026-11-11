namespace A2A.Client;

/// <summary>
///     <see cref="A2AClient.AskAsync" /> or <see cref="A2AClient.StreamTextAsync" /> got a task that ended in a state other
///     than <see cref="TaskState.Completed" />: it failed, was rejected or canceled, or the agent needs input or
///     authorization before it can go on.
/// </summary>
/// <remarks>
///     To answer an agent that needs input, send the next message with the task's <see cref="AgentTask.ContextId" /> and
///     <see cref="AgentTask.Id" />. The status message is text from the agent: treat it as untrusted.
/// </remarks>
public sealed class TaskNotCompletedException : Exception
{
    /// <summary>Creates the exception for <paramref name="task" />, which did not complete.</summary>
    public TaskNotCompletedException(AgentTask task)
        : base(BuildMessage(task ?? throw new ArgumentNullException(nameof(task))))
    {
        Task = task;
    }

    /// <summary>
    ///     The task as the agent left it. From a stream it holds only the id, the context id and the last status, not the
    ///     artifacts.
    /// </summary>
    public AgentTask Task { get; }

    /// <summary>The state the task ended in, for example <see cref="TaskState.Failed" /> or <see cref="TaskState.InputRequired" />.</summary>
    public TaskState State => Task.Status.State;

    private static string BuildMessage(AgentTask task)
    {
        var text = task.Status.Message?.GetText();
        return string.IsNullOrEmpty(text)
            ? $"The task ended in state {task.Status.State}."
            : $"The task ended in state {task.Status.State}: {text}";
    }
}
