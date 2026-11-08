using System.Text.Json;
using A2A.Server.Storage;

namespace A2A.Server;

/// <summary>
/// An <see cref="IAgentExecutor"/> that can also continue an existing task with no inbound client request, for example from a
/// timer or an external callback. The host starts a continuation with
/// <see cref="Execution.TaskExecutionEngine.ContinueTaskAsync"/>.
/// </summary>
public interface IContinuableAgentExecutor : IAgentExecutor
{
    /// <summary>
    /// Continues the task <paramref name="context"/> names. Report progress through <paramref name="updater"/>, as in
    /// <see cref="IAgentExecutor.ExecuteAsync"/>. Return when you have nothing more to report.
    /// </summary>
    /// <remarks>
    /// <para>There is no inbound request, so there is no bearer token and no token exchange.</para>
    /// <para>An exception fails the task, the same as in <see cref="IAgentExecutor.ExecuteAsync"/>.</para>
    /// <para>
    /// The host must store the task key when the task starts, to call <see cref="Execution.TaskExecutionEngine.ContinueTaskAsync"/>
    /// later. In <see cref="IAgentExecutor.ExecuteAsync"/>, build it as
    /// <c>new TaskKey(context.Call.Tenant, context.Call.Principal, context.TaskId)</c>. In <c>ContinueAsync</c> it is
    /// <see cref="TaskContinuationContext.Key"/>.
    /// </para>
    /// </remarks>
    /// <param name="context">The task and the caller's metadata.</param>
    /// <param name="updater">Writes the events of this run.</param>
    /// <param name="cancellationToken">Fires on CancelTask, the task time limit or shutdown.</param>
    Task ContinueAsync(TaskContinuationContext context, TaskUpdater updater, CancellationToken cancellationToken);
}

/// <summary>What <see cref="IContinuableAgentExecutor.ContinueAsync"/> knows about the task it continues.</summary>
public sealed class TaskContinuationContext
{
    /// <summary>The task: tenant, owner and id.</summary>
    public required TaskKey Key { get; init; }

    /// <summary>The task as stored when the run started. A copy made for this run: changing it does not change the stored task.</summary>
    public required AgentTask Task { get; init; }

    /// <summary>The metadata the caller passed to <c>ContinueTaskAsync</c>. A copy. Null when none. Also <see cref="TaskUpdater.RequestMetadata"/>.</summary>
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>How a request to continue a task ended.</summary>
public enum TaskContinuationStatus
{
    /// <summary>The agent was started.</summary>
    Started,

    /// <summary>The task does not exist, or belongs to another owner or tenant.</summary>
    TaskNotFound,

    /// <summary>The task is in a terminal state.</summary>
    TaskTerminal,

    /// <summary>The task waited for input longer than <see cref="A2AServerOptions.InterruptedTimeout"/>. The retention sweep cancels it when retention is on.</summary>
    InterruptedTimedOut,

    /// <summary>An execution of the task is running, or another continuation is starting.</summary>
    AlreadyRunning,

    /// <summary>The tenant has no agent, or its agent is not an <see cref="IContinuableAgentExecutor"/>.</summary>
    NotSupported,
}

/// <summary>The result of <see cref="Execution.TaskExecutionEngine.ContinueTaskAsync"/>.</summary>
public sealed class TaskContinuationResult
{
    internal TaskContinuationResult(TaskContinuationStatus status, Task completion)
    {
        Status = status;
        Completion = completion;
    }

    /// <summary>How the request ended.</summary>
    public TaskContinuationStatus Status { get; }

    /// <summary>True when the agent was started (<see cref="Status"/> is <see cref="TaskContinuationStatus.Started"/>).</summary>
    public bool Started => Status == TaskContinuationStatus.Started;

    /// <summary>
    /// Completes when the agent returned and every event is stored. Already complete when the request was refused. Never faults.
    /// </summary>
    public Task Completion { get; }
}
