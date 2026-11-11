namespace A2A.Client;

/// <summary>
///     <see cref="A2AClientTextExtensions.AskAsync" /> or <see cref="A2AClientTextExtensions.StreamTextAsync" /> got a task that ended in a state other
///     than <see cref="TaskState.Completed" />: it failed, was rejected or canceled, or the agent needs input or
///     authorization before it can go on.
/// </summary>
/// <remarks>
///     To answer an agent that needs input, send the next message with the task's <see cref="AgentTask.ContextId" /> and
///     <see cref="AgentTask.Id" />. The status message is text from the agent: treat it as untrusted.
/// </remarks>
public class TaskNotCompletedException : Exception
{
    /// <summary>Creates the exception for <paramref name="task" />, which did not complete.</summary>
    public TaskNotCompletedException(AgentTask task)
        : this(task, "The task")
    {
    }

    /// <summary>
    ///     Creates the exception with a message that starts with <paramref name="what" /> (for example "The skill call"),
    ///     followed by the state the task ended in and its status message.
    /// </summary>
    protected TaskNotCompletedException(AgentTask task, string what)
        : base(BuildMessage(task ?? throw new ArgumentNullException(nameof(task)), what))
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

    private static string BuildMessage(AgentTask task, string what)
    {
        var text = task.Status.Message?.GetText();
        return string.IsNullOrEmpty(text)
            ? $"{what} ended in state {task.Status.State}."
            : $"{what} ended in state {task.Status.State}: {text}";
    }
}
