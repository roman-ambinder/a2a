namespace A2A.Client;

/// <summary>
///     The outcome of one turn of an <see cref="A2AConversation" />: a direct message from the agent, or a task in the state
///     it stopped in. It is returned for every state, including failed and input-required; nothing is thrown for a task
///     state. Call <see cref="EnsureCompleted" /> to throw when the turn did not complete.
/// </summary>
/// <remarks>The text and the status text come from the agent: treat them as untrusted.</remarks>
public sealed class AgentReply
{
    internal AgentReply(Message message)
    {
        Message = message;
        Text = message.GetText();
        ContextId = message.ContextId;
        TaskId = message.TaskId;
    }

    internal AgentReply(AgentTask task, string text)
    {
        Task = task;
        Text = text;
        State = task.Status.State;
        StatusText = task.Status.Message?.GetText() is { Length: > 0 } status ? status : null;
        ContextId = string.IsNullOrEmpty(task.ContextId) ? null : task.ContextId;
        TaskId = string.IsNullOrEmpty(task.Id) ? null : task.Id;
    }

    /// <summary>
    ///     The answer: the text of the direct message, or the text of the task's artifacts. Empty when there is none, for
    ///     example when the agent is waiting for input; its question is in <see cref="StatusText" />.
    /// </summary>
    public string Text { get; }

    /// <summary>
    ///     The text of the task's status message: the agent's question when it needs input, the reason when it failed, or a
    ///     closing note. Null for a direct message and when the status has no text.
    /// </summary>
    public string? StatusText { get; }

    /// <summary>The task's state. Null when the agent answered with a direct message.</summary>
    public TaskState? State { get; }

    /// <summary>The task's id, or the task id of the direct message. Null when there is none.</summary>
    public string? TaskId { get; }

    /// <summary>The context (conversation) id. Null when the agent sent none.</summary>
    public string? ContextId { get; }

    /// <summary>
    ///     The task, when the agent answered with one. After a stream it can hold only the id, the context id and the last
    ///     status.
    /// </summary>
    public AgentTask? Task { get; }

    /// <summary>The direct message, when the agent answered with one instead of a task.</summary>
    public Message? Message { get; }

    /// <summary>True for a direct message and for a task in state <see cref="TaskState.Completed" />.</summary>
    public bool IsCompleted => Message is not null || State == TaskState.Completed;

    /// <summary>True when the agent waits for more input (<see cref="TaskState.InputRequired" />). Answer with the next send.</summary>
    public bool NeedsInput => State == TaskState.InputRequired;

    /// <summary>True when the agent waits for authorization (<see cref="TaskState.AuthRequired" />).</summary>
    public bool NeedsAuth => State == TaskState.AuthRequired;

    /// <summary>Returns this reply when it <see cref="IsCompleted" />, and throws otherwise.</summary>
    /// <exception cref="TaskNotCompletedException">The task is not in state <see cref="TaskState.Completed" />.</exception>
    public AgentReply EnsureCompleted()
    {
        return IsCompleted ? this : throw new TaskNotCompletedException(Task!);
    }
}
