using A2A.Server.Coordination;
using A2A.Server.Push;
using A2A.Server.Storage;

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
    private readonly A2ARequestHandler? _handler;

    /// <summary>
    /// Created by DI. The optional <paramref name="handler"/> cancels executions of this instance directly; without it, a
    /// running task is cancelled only through <see cref="ExecutionCoordinator.RequestCancelAsync"/> (other instances).
    /// </summary>
    public A2ATaskAdmin(TaskStore tasks, EventStore events, PushConfigStore pushConfigs, PushOutbox outbox, ExecutionCoordinator coordinator,
        A2ARequestHandler? handler = null)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _pushConfigs = pushConfigs ?? throw new ArgumentNullException(nameof(pushConfigs));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _handler = handler;
    }

    /// <summary>
    /// Deletes the task with its pending push deliveries, dead letters, push configs and event log. A running task is
    /// cancelled first, on every instance (without waiting for it to stop: its later events find no task and are dropped).
    /// Its context stays until the retention sweep removes it as an orphan. Owner-scoped through <paramref name="key"/>.
    /// </summary>
    /// <returns>True when the task was deleted; false when it does not exist for this tenant and owner.</returns>
    public async ValueTask<bool> DeleteTaskAsync(TaskKey key, CancellationToken ct = default)
    {
        if (await _tasks.GetAsync(key, ct).ConfigureAwait(false) is null) return false;
        if (await _coordinator.IsRunningAsync(key, ct).ConfigureAwait(false))
        {
            if (_handler is not null) await _handler.CancelExecutionsAsync(key, ct).ConfigureAwait(false);
            else await _coordinator.RequestCancelAsync(key, ct).ConfigureAwait(false);
        }
        var deleted = await PurgeAsync(key, ct).ConfigureAwait(false);
        // A cancelled execution may have committed while the purge ran, after its deletes: delete again (idempotent). With
        // the task row gone, no later commit can succeed.
        await _outbox.DeleteAllAsync(key, ct).ConfigureAwait(false);
        await _events.DeleteAsync(key, ct).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>
    /// Deletes the task's data in an order that survives a crash part-way: pending push deliveries and dead letters, push
    /// configs, the event log, and the task row last. Until the row is gone the task is still listed, so the next sweep (or
    /// call) finishes the job; every step is idempotent. Returns whether the task row was deleted by this call.
    /// </summary>
    internal async ValueTask<bool> PurgeAsync(TaskKey key, CancellationToken ct)
    {
        await _outbox.DeleteAllAsync(key, ct).ConfigureAwait(false);
        await _pushConfigs.DeleteAllAsync(key, ct).ConfigureAwait(false);
        await _events.DeleteAsync(key, ct).ConfigureAwait(false);
        return await _tasks.DeleteAsync(key, ct).ConfigureAwait(false);
    }
}
