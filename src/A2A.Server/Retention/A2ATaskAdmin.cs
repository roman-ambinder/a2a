using A2A.Server.Audit;
using A2A.Server.Coordination;
using A2A.Server.Execution;
using A2A.Server.Push;
using A2A.Server.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.Server.Retention;

/// <summary>
/// Operator API for tasks: deletes a task with all its data. Registered by <c>AddA2AServer</c>. Not exposed over A2A; wire it
/// to your own admin endpoint and protect that. The retention sweep (<see cref="A2ARetentionService"/>) deletes expired tasks
/// the same way.
/// </summary>
public sealed class A2ATaskAdmin
{
    private readonly TaskStore _tasks;
    private readonly EventStore _events;
    private readonly PushConfigStore _pushConfigs;
    private readonly PushOutbox _outbox;
    private readonly ExecutionCoordinator _coordinator;
    private readonly TaskExecutionEngine? _engine;
    private readonly ArtifactObjectStore? _objects;
    private readonly ILogger _logger;
    private readonly A2AAuditor _audit;

    /// <summary>Creates the admin API. In an app the DI container does this.</summary>
    /// <param name="tasks">The task store.</param>
    /// <param name="events">The event store.</param>
    /// <param name="pushConfigs">The push config store.</param>
    /// <param name="outbox">The push outbox.</param>
    /// <param name="coordinator">Asks other instances to cancel a task's executions.</param>
    /// <param name="engine">
    /// Cancels executions of this instance directly; without it, a running task is cancelled only through
    /// <see cref="ExecutionCoordinator.RequestCancelAsync"/> (other instances).
    /// </param>
    /// <param name="objects">The artifact object store. When set, a deleted task's objects are deleted with it.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="audit">Writes <see cref="DeleteTaskAsync(TaskKey, string, CancellationToken)"/> to the audit trail. Without it nothing is audited.</param>
    public A2ATaskAdmin(TaskStore tasks, EventStore events, PushConfigStore pushConfigs, PushOutbox outbox, ExecutionCoordinator coordinator,
        TaskExecutionEngine? engine = null, ArtifactObjectStore? objects = null, ILogger<A2ATaskAdmin>? logger = null, A2AAuditor? audit = null)
    {
        _audit = audit ?? A2AAuditor.None;
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _pushConfigs = pushConfigs ?? throw new ArgumentNullException(nameof(pushConfigs));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _engine = engine;
        _objects = objects;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Deletes the task with its pending push deliveries, dead letters, push configs and event log. Its executions are
    /// cancelled first, on every instance (without waiting for them to stop: their later events find no task and are dropped).
    /// Its context stays until the retention sweep removes it as an orphan. Owner-scoped through <paramref name="key"/>.
    /// </summary>
    /// <remarks>Audited with the actor <see cref="A2AAuditEvent.UnknownOperator"/>. Use the overload with an <c>actor</c> to say who deletes.</remarks>
    /// <returns>True when the task was deleted; false when it does not exist for this tenant and owner.</returns>
    public ValueTask<bool> DeleteTaskAsync(TaskKey key, CancellationToken cancellationToken = default) =>
        DeleteTaskAsync(key, A2AAuditEvent.UnknownOperator, cancellationToken);

    /// <summary>
    /// Deletes the task as <see cref="DeleteTaskAsync(TaskKey, CancellationToken)"/> does, and writes the audit trail
    /// (<see cref="A2AAuditAction.AdminTaskDeleted"/>). With <see cref="A2AAuditOptions.FailClosed"/> the delete does not happen
    /// when the <see cref="A2AAuditOutcome.Attempted"/> row cannot be written; the call then throws <see cref="A2AException"/>
    /// with <see cref="A2AErrorCode.Internal"/>. A task that does not exist is recorded as <see cref="A2AAuditOutcome.Failed"/> with the reason <c>NotFound</c>.
    /// </summary>
    /// <param name="key">The task (owner-scoped).</param>
    /// <param name="actor">Who deletes the task: the operator's id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when the task was deleted; false when it does not exist for this tenant and owner.</returns>
    public async ValueTask<bool> DeleteTaskAsync(TaskKey key, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var e = _audit.For(A2AAuditAction.AdminTaskDeleted, actor, key.Tenant, key.TaskId);
        await _audit.BeginAsync(e, cancellationToken).ConfigureAwait(false);
        bool deleted;
        try
        {
            deleted = await DeleteTaskCoreAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _audit.FailAsync(e, ex).ConfigureAwait(false);
            throw;
        }
        if (deleted) await _audit.SucceedAsync(e).ConfigureAwait(false);
        else await _audit.FailAsync(e, "NotFound").ConfigureAwait(false);
        return deleted;
    }

    private async ValueTask<bool> DeleteTaskCoreAsync(TaskKey key, CancellationToken cancellationToken)
    {
        if (await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) is null) return false;
        // Always: an execution here whose register failed is invisible to the coordinator's running check.
        if (_engine is not null) await _engine.CancelExecutionsAsync(key, cancellationToken).ConfigureAwait(false);
        else await _coordinator.RequestCancelAsync(key, cancellationToken).ConfigureAwait(false);
        var deleted = await PurgeAsync(key, cancellationToken).ConfigureAwait(false);
        // A cancelled execution may have committed while the purge ran, after its deletes: delete again (idempotent). With
        // the task row gone, no later commit can succeed.
        await _outbox.DeleteAllAsync(key, cancellationToken).ConfigureAwait(false);
        await _events.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>
    /// Deletes the task's data in an order that survives a crash part-way: pending push deliveries and dead letters, push
    /// configs, the event log, and the task row; then its artifact objects. Until the row is gone the task is still listed, so
    /// the next sweep (or call) finishes the job; every step is idempotent. The objects go after the row: deleting them first
    /// could leave a task that points at nothing. They are deleted only when this call deleted the row, and a failure is
    /// logged and left to the orphan sweep. Returns whether the task row was deleted by this call.
    /// </summary>
    internal async ValueTask<bool> PurgeAsync(TaskKey key, CancellationToken cancellationToken)
    {
        await _outbox.DeleteAllAsync(key, cancellationToken).ConfigureAwait(false);
        await _pushConfigs.DeleteAllAsync(key, cancellationToken).ConfigureAwait(false);
        await _events.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        var deleted = await _tasks.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        if (deleted && _objects is not null) await DeleteObjectsAsync(_objects, key, cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    private async ValueTask DeleteObjectsAsync(ArtifactObjectStore objects, TaskKey key, CancellationToken cancellationToken)
    {
        try
        {
            var prefix = ArtifactObjectKey.TaskPrefix(key.Tenant, key.TaskId);
            // Collect first: a provider may not allow deleting while a listing is open.
            var keys = new List<string>();
            await foreach (var entry in objects.ListAsync(prefix, cancellationToken).ConfigureAwait(false))
                if (entry.Key.StartsWith(prefix, StringComparison.Ordinal)) keys.Add(entry.Key);
            foreach (var objectKey in keys) await objects.DeleteAsync(objectKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            ServerLog.ArtifactObjectsDeleteFailed(_logger, ex, key.TaskId);
        }
    }
}
