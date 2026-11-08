using A2A.Server.Coordination;
using A2A.Server.Storage;

namespace A2A.Server.Execution;

// Execution leases and cancel requests across instances (the ExecutionCoordinator). Kept apart from the event pipeline of
// TaskExecutionEngine.cs: everything here works on the running executions and the coordinator, never on events.
public sealed partial class TaskExecutionEngine
{
    /// <summary>Registers the execution's lease. A failure is logged: the next heartbeat registers it instead.</summary>
    private async Task RegisterAsync(Execution exec)
    {
        try
        {
            await _coordinator.RegisterAsync(exec.Key, exec.Id, CancellationToken.None).ConfigureAwait(false);
            exec.Registered = true;
        }
        catch (Exception ex)
        {
            ServerLog.LeaseRegisterFailed(_logger, ex, exec.TaskId);
        }
    }

    /// <summary>
    /// Ends the execution's lease. Waits for a heartbeat still under way and keeps later ones out (the gate is held for good), so
    /// a late register cannot outlive the unregister. Fenced by the execution id: never ends a newer execution's lease.
    /// </summary>
    private async Task UnregisterAsync(Execution exec)
    {
        await exec.HeartbeatGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _coordinator.UnregisterAsync(exec.Key, exec.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServerLog.LeaseUnregisterFailed(_logger, ex, exec.TaskId);
        }
    }

    /// <summary>
    /// Renews the execution's lease, or registers it when it has none (the register at start failed, or the lease was lost;
    /// then <see cref="CancelIfEndedAsync"/> checks the task after the register), and renews its idempotency claim. Runs on
    /// the heartbeat timer; skips a beat while the previous one is still running. Failures are logged and the execution goes on.
    /// </summary>
    private async Task HeartbeatAsync(Execution exec)
    {
        if (!exec.HeartbeatGate.Wait(0)) return;
        try
        {
            try
            {
                if (exec.Registered)
                {
                    if (!await _coordinator.HeartbeatAsync(exec.Key, exec.Id, CancellationToken.None).ConfigureAwait(false))
                        await OnLeaseLostAsync(exec).ConfigureAwait(false);
                }
                else
                {
                    // The execution still runs here, so registering it (again, under the same id) is not a resurrection:
                    // a finished execution holds the heartbeat gate for good and never gets here.
                    await _coordinator.RegisterAsync(exec.Key, exec.Id, CancellationToken.None).ConfigureAwait(false);
                    // When the read fails, the execution stays unregistered: the next heartbeat registers and reads again.
                    if (exec.LeaseLost) await CancelIfEndedAsync(exec).ConfigureAwait(false);
                    exec.Registered = true;
                }
            }
            catch (Exception ex)
            {
                ServerLog.LeaseRenewFailed(_logger, ex, exec.TaskId);
            }
            try
            {
                await exec.Claim.RenewAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ServerLog.ClaimRenewFailed(_logger, ex, exec.TaskId);
            }
        }
        finally
        {
            exec.HeartbeatGate.Release();
        }
    }

    /// <summary>
    /// The execution's lease lapsed (its heartbeats missed the lease, for example during a long pause) and the coordinator no
    /// longer holds it. Called under the heartbeat gate. When the task ended (<see cref="TaskEndedAsync"/>: the retention sweep
    /// may have failed it as dead), the execution is cancelled: its later events would be dropped anyway. Otherwise the
    /// execution registers again with its next heartbeat, so every instance sees it running.
    /// </summary>
    private async Task OnLeaseLostAsync(Execution exec)
    {
        if (await TaskEndedAsync(exec).ConfigureAwait(false))
        {
            ServerLog.LeaseLostTaskEnded(_logger, exec.TaskId);
            CancelAll([exec]);
            return;
        }
        ServerLog.LeaseLost(_logger, exec.TaskId);
        exec.LeaseLost = true;
        exec.Registered = false;
    }

    /// <summary>
    /// Called under the heartbeat gate right after the heartbeat registered an execution that had lost its lease. While it
    /// had none, another instance may have ended the task (the retention sweep fails it as dead), and that instance's cancel
    /// request may never have arrived. So the task is read again, and when it is terminal or gone the execution is cancelled.
    /// </summary>
    private async Task CancelIfEndedAsync(Execution exec)
    {
        var ended = await TaskEndedAsync(exec).ConfigureAwait(false);
        exec.LeaseLost = false;
        if (!ended) return;
        ServerLog.LeaseLostTaskEnded(_logger, exec.TaskId);
        CancelAll([exec]);
    }

    /// <summary>
    /// Whether the execution's task ended: it is terminal, or it was stored and is gone (deleted). A new task that is not stored
    /// yet (the agent has sent no event) has not ended: nothing else can end a task that does not exist. The flag is read
    /// before the task, so a first event stored in between is seen in the record.
    /// </summary>
    private async Task<bool> TaskEndedAsync(Execution exec)
    {
        var stored = exec.TaskStored;
        var record = await _tasks.GetAsync(exec.Key, CancellationToken.None).ConfigureAwait(false);
        return record is null ? stored : record.Task.Status.State.IsTerminal();
    }

    /// <summary>
    /// Reads the cancel requests of every instance and cancels the matching executions here, until shutdown. The first reader
    /// is registered before this returns, so no request sent after the engine exists is missed; after a failure it reads
    /// again every <see cref="CoordinationOptions.RunningPollInterval"/>.
    /// </summary>
    private void StartCancelListener(CancellationToken stopping)
    {
        var (requests, next) = ReadCancelRequests(stopping);
        _ = ListenForCancelRequestsAsync(requests, next, stopping);
    }

    /// <summary>Starts a reader of cancel requests (registered once this returns), or returns null when the coordinator fails.</summary>
#pragma warning disable CA2012 // the first MoveNextAsync registers the reader; the listener awaits it exactly once
    private (IAsyncEnumerator<TaskKey>? Requests, ValueTask<bool> Next) ReadCancelRequests(CancellationToken stopping)
    {
        try
        {
            var requests = _coordinator.CancelRequestsAsync(stopping).GetAsyncEnumerator(stopping);
            return (requests, requests.MoveNextAsync());
        }
        catch (Exception ex)
        {
            LogCancelReadFailure(ex);
            return (null, default);
        }
    }
#pragma warning restore CA2012

    private async Task ListenForCancelRequestsAsync(IAsyncEnumerator<TaskKey>? requests, ValueTask<bool> next, CancellationToken stopping)
    {
        while (true)
        {
            if (requests is not null)
            {
                try
                {
                    while (await next.ConfigureAwait(false))
                    {
                        _cancelReadFailures = 0; // reading works again
                        CancelHere(requests.Current);
                        next = requests.MoveNextAsync();
                    }
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    LogCancelReadFailure(ex);
                }
                try { await requests.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { ServerLog.CancelReaderDisposeFailed(_logger, ex); }
            }
            if (stopping.IsCancellationRequested) return;
            try { await Task.Delay(_options.Coordination.RunningPollInterval, stopping).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            (requests, next) = ReadCancelRequests(stopping);
        }
    }

    /// <summary>Consecutive failures to read cancel requests. Only the listener loop touches it.</summary>
    private int _cancelReadFailures;

    /// <summary>The first of a run of failures is a warning; the retries that fail again are debug, until reading works.</summary>
    private void LogCancelReadFailure(Exception ex)
    {
        if (_cancelReadFailures++ == 0) ServerLog.CancelReadFailed(_logger, ex, _options.Coordination.RunningPollInterval);
        else ServerLog.CancelReadStillFailing(_logger, ex, _cancelReadFailures);
    }

    /// <summary>
    /// Cancels this instance's executions of <paramref name="task"/> that another instance asked to cancel, except
    /// CancelTask runs (they record the cancellation).
    /// </summary>
    private void CancelHere(TaskKey task) =>
        CancelAll(_running.Snapshot(ScopeKey(task.Tenant, task.TaskId)).Where(exec => !exec.IsCancelRun && exec.Key == task));

    /// <summary>Cancels each execution; a cancellation callback that throws is logged and does not stop the others.</summary>
    private void CancelAll(IEnumerable<Execution> executions)
    {
        foreach (var exec in executions)
        {
            if (exec.Cancel() is { } error) ServerLog.CancelCallbackFailed(_logger, error, exec.TaskId);
        }
    }

    /// <summary>Asks every execution of the task running on this instance to stop (CancelTask runs included); returns them.</summary>
    internal List<Execution> CancelRunning(TaskKey key)
    {
        var running = _running.Snapshot(ScopeKey(key.Tenant, key.TaskId)).Where(exec => exec.Key == key).ToList();
        CancelAll(running);
        return running;
    }

    /// <summary>
    /// Cancels every execution of the task: this instance's at once (CancelTask runs included), the other instances' through
    /// <see cref="ExecutionCoordinator.RequestCancelAsync"/>. Does not wait for them to stop.
    /// </summary>
    internal async Task CancelExecutionsAsync(TaskKey key, CancellationToken ct = default)
    {
        CancelRunning(key);
        await _coordinator.RequestCancelAsync(key, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the other instances to cancel their executions of the task (CancelTask: this instance's were cancelled directly). A
    /// failing coordinator is logged; the cancel still runs here.
    /// </summary>
    internal async Task RequestCancelElsewhereAsync(TaskKey key, CancellationToken ct)
    {
        try
        {
            await _coordinator.RequestCancelAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ServerLog.RequestCancelFailed(_logger, ex, key.TaskId);
        }
    }

    /// <summary>
    /// Whether an execution of the task runs on any instance. Null when none does; otherwise what to wait on before asking
    /// again: the completions of the executions on this instance, or, when only other instances run one, a
    /// <see cref="CoordinationOptions.RunningPollInterval"/> delay. Executions here are checked first, without a coordinator round trip.
    /// </summary>
    internal async ValueTask<List<Task>?> RunningWaitsAsync(TaskKey key, CancellationToken ct)
    {
        var local = RunningCompletions(key);
        if (local.Count > 0) return local;
        if (!await _coordinator.IsRunningAsync(key, ct).ConfigureAwait(false)) return null;
        return [Task.Delay(_options.Coordination.RunningPollInterval, ct)];
    }

    /// <summary>Whether an execution of the task runs on any instance. A failing coordinator counts as not running (logged).</summary>
    internal async Task<bool> IsRunningAnywhereAsync(TaskKey key, CancellationToken ct)
    {
        if (RunningCompletions(key).Count > 0) return true;
        try { return await _coordinator.IsRunningAsync(key, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServerLog.RunningCheckFailed(_logger, ex, key.TaskId);
            return false;
        }
    }

    /// <summary>
    /// Waits until no execution of the task runs on any instance (the cancelled ones and the CancelTask execution), or
    /// <paramref name="grace"/> has passed. When the coordinator fails, waits out the grace period (logged), so CancelTask
    /// still answers.
    /// </summary>
    internal async Task WaitUntilStoppedAsync(TaskKey key, TimeSpan grace, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(grace);
        try
        {
            while (await RunningWaitsAsync(key, timeout.Token).ConfigureAwait(false) is { } running)
                await Task.WhenAny(running).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The grace period is over: answer with the task as it is.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The coordinator failed: other instances cannot be checked, so give them the whole grace period.
            ServerLog.RunningCheckFailed(_logger, ex, key.TaskId);
            try { await Task.Delay(Timeout.Infinite, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
    }

    /// <summary>
    /// For the retention sweep: true when the execution <paramref name="executionId"/>, whose lease expired, still runs on this
    /// instance (its heartbeats lapsed, for example during a long pause). The sweep must then neither fail its task nor remove
    /// its lease. Its next heartbeat registers it again under the same id: when no heartbeat is under way, this marks it
    /// unregistered. A heartbeat under way finds the lease lost and handles it itself; an ending execution unregisters itself
    /// (it holds the heartbeat gate before it unregisters, so a finished execution is never brought back).
    /// </summary>
    internal bool TryReclaimLocal(TaskKey key, string executionId)
    {
        var exec = _running.Snapshot(ScopeKey(key.Tenant, key.TaskId))
            .FirstOrDefault(e => e.Key == key && string.Equals(e.Id, executionId, StringComparison.Ordinal));
        if (exec is null) return false;
        if (exec.HeartbeatGate.Wait(0))
        {
            exec.LeaseLost = true;
            exec.Registered = false;
            exec.HeartbeatGate.Release();
        }
        return true;
    }
}
