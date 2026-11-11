using System.Text.Json;
using A2A.Server.Idempotency;
using A2A.Server.Retention;
using A2A.Server.Storage;

namespace A2A.Server.Execution;

// Proactive continuation: runs an agent on an existing task with no inbound request. Kept apart from the event pipeline of
// TaskExecutionEngine.cs: this only decides whether to start, then starts a normal execution through StartRawAsync.
public sealed partial class TaskExecutionEngine
{
    private const string ContinueKind = "continue";

    /// <summary>
    ///     Prefix of the id of a start claim. The retention sweep removes an expired lease with this prefix and never
    ///     touches its task.
    /// </summary>
    internal const string ContinuationClaimPrefix = "claim:";

    /// <summary>
    ///     Continues an existing task with <see cref="IContinuableAgentExecutor.ContinueAsync" />, with no inbound client
    ///     request and
    ///     no push registration. The events go through the normal commit path: to the event store, to stream subscribers, and
    ///     to
    ///     push when the task has push configs and the tenant's card declares push.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A request that cannot start is refused with a status, and nothing changes: no lease, no agent run, no event.
    ///         Exceptions
    ///         are for a bad <paramref name="key" />, a failing agent factory, a failing store or coordinator, and a cancelled
    ///         <paramref name="cancellationToken" />.
    ///     </para>
    ///     <para>
    ///         A continuation does not start while another continuation or a registered execution of the task runs, on any
    ///         instance:
    ///         that gives <see cref="TaskContinuationStatus.AlreadyRunning" />. A client reply or CancelTask can still start
    ///         next to a
    ///         running continuation, so the agent must allow for that. <c>AlreadyRunning</c> covers duplicates during a run
    ///         only: a
    ///         replica that fires just after a fast run ended, on a task left open, gets a second
    ///         <see cref="TaskContinuationStatus.Started" />, so the host deduplicates. An overdue interrupted task
    ///         (<see cref="A2AServerOptions.InterruptedTimeout" />; null turns it off) is refused; the retention sweep cancels
    ///         it when
    ///         retention is on. This is a server-side API: it does not check the caller, so a host that exposes it must
    ///         authenticate
    ///         its callers and map them to a <see cref="TaskKey" />.
    ///     </para>
    ///     <para>
    ///         The started run is a normal execution: it holds a lease, is stopped by CancelTask, and ends after
    ///         <see cref="A2AServerOptions.MaxTaskDuration" />. An agent that throws fails the task.
    ///     </para>
    /// </remarks>
    /// <param name="key">The task: tenant, owner and id.</param>
    /// <param name="metadata">Passed to the agent as <see cref="TaskContinuationContext.Metadata" />. The agent gets a copy.</param>
    /// <param name="cancellationToken">Cancels only the attempt to start. A started run does not stop with it.</param>
    /// <returns>The status, and a <see cref="TaskContinuationResult.Completion" /> that ends with the run.</returns>
    /// <exception cref="ArgumentException">
    ///     The task id is empty, or the tenant or the owner is null. An empty tenant is the
    ///     default tenant.
    /// </exception>
    public async ValueTask<TaskContinuationResult> ContinueTaskAsync(TaskKey key,
        Dictionary<string, JsonElement>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key.Tenant, nameof(key));
        ArgumentNullException.ThrowIfNull(key.Owner, nameof(key));
        ArgumentException.ThrowIfNullOrEmpty(key.TaskId, nameof(key));
        cancellationToken.ThrowIfCancellationRequested();

        if (ContinuationRefusal(await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false)) is
            { } early) return Refused(key, early);
        if (RunningCompletions(key).Count > 0 || IsStartPending(key))
            return Refused(key, TaskContinuationStatus.AlreadyRunning);

        await using var agent = await RentAgentAsync(key.Tenant, null, cancellationToken).ConfigureAwait(false);
        agent.Error?.Throw(); // a failing factory reaches the caller; the task is untouched
        if (agent.Executor is not IContinuableAgentExecutor) return Refused(key, TaskContinuationStatus.NotSupported);

        // After the early refusals and the agent check, before the start claim (a claim has no heartbeat while queued).
        try
        {
            agent.AttachSlot(await AdmitAsync(key.Tenant, IdempotencyClaim.None, key.TaskId, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (A2AException ex) when (ex.Code == A2AErrorCode.RateLimited)
        {
            return Refused(key, TaskContinuationStatus.AtCapacity);
        }

        var pushEnabled =
            await PushEnabledForTenantAsync(key, cancellationToken)
                .ConfigureAwait(false); // before the claim: it has no heartbeat
        var claim = ContinuationClaimPrefix + Guid.NewGuid().ToString("N");
        try
        {
            if (!await _coordinator.TryRegisterExclusiveAsync(key, claim, cancellationToken).ConfigureAwait(false))
                return Refused(key, TaskContinuationStatus.AlreadyRunning);
            // A follow-up committed here but not registered yet (waiting for admission) is not in the coordinator: its
            // pending start is the only sign of it. Checked again after the claim, as the admission wait may be long.
            if (IsStartPending(key)) return Refused(key, TaskContinuationStatus.AlreadyRunning);
            // Read after the claim: the sweep and the other instances now see this task as running.
            var record = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (ContinuationRefusal(record) is { } late) return Refused(key, late);
            var context = new TaskContinuationContext
            {
                Key = key,
                Task = record!.Task.DeepCopy(), // the agent's own copy
                Metadata = CopyMetadata(metadata)
            };
            var exec = await StartRawAsync(key, record.Task.ContextId, false, null, null, false,
                (executor, updater, token) =>
                {
                    updater.SeedUsage(context.Task.Metadata); // the usage earlier runs stored
                    return ((IContinuableAgentExecutor)executor).ContinueAsync(context, updater, token);
                },
                agent, pushEnabled, requestMetadata: context.Metadata, timeLimit: _options.MaxTaskDuration,
                kind: ContinueKind).ConfigureAwait(false);
            exec.Detach(); // no request reads the outgoing events
            await CancelIfEndedMeanwhileAsync(key, exec).ConfigureAwait(false);
            return new TaskContinuationResult(TaskContinuationStatus.Started, exec.Completion);
        }
        finally
        {
            // The run has its own lease now. This also runs when the claim call threw: a client timeout can throw after the
            // coordinator ran the claim. A claim that is not released expires, and the sweep removes it without touching the task.
            try
            {
                await _coordinator.UnregisterAsync(key, claim, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ServerLog.ContinuationClaimReleaseFailed(Logger, ex, key.TaskId);
            }
        }
    }

    /// <summary>
    ///     A CancelTask, delete or sweep between the read after the claim and the run's registration found nothing to cancel.
    ///     Read
    ///     the task again: when it is gone or terminal, stop the run. A failed read is logged and does not throw: the run has
    ///     started.
    /// </summary>
    private async Task CancelIfEndedMeanwhileAsync(TaskKey key, Execution exec)
    {
        try
        {
            var record = await _tasks.GetAsync(key, CancellationToken.None).ConfigureAwait(false);
            if (record is null || record.Task.Status.State.IsTerminal()) CancelAll([exec]);
        }
        catch (Exception ex)
        {
            ServerLog.ContinuationRecheckFailed(Logger, ex, key.TaskId);
        }
    }

    // A deep copy: a caller's disposed JsonDocument must not break the agent.
    private static Dictionary<string, JsonElement>? CopyMetadata(Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is null) return null;
        var copy = new Dictionary<string, JsonElement>(metadata.Count, metadata.Comparer);
        foreach (var (name, value) in metadata) copy[name] = value.Clone();
        return copy;
    }

    /// <summary>Why a continuation cannot start on <paramref name="record" />, or null when it can.</summary>
    private TaskContinuationStatus? ContinuationRefusal(TaskRecord? record)
    {
        if (record is null) return TaskContinuationStatus.TaskNotFound;
        if (record.Task.Status.State.IsTerminal()) return TaskContinuationStatus.TaskTerminal;
        // The sweep's own test, so the answer does not depend on when the sweep last ran.
        if (_options.InterruptedTimeout is { } timeout
            && A2ARetentionService.StillWaiting(record, A2ARetentionService.Before(Clock.GetUtcNow(), timeout)))
            return TaskContinuationStatus.InterruptedTimedOut;
        return null;
    }

    private TaskContinuationResult Refused(TaskKey key, TaskContinuationStatus status)
    {
        ServerLog.ContinuationRefused(Logger, key.TaskId, status);
        return new TaskContinuationResult(status, Task.CompletedTask);
    }
}