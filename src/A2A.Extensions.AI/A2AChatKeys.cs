namespace A2A.Extensions.AI;

/// <summary>Keys of the <c>AdditionalProperties</c> that <see cref="A2AChatClient"/> reads and writes.</summary>
public static class A2AChatKeys
{
    /// <summary>The task id. Set it on <c>ChatOptions.AdditionalProperties</c> to continue a task; read it from responses and updates.</summary>
    public const string TaskId = "a2a.taskId";

    /// <summary>The task state on responses and updates, as a string: <c>submitted</c>, <c>working</c>, <c>completed</c>, <c>failed</c>, <c>canceled</c>, <c>rejected</c>, <c>input-required</c> or <c>auth-required</c>.</summary>
    public const string TaskState = "a2a.taskState";
}
