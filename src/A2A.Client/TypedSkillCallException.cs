namespace A2A.Client;

/// <summary>A typed skill call (<see cref="A2AClient.CallSkillAsync{TInput, TOutput}"/>) ended in a task state other than completed.</summary>
[global::System.Diagnostics.CodeAnalysis.Experimental("A2AEXP001", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class TypedSkillCallException : Exception
{
    /// <summary>Creates the exception for <paramref name="task"/>, which did not complete.</summary>
    public TypedSkillCallException(AgentTask task)
        : base(BuildMessage(task ?? throw new ArgumentNullException(nameof(task))))
    {
        Task = task;
    }

    /// <summary>The task as the agent left it. It can hold a status message, which is text from the agent.</summary>
    public AgentTask Task { get; }

    /// <summary>The state the task ended in, for example <see cref="TaskState.Failed"/> or <see cref="TaskState.InputRequired"/>.</summary>
    public TaskState State => Task.Status.State;

    private static string BuildMessage(AgentTask task)
    {
        var text = task.Status.Message?.GetText();
        return string.IsNullOrEmpty(text)
            ? $"The skill call ended in state {task.Status.State}."
            : $"The skill call ended in state {task.Status.State}: {text}";
    }
}
