using A2A.Server.Coordination;
using A2A.Server.Execution;
using A2A.Server.Storage;
using A2A.Server.Telemetry;
using A2A.Server.Uploads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace A2A.Server.Retention;

/// <summary>
///     The retention sweep. Every <see cref="A2ARetentionOptions.SweepInterval" />, on one instance at a time (the
///     cluster-wide
///     sweep lease <see cref="SweepLeaseName" /> of the <see cref="ExecutionCoordinator" />), it:
///     <list type="number">
///         <item>
///             fails the SUBMITTED or WORKING tasks whose execution stopped without ending (its lease expired: its
///             instance died)
///             with the status text "The agent stopped unexpectedly.", unless another execution of the task still runs or
///             the task
///             changed within the last <see cref="CoordinationOptions.LeaseTtl" />, and asks every instance to cancel the
///             task's
///             executions. An execution whose heartbeats lapse past <see cref="CoordinationOptions.LeaseTtl" /> on another
///             instance (for
///             example a long pause, or a coordinator outage) is treated as dead: its task is marked FAILED, its later
///             events are dropped,
///             and its agent is cancelled. One that lapses on the sweeping instance is still seen running, and registers
///             again. An
///             expired start claim of a proactive continuation is only removed, and its task is not changed;
///         </item>
///         <item>
///             cancels the tasks that waited in <c>INPUT_REQUIRED</c> or <c>AUTH_REQUIRED</c> longer than
///             <see cref="A2AServerOptions.InterruptedTimeout" />, with no agent running and no update (such as a
///             committed reply) since,
///             with the status text "The task waited too long for input.";
///         </item>
///         <item>
///             deletes the terminal tasks older than <see cref="A2ARetentionOptions.TaskRetention" /> with all their data
///             (<see cref="A2ATaskAdmin.DeleteTaskAsync(Storage.TaskKey, CancellationToken)" />);
///         </item>
///         <item>
///             deletes the contexts older than <see cref="A2ARetentionOptions.OrphanContextRetention" /> that no task
///             uses, and tells every <see cref="IA2AContextLifecycle" /> about each one;
///         </item>
///         <item>
///             when an <see cref="ArtifactObjectStore" /> is registered, deletes the artifact objects older than
///             <see cref="ArtifactStorageOptions.OrphanGracePeriod" /> whose task no longer exists or does not refer to
///             them. A task that
///             cannot be read keeps its objects;
///         </item>
///         <item>
///             when <see cref="ArtifactUploadOptions.Enabled" /> is true and an object store is registered, deletes the
///             inbound uploads
///             that no task uses, once they are older than <see cref="ArtifactUploadOptions.UnusedUploadTtl" /> plus
///             <see cref="ArtifactStorageOptions.OrphanGracePeriod" />, and the references that point at nothing. A task
///             that cannot be read
///             keeps its uploads. At most <see cref="A2ARetentionOptions.BatchSize" /> objects per sweep, and at most once
///             per
///             <see cref="ArtifactStorageOptions.OrphanSweepInterval" />.
///         </item>
///     </list>
///     Status changes go through the normal commit path, so subscribers and webhooks see them. Each step reads batches of
///     <see cref="A2ARetentionOptions.BatchSize" /> new items (plus the items it skipped, which stay listed). A failing
///     step or item is logged as a warning and the sweep goes on;
///     the next sweep tries again. Items a step skips (for example a task whose follow-up runs) do not hold back the items
///     listed after them. Every change is idempotent, so a sweep that overlaps another does no harm. Store-specific
///     cleanup (for example expired idempotency entries in PostgreSQL) stays with the store.
/// </summary>
public sealed class A2ARetentionService : BackgroundService
{
    /// <summary>Name of the cluster-wide lease that lets one instance sweep per interval.</summary>
    public const string SweepLeaseName = "a2a:retention";

    /// <summary>
    ///     Status text of a task cancelled because it waited longer than
    ///     <see cref="A2AServerOptions.InterruptedTimeout" />.
    /// </summary>
    internal const string InterruptedTimeoutText = "The task waited too long for input.";

    /// <summary>Status text of a task failed because its execution's lease expired.</summary>
    internal const string StoppedUnexpectedlyText = "The agent stopped unexpectedly.";

    private readonly A2ATaskAdmin _admin;
    private readonly ExecutionCoordinator _coordinator;
    private readonly TaskExecutionEngine _engine;
    private readonly TimeSpan? _interruptedTimeout;
    private readonly TimeSpan _leaseTtl;
    private readonly IA2AContextLifecycle[] _lifecycles;
    private readonly ILogger _logger;
    private readonly ArtifactObjectStore? _objects;

    private readonly A2ARetentionOptions _options;
    private readonly TimeSpan _orphanGracePeriod;
    private readonly TimeSpan _orphanSweepInterval;
    private readonly TaskStore _tasks;
    private readonly TimeProvider _time;
    private readonly InboundFiles? _uploads;
    private DateTimeOffset? _lastOrphanStep;
    private DateTimeOffset? _lastUploadStep;

    /// <summary>Creates the sweep. In an app the DI container does this (<c>AddA2ARetention</c>).</summary>
    /// <param name="options">The sweep settings.</param>
    /// <param name="serverOptions">
    ///     Server options (<see cref="A2AServerOptions.InterruptedTimeout" />,
    ///     <see cref="A2AServerOptions.Coordination" />).
    /// </param>
    /// <param name="tasks">The task store.</param>
    /// <param name="coordinator">The execution coordinator (expired leases, the sweep lease).</param>
    /// <param name="engine">Commits status changes through the normal path and cancels executions.</param>
    /// <param name="admin">Deletes expired tasks with their data.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="time">The clock for the cut-offs and the sweep interval. Default <see cref="TimeProvider.System" />.</param>
    /// <param name="objects">The artifact object store. Without it there is no orphan object step.</param>
    /// <remarks>
    ///     Kept for code built against an earlier version. New code uses the constructor that also takes the context
    ///     lifecycle hooks; its last four parameters are optional.
    /// </remarks>
    public A2ARetentionService(IOptions<A2ARetentionOptions> options, A2AServerOptions serverOptions, TaskStore tasks,
        ExecutionCoordinator coordinator, TaskExecutionEngine engine, A2ATaskAdmin admin,
        ILogger<A2ARetentionService>? logger, TimeProvider? time, ArtifactObjectStore? objects)
        : this(options, serverOptions, tasks, coordinator, engine, admin, logger, time, objects, null)
    {
    }

    /// <summary>
    ///     Creates the sweep with hooks that hear of every context it deletes. In an app the DI container does this
    ///     (<c>AddA2ARetention</c>), with every registered <see cref="IA2AContextLifecycle" />.
    /// </summary>
    /// <param name="options">The sweep settings.</param>
    /// <param name="serverOptions">
    ///     Server options (<see cref="A2AServerOptions.InterruptedTimeout" />,
    ///     <see cref="A2AServerOptions.Coordination" />).
    /// </param>
    /// <param name="tasks">The task store.</param>
    /// <param name="coordinator">The execution coordinator (expired leases, the sweep lease).</param>
    /// <param name="engine">Commits status changes through the normal path and cancels executions.</param>
    /// <param name="admin">Deletes expired tasks with their data.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="time">The clock for the cut-offs and the sweep interval. Default <see cref="TimeProvider.System" />.</param>
    /// <param name="objects">The artifact object store. Without it there is no orphan object step.</param>
    /// <param name="lifecycles">Called for each context the sweep deletes, in order. Null or empty for none.</param>
    /// <exception cref="ArgumentException"><paramref name="lifecycles" /> contains null.</exception>
    public A2ARetentionService(IOptions<A2ARetentionOptions> options, A2AServerOptions serverOptions, TaskStore tasks,
        ExecutionCoordinator coordinator, TaskExecutionEngine engine, A2ATaskAdmin admin,
        ILogger<A2ARetentionService>? logger = null, TimeProvider? time = null, ArtifactObjectStore? objects = null,
        IEnumerable<IA2AContextLifecycle>? lifecycles = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serverOptions);
        _options = options.Value;
        _interruptedTimeout = serverOptions.InterruptedTimeout;
        _leaseTtl = serverOptions.Coordination.LeaseTtl;
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _admin = admin ?? throw new ArgumentNullException(nameof(admin));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _objects = objects;
        _lifecycles = lifecycles?.ToArray() ?? [];
        if (Array.IndexOf(_lifecycles, null) >= 0)
            throw new ArgumentException("A context lifecycle hook is null.", nameof(lifecycles));
        _orphanGracePeriod = serverOptions.ArtifactStorage.OrphanGracePeriod;
        _orphanSweepInterval = serverOptions.ArtifactStorage.OrphanSweepInterval;
        // Built here, not injected: the sweep only needs the store, the options and the clock.
        if (objects is not null && serverOptions.ArtifactStorage.Uploads.Enabled)
            _uploads = new InboundFiles(objects, serverOptions.ArtifactStorage, _time, logger);
    }

    /// <summary>
    ///     Creates the sweep from <paramref name="services" />, as <c>AddA2ARetention</c> registers it: the required
    ///     services, the optional logger, clock and artifact object store when registered, and every
    ///     <see cref="IA2AContextLifecycle" />. <paramref name="options" /> replaces the registered
    ///     <see cref="A2ARetentionOptions" /> when given.
    /// </summary>
    internal static A2ARetentionService FromServices(IServiceProvider services,
        IOptions<A2ARetentionOptions>? options = null)
    {
        return new A2ARetentionService(
            options ?? services.GetRequiredService<IOptions<A2ARetentionOptions>>(),
            services.GetRequiredService<A2AServerOptions>(),
            services.GetRequiredService<TaskStore>(),
            services.GetRequiredService<ExecutionCoordinator>(),
            services.GetRequiredService<TaskExecutionEngine>(),
            services.GetRequiredService<A2ATaskAdmin>(),
            services.GetService<ILogger<A2ARetentionService>>(),
            services.GetService<TimeProvider>(),
            services.GetService<ArtifactObjectStore>(),
            services.GetServices<IA2AContextLifecycle>());
    }

    /// <summary>Checks the options (<see cref="ValidateOptions" />), then starts sweeping.</summary>
    /// <exception cref="InvalidOperationException">An option is out of range.</exception>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        ValidateOptions();
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    ///     Throws <see cref="InvalidOperationException" /> when <see cref="A2ARetentionOptions.Validate" /> does, or when
    ///     <see cref="A2AServerOptions.InterruptedTimeout" /> is set and not positive, or when an artifact object store is
    ///     registered and
    ///     <see cref="ArtifactStorageOptions.OrphanGracePeriod" /> or
    ///     <see cref="ArtifactStorageOptions.OrphanSweepInterval" /> is not positive.
    /// </summary>
    internal void ValidateOptions()
    {
        _options.Validate();
        // A grace period of zero or less puts the cut-off at now or later: an object written but not yet committed would be
        // deleted, and the commit would then create a task that points at nothing.
        if (_objects is not null && _orphanGracePeriod <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"ArtifactStorage.OrphanGracePeriod ({_orphanGracePeriod}) must be greater than zero.");
        if (_objects is not null && _orphanSweepInterval <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"ArtifactStorage.OrphanSweepInterval ({_orphanSweepInterval}) must be greater than zero.");
        if (_interruptedTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"InterruptedTimeout ({timeout}) must be greater than zero, or null to turn it off.");
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait first: at startup the stores may still be creating their schema.
        using var timer = new PeriodicTimer(_options.SweepInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                try
                {
                    await SweepAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ServerLog.SweepFailed(_logger, ex);
                }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    ///     Takes the sweep lease for one <see cref="A2ARetentionOptions.SweepInterval" /> and, when it got it, runs one sweep.
    ///     Returns false when another instance (or this one) holds the lease.
    /// </summary>
    internal async Task<bool> SweepAsync(CancellationToken cancellationToken)
    {
        if (!await _coordinator.TryAcquireSweepLeaseAsync(SweepLeaseName, _options.SweepInterval, cancellationToken)
                .ConfigureAwait(false)) return false;
        await RunOnceAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>One sweep without the lease: every step, each isolated from the others' failures.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await StepAsync("dead executions", RecoverDeadExecutionsAsync, cancellationToken).ConfigureAwait(false);
        if (_interruptedTimeout is { } timeout)
            await StepAsync("interrupted timeout", c => CancelInterruptedAsync(timeout, c), cancellationToken)
                .ConfigureAwait(false);
        await StepAsync("expired tasks", PurgeExpiredTasksAsync, cancellationToken).ConfigureAwait(false);
        await StepAsync("orphan contexts", DeleteOrphanContextsAsync, cancellationToken).ConfigureAwait(false);
        if (_objects is { } objects && OrphanStepDue())
            await StepAsync("orphan artifact objects", async c =>
            {
                // A failed step, or one that stopped at BatchSize, is not recorded: the next sweep runs it again, so a backlog drains.
                if (!await DeleteOrphanObjectsAsync(objects, c).ConfigureAwait(false))
                    _lastOrphanStep = _time.GetUtcNow();
            }, cancellationToken).ConfigureAwait(false);
        if (_uploads is { } uploads && UploadStepDue())
            await StepAsync("unused uploads", async c =>
            {
                // Recorded like the orphan step: a failed or batch-limited run is not, so a backlog drains on the next sweep.
                if (!await uploads.SweepAsync(_tasks, _options.BatchSize, c).ConfigureAwait(false))
                    _lastUploadStep = _time.GetUtcNow();
            }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     True when this instance has not run the unused-uploads step, or the last run is at least
    ///     <see cref="ArtifactStorageOptions.OrphanSweepInterval" /> ago.
    /// </summary>
    private bool UploadStepDue()
    {
        return _lastUploadStep is not { } last || _time.GetUtcNow() - last >= _orphanSweepInterval;
    }

    /// <summary>
    ///     True when this instance has not run the orphan step, or the last run is at least
    ///     <see cref="ArtifactStorageOptions.OrphanSweepInterval" /> ago.
    /// </summary>
    private bool OrphanStepDue()
    {
        return _lastOrphanStep is not { } last || _time.GetUtcNow() - last >= _orphanSweepInterval;
    }

    private async Task StepAsync(string name, Func<CancellationToken, Task> step, CancellationToken cancellationToken)
    {
        try
        {
            await step(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ServerLog.SweepStepFailed(_logger, ex, name);
        }
    }

    /// <summary>
    ///     Reads batches with <paramref name="list" /> and handles each item with <paramref name="handle" /> (true when the
    ///     item
    ///     will not be listed again). An item that stays listed (skipped or failed) stays at the head of every later read, so
    ///     the
    ///     next read asks for that many more items and only the ones not seen yet are handled: a head of skipped items (for
    ///     example interrupted tasks whose follow-up runs) never hides the items behind it. Reading stops when a read returns
    ///     fewer items than asked for (the end) or nothing new. A failing item is logged and skipped.
    /// </summary>
    private async Task BatchesAsync<T>(string what, Func<int, CancellationToken, ValueTask<IReadOnlyList<T>>> list,
        Func<T, CancellationToken, Task<bool>> handle, Func<T, string> describe, CancellationToken cancellationToken)
        where T : notnull
    {
        var seen = new HashSet<T>();
        var skipped = 0;
        while (true)
        {
            var max = (int)Math.Min(int.MaxValue, (long)skipped + _options.BatchSize);
            var batch = await list(max, cancellationToken).ConfigureAwait(false);
            var fresh = 0;
            foreach (var item in batch)
            {
                if (!seen.Add(item)) continue;
                fresh++;
                cancellationToken.ThrowIfCancellationRequested();
                var handled = false;
                try
                {
                    handled = await handle(item, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ServerLog.SweepItemFailed(_logger, ex, what, describe(item));
                }

                if (!handled) skipped++;
            }

            if (batch.Count < max || fresh == 0) return;
        }
    }

    private Task RecoverDeadExecutionsAsync(CancellationToken cancellationToken)
    {
        return BatchesAsync("expired execution", (max, c) => _coordinator.ListExpiredAsync(max, c), RecoverAsync,
            e => $"of task {e.Task.TaskId}", cancellationToken);
    }

    /// <summary>
    ///     A lease expired. A start claim of a continuation (<see cref="TaskExecutionEngine.ContinuationClaimPrefix" />) is
    ///     only
    ///     removed: it is not an execution, so its task is never read or changed. Otherwise the execution's instance died: a
    ///     task still SUBMITTED or WORKING becomes FAILED, and every instance is
    ///     asked to cancel its executions (one that only lapsed may still run). Exceptions:
    ///     <list type="bullet">
    ///         <item>
    ///             The execution still runs on this instance (its heartbeats lapsed): nothing changes, and its next heartbeat
    ///             registers it again (<c>TaskExecutionEngine.TryReclaimLocal</c>).
    ///         </item>
    ///         <item>Another execution of the task runs: the task is left alone.</item>
    ///         <item>
    ///             The task changed since it was read (read first, so a follow-up that starts meanwhile is seen as a change),
    ///             or within
    ///             the last <see cref="CoordinationOptions.LeaseTtl" /> (a follow-up on another instance may have committed
    ///             and not
    ///             registered its execution yet), or an execution of it runs here: the task is left alone for now and the
    ///             lease stays
    ///             listed, so the next sweep checks again.
    ///         </item>
    ///     </list>
    ///     An interrupted task is left to the interrupted timeout. Otherwise the expired lease is removed, so it is not listed
    ///     again. Returns true when the lease was removed.
    /// </summary>
    private async Task<bool> RecoverAsync((TaskKey Task, string ExecutionId) expired,
        CancellationToken cancellationToken)
    {
        var (key, executionId) = expired;
        if (executionId.StartsWith(TaskExecutionEngine.ContinuationClaimPrefix, StringComparison.Ordinal))
        {
            await _coordinator.UnregisterAsync(key, executionId, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (_engine.TryReclaimLocal(key, executionId))
        {
            ServerLog.SweepReclaimedLocal(_logger, key.TaskId);
            return false; // listed until its next heartbeat registers it again
        }

        if (await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) is
                { Task.Status.State: TaskState.Submitted or TaskState.Working } record
            && !await _coordinator.IsRunningAsync(key, cancellationToken).ConfigureAwait(false))
        {
            var seen = record.Version;
            var quietSince = Before(_time.GetUtcNow(), _leaseTtl);
            if (!await _engine.CommitSystemStatusAsync(key, TaskState.Failed, StoppedUnexpectedlyText,
                    r => r.Version == seen && r.UpdatedAt < quietSince, cancellationToken).ConfigureAwait(false))
                return false; // changed, recently changed, or runs here: the next sweep checks again
            ServerLog.SweepFailedDeadExecution(_logger, key.TaskId);
            await CancelEverywhereAsync(key, cancellationToken).ConfigureAwait(false);
        }

        await _coordinator.UnregisterAsync(key, executionId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    ///     Asks every instance to cancel the task's executions: one that only lapsed (a long pause) may still run its agent. A
    ///     failure is logged; that agent then stops at its next heartbeat, which finds its lease lost and the task terminal.
    /// </summary>
    private async Task CancelEverywhereAsync(TaskKey key, CancellationToken cancellationToken)
    {
        try
        {
            await _engine.CancelExecutionsAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            ServerLog.RequestCancelFailed(_logger, ex, key.TaskId);
        }
    }

    private Task CancelInterruptedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var cutoff = Before(_time.GetUtcNow(), timeout);
        return BatchesAsync("interrupted task", (max, c) => _tasks.ListInterruptedBeforeAsync(cutoff, max, c),
            (key, c) => CancelIfWaitingAsync(key, cutoff, c), key => key.TaskId, cancellationToken);
    }

    /// <summary>
    ///     Cancels a task that is still interrupted since before <paramref name="cutoff" />, was not updated since then (a
    ///     committed
    ///     follow-up counts as activity, even before its execution starts), and that no execution runs. The checks are
    ///     repeated
    ///     on the record the commit reads under the task lock. Returns true when the task will not be listed again.
    /// </summary>
    private async Task<bool> CancelIfWaitingAsync(TaskKey key, DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        if (await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) is not { } record) return true;
        var status = record.Task.Status;
        if (!status.State.IsInterrupted() || (status.Timestamp ?? record.CreatedAt) >= cutoff)
            return true; // no longer listed
        if (record.UpdatedAt >= cutoff) return false; // a reply arrived; listed until its execution changes the status
        if (await _coordinator.IsRunningAsync(key, cancellationToken).ConfigureAwait(false)) return false;
        var seen = record.Version;
        if (!await _engine.CommitSystemStatusAsync(key, TaskState.Canceled, InterruptedTimeoutText,
                    r => r.Version == seen && StillWaiting(r, cutoff), cancellationToken)
                .ConfigureAwait(false)) return false;
        ServerLog.SweepCancelledInterrupted(_logger, key.TaskId, _interruptedTimeout);
        return true;
    }

    /// <summary>Interrupted, with its status and its last update both older than <paramref name="cutoff" />.</summary>
    internal static bool StillWaiting(TaskRecord r, DateTimeOffset cutoff)
    {
        return r.Task.Status.State.IsInterrupted() && (r.Task.Status.Timestamp ?? r.CreatedAt) < cutoff &&
               r.UpdatedAt < cutoff;
    }

    private Task PurgeExpiredTasksAsync(CancellationToken cancellationToken)
    {
        var cutoff = Before(_time.GetUtcNow(), _options.TaskRetention);
        return BatchesAsync("expired task", (max, c) => _tasks.ListExpiredAsync(cutoff, max, c),
            async (key, c) =>
            {
                await _admin.PurgeAsync(key, c).ConfigureAwait(false);
                return true;
            }, key => key.TaskId, cancellationToken);
    }

    private Task DeleteOrphanContextsAsync(CancellationToken cancellationToken)
    {
        var cutoff = Before(_time.GetUtcNow(), _options.OrphanContextRetention);
        // False: a task uses it again, or it is gone already. Either way it is not an orphan to delete now.
        return BatchesAsync("orphan context", (max, c) => _tasks.ListOrphanContextsAsync(cutoff, max, c),
            async (context, c) =>
            {
                if (!await _tasks.DeleteContextAsync(context.Tenant, context.ContextId, c).ConfigureAwait(false))
                    return false;
                await NotifyContextDeletedAsync(context, c).ConfigureAwait(false);
                return true;
            },
            context => context.ContextId, cancellationToken);
    }

    /// <summary>
    ///     Tells every hook. Each one is isolated: a failure is logged, and the next hook still hears of the context. The
    ///     context is deleted already, so the item counts as done either way.
    /// </summary>
    private async Task NotifyContextDeletedAsync(ContextRecord context, CancellationToken cancellationToken)
    {
        foreach (var lifecycle in _lifecycles)
            try
            {
                await lifecycle.OnContextDeletedAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ServerLog.ContextLifecycleHookFailed(_logger, ex, lifecycle.GetType().Name, context.ContextId);
            }
    }

    /// <summary>
    ///     Deletes the objects older than the grace period whose task is gone or no longer refers to them, at most
    ///     <see cref="A2ARetentionOptions.BatchSize" /> per sweep. The task is read without its owner, so a live task of
    ///     anyone counts. Any doubt keeps
    ///     the object: a key that is not one of ours, a task that cannot be read, a delete that fails. Younger objects are
    ///     never
    ///     touched: their task may not be committed yet. Returns true when it stopped at the batch size, with more listed.
    /// </summary>
    private async Task<bool> DeleteOrphanObjectsAsync(ArtifactObjectStore objects, CancellationToken cancellationToken)
    {
        var cutoff = Before(_time.GetUtcNow(), _orphanGracePeriod);
        // Only the current task is kept. Stores list in key order, so the objects of one task come together; any other order only costs more reads.
        (string Tenant, string TaskId)? currentTask = null;
        HashSet<string>? used = null;
        var deleted = 0;
        await foreach (var entry in objects.ListAsync(string.Empty, cancellationToken).ConfigureAwait(false))
        {
            if (deleted >= _options.BatchSize) return true;
            if (entry.CreatedAt >= cutoff) continue;
            if (!ArtifactObjectKey.TryParse(entry.Key, out var tenant, out var taskId, out var objectId)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (currentTask != (tenant, taskId))
                {
                    var record = await _tasks.GetForMaintenanceAsync(tenant, taskId, cancellationToken)
                        .ConfigureAwait(false);
                    used = record is null ? null : ReferencedObjectIds(record.Task);
                    currentTask = (tenant, taskId);
                }

                if (used is not null && used.Contains(objectId)) continue; // a live task uses it
                await objects.DeleteAsync(entry.Key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException ||
                                       !cancellationToken.IsCancellationRequested)
            {
                ServerLog.ArtifactObjectCheckFailed(_logger, ex, entry.Key);
                continue;
            }

            deleted++;
            ServerTelemetry.OrphanObjectsDeleted.Add(1);
            ServerLog.ArtifactOrphanDeleted(_logger, entry.Key);
        }

        return false;
    }

    /// <summary>The object ids of every artifact link in the task's artifacts, history and status message.</summary>
    private static HashSet<string> ReferencedObjectIds(AgentTask task)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        void Add(IEnumerable<Part>? parts)
        {
            if (parts is null) return;
            foreach (var part in parts)
                if (part is UrlPart url && ArtifactLink.TryGetObjectId(url.Url, out var id))
                    ids.Add(id);
        }

        if (task.Artifacts is { } artifacts)
            foreach (var artifact in artifacts)
                Add(artifact.Parts);
        if (task.History is { } history)
            foreach (var message in history)
                Add(message.Parts);
        Add(task.Status?.Message?.Parts);
        return ids;
    }

    /// <summary><paramref name="now" /> minus <paramref name="age" />, or the earliest time when that is out of range.</summary>
    internal static DateTimeOffset Before(DateTimeOffset now, TimeSpan age)
    {
        return now - DateTimeOffset.MinValue > age ? now - age : DateTimeOffset.MinValue;
    }
}