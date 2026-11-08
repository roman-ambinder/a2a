using System.Text.Json;
using A2A.Server.Retention;
using A2A.Server.Storage;

namespace A2A.Server.Execution;

// Proactive continuation: runs an agent on an existing task with no inbound request. Kept apart from the event pipeline of
// TaskExecutionEngine.cs: this only decides whether to start, then starts a normal execution through StartRawAsync.
public sealed partial class TaskExecutionEngine
{
    private const string ContinueKind = "continue";

    /// <summary>
    /// Continues an existing task with <see cref="IContinuableAgentExecutor.ContinueAsync"/>, with no inbound client request and
    /// no push registration. The events go through the normal commit path: to the event store, to stream subscribers, and to
    /// push when the task has push configs and the tenant's card declares push.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A request that cannot start is refused with a status, and nothing changes: no lease, no agent run, no event. Exceptions
    /// are for a bad <paramref name="key"/>, a failing agent factory, a failing store or coordinator, and a cancelled
    /// <paramref name="cancellationToken"/>.
    /// </para>
    /// <para>
    /// The run is exclusive against every execution of the task on every instance: a task with a running execution gives
    /// <see cref="TaskContinuationStatus.AlreadyRunning"/>. A timer that fires on every replica gets one
    /// <see cref="TaskContinuationStatus.Started"/>. An overdue interrupted task (<see cref="A2AServerOptions.InterruptedTimeout"/>;
    /// null turns it off) is refused and left for the retention sweep. This is a server-side API: it does not check the caller,
    /// so a host that exposes it must authenticate its callers and map them to a <see cref="TaskKey"/>.
    /// </para>
    /// <para>
    /// The started run is a normal execution: it holds a lease, is stopped by CancelTask, and ends after
    /// <see cref="A2AServerOptions.MaxTaskDuration"/>. An agent that throws fails the task.
    /// </para>
    /// </remarks>
    /// <param name="key">The task: tenant, owner and id.</param>
    /// <param name="metadata">Passed to the agent as <see cref="TaskContinuationContext.Metadata"/>. The agent gets a copy.</param>
    /// <param name="cancellationToken">Cancels only the attempt to start. A started run does not stop with it.</param>
    /// <returns>The status, and a <see cref="TaskContinuationResult.Completion"/> that ends with the run.</returns>
    /// <exception cref="ArgumentException">A part of <paramref name="key"/> is null or empty.</exception>
    public async ValueTask<TaskContinuationResult> ContinueTaskAsync(TaskKey key, Dictionary<string, JsonElement>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key.Tenant, nameof(key));
        ArgumentNullException.ThrowIfNull(key.Owner, nameof(key));
        ArgumentException.ThrowIfNullOrEmpty(key.TaskId, nameof(key));
        cancellationToken.ThrowIfCancellationRequested();

        if (ContinuationRefusal(await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false)) is { } early) return Refused(key, early);
        if (RunningCompletions(key).Count > 0) return Refused(key, TaskContinuationStatus.AlreadyRunning);

        await using var agent = await RentAgentAsync(key.Tenant, null, cancellationToken).ConfigureAwait(false);
        agent.Error?.Throw(); // a failing factory reaches the caller; the task is untouched
        if (agent.Executor is not IContinuableAgentExecutor) return Refused(key, TaskContinuationStatus.NotSupported);

        var claim = Guid.NewGuid().ToString("N");
        if (!await _coordinator.TryRegisterExclusiveAsync(key, claim, cancellationToken).ConfigureAwait(false))
            return Refused(key, TaskContinuationStatus.AlreadyRunning);
        try
        {
            // Read after the claim: the sweep and the other instances now see this task as running.
            var record = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (ContinuationRefusal(record) is { } late) return Refused(key, late);
            var pushEnabled = await PushEnabledForTenantAsync(key, cancellationToken).ConfigureAwait(false);
            var context = new TaskContinuationContext
            {
                Key = key,
                Task = record!.Task.DeepCopy(), // the agent's own copy
                Metadata = metadata is null ? null : new Dictionary<string, JsonElement>(metadata),
            };
            var exec = await StartRawAsync(key, record.Task.ContextId, isNewTask: false, userMessage: null, push: null, emitInitialSnapshot: false,
                (executor, updater, token) => ((IContinuableAgentExecutor)executor).ContinueAsync(context, updater, token),
                agent, pushEnabled, requestMetadata: context.Metadata, timeLimit: _options.MaxTaskDuration, kind: ContinueKind).ConfigureAwait(false);
            exec.Detach(); // no request reads the outgoing events
            return new TaskContinuationResult(TaskContinuationStatus.Started, exec.Completion);
        }
        finally
        {
            // The run has its own lease now. A failed release is not fatal: the claim expires.
            try { await _coordinator.UnregisterAsync(key, claim, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { ServerLog.ContinuationClaimReleaseFailed(_logger, ex, key.TaskId); }
        }
    }

    /// <summary>Why a continuation cannot start on <paramref name="record"/>, or null when it can.</summary>
    private TaskContinuationStatus? ContinuationRefusal(TaskRecord? record)
    {
        if (record is null) return TaskContinuationStatus.TaskNotFound;
        if (record.Task.Status.State.IsTerminal()) return TaskContinuationStatus.TaskTerminal;
        // The sweep's own test, so the answer does not depend on when the sweep last ran.
        if (_options.InterruptedTimeout is { } timeout
            && A2ARetentionService.StillWaiting(record, A2ARetentionService.Before(_time.GetUtcNow(), timeout)))
            return TaskContinuationStatus.InterruptedTimedOut;
        return null;
    }

    private TaskContinuationResult Refused(TaskKey key, TaskContinuationStatus status)
    {
        ServerLog.ContinuationRefused(_logger, key.TaskId, status);
        return new TaskContinuationResult(status, Task.CompletedTask);
    }
}
