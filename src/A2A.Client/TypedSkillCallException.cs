using System.Diagnostics.CodeAnalysis;

namespace A2A.Client;

/// <summary>
///     A typed skill call (<see cref="A2AClient.CallSkillAsync{TInput, TOutput}" />) ended in a task state other than
///     completed. It is a <see cref="TaskNotCompletedException" />, so one <c>catch</c> covers it and
///     <see cref="A2AClientTextExtensions.AskAsync" />; <see cref="TaskNotCompletedException.Task" /> and
///     <see cref="TaskNotCompletedException.State" /> are the task as the agent left it.
/// </summary>
[Experimental("A2AEXP001",
    UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class TypedSkillCallException : TaskNotCompletedException
{
    /// <summary>Creates the exception for <paramref name="task" />, which did not complete.</summary>
    public TypedSkillCallException(AgentTask task)
        : base(task, "The skill call")
    {
    }
}
