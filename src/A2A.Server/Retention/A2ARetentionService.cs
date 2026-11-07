using A2A.Server.Coordination;
using A2A.Server.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace A2A.Server.Retention;

/// <summary>
/// The retention sweep. Every <see cref="A2ARetentionOptions.SweepInterval"/>, on one instance at a time (the cluster-wide
/// sweep lease <see cref="SweepLeaseName"/> of the <see cref="ExecutionCoordinator"/>), it:
/// <list type="number">
/// <item>fails the tasks whose execution stopped without ending (its lease expired: its instance died) with the status text
/// "The agent stopped unexpectedly.", unless another execution of the task still runs;</item>
/// <item>cancels the tasks that waited in <c>INPUT_REQUIRED</c> or <c>AUTH_REQUIRED</c> longer than
/// <see cref="A2AServerOptions.InterruptedTimeout"/>, with no agent running, with the status text "The task waited too long
/// for input.";</item>
/// <item>deletes the terminal tasks older than <see cref="A2ARetentionOptions.TaskRetention"/> with all their data
/// (<see cref="A2ATaskAdmin.DeleteTaskAsync"/>);</item>
/// <item>deletes the contexts older than <see cref="A2ARetentionOptions.OrphanContextRetention"/> that no task uses.</item>
/// </list>
/// Status changes go through the normal commit path, so subscribers and webhooks see them. Each step reads batches of up to
/// <see cref="A2ARetentionOptions.BatchSize"/> items. A failing step or item is logged as a warning and the sweep goes on;
/// the next sweep tries again. Every change is idempotent, so a sweep that overlaps another does no harm. Store-specific
/// cleanup (for example expired idempotency entries in PostgreSQL) stays with the store.
/// </summary>
public sealed class A2ARetentionService : BackgroundService
{
    /// <summary>Name of the cluster-wide lease that lets one instance sweep per interval.</summary>
    public const string SweepLeaseName = "a2a:retention";

    /// <summary>Status text of a task cancelled because it waited longer than <see cref="A2AServerOptions.InterruptedTimeout"/>.</summary>
    internal const string InterruptedTimeoutText = "The task waited too long for input.";

    /// <summary>Status text of a task failed because its execution's lease expired.</summary>
    internal const string StoppedUnexpectedlyText = "The agent stopped unexpectedly.";

    private readonly A2ARetentionOptions _options;
    private readonly TimeSpan? _interruptedTimeout;
    private readonly TaskStore _tasks;
    private readonly ExecutionCoordinator _coordinator;
    private readonly A2ARequestHandler _handler;
    private readonly A2ATaskAdmin _admin;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    /// <summary>Created by DI. The optional <paramref name="time"/> is the clock for the cut-offs and the sweep interval.</summary>
    public A2ARetentionService(IOptions<A2ARetentionOptions> options, IOptions<A2AServerOptions> serverOptions, TaskStore tasks,
        ExecutionCoordinator coordinator, A2ARequestHandler handler, A2ATaskAdmin admin, ILogger<A2ARetentionService>? logger = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serverOptions);
        _options = options.Value;
        _interruptedTimeout = serverOptions.Value.InterruptedTimeout;
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _admin = admin ?? throw new ArgumentNullException(nameof(admin));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Checks the options (<see cref="ValidateOptions"/>), then starts sweeping.</summary>
    /// <exception cref="InvalidOperationException">An option is out of range.</exception>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        ValidateOptions();
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when <see cref="A2ARetentionOptions.Validate"/> does, or when
    /// <see cref="A2AServerOptions.InterruptedTimeout"/> is set and not positive.
    /// </summary>
    internal void ValidateOptions()
    {
        _options.Validate();
        if (_interruptedTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new InvalidOperationException($"InterruptedTimeout ({timeout}) must be greater than zero, or null to turn it off.");
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait first: at startup the stores may still be creating their schema.
        using var timer = new PeriodicTimer(_options.SweepInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
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
                    _logger.LogWarning(ex, "Retention sweep failed; the next sweep tries again");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Takes the sweep lease for one <see cref="A2ARetentionOptions.SweepInterval"/> and, when it got it, runs one sweep.
    /// Returns false when another instance (or this one) holds the lease.
    /// </summary>
    internal async Task<bool> SweepAsync(CancellationToken ct)
    {
        if (!await _coordinator.TryAcquireSweepLeaseAsync(SweepLeaseName, _options.SweepInterval, ct).ConfigureAwait(false)) return false;
        await RunOnceAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>One sweep without the lease: every step, each isolated from the others' failures.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await StepAsync("dead executions", RecoverDeadExecutionsAsync, ct).ConfigureAwait(false);
        if (_interruptedTimeout is { } timeout) await StepAsync("interrupted timeout", c => CancelInterruptedAsync(timeout, c), ct).ConfigureAwait(false);
        await StepAsync("expired tasks", PurgeExpiredTasksAsync, ct).ConfigureAwait(false);
        await StepAsync("orphan contexts", DeleteOrphanContextsAsync, ct).ConfigureAwait(false);
    }

    private async Task StepAsync(string name, Func<CancellationToken, Task> step, CancellationToken ct)
    {
        try
        {
            await step(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retention step '{Step}' failed; the next sweep tries again", name);
        }
    }

    /// <summary>
    /// Reads batches with <paramref name="list"/> and handles each item with <paramref name="handle"/> (true when the item
    /// will not be listed again). Reads the next batch only while the last one was full and every item in it was handled,
    /// so items that stay listed (skipped or failed) never make a sweep loop. A failing item is logged and skipped.
    /// </summary>
    private async Task BatchesAsync<T>(string what, Func<CancellationToken, ValueTask<IReadOnlyList<T>>> list,
        Func<T, CancellationToken, Task<bool>> handle, Func<T, string> describe, CancellationToken ct)
    {
        while (true)
        {
            var batch = await list(ct).ConfigureAwait(false);
            var handled = 0;
            foreach (var item in batch)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (await handle(item, ct).ConfigureAwait(false)) handled++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Retention could not handle {What} {Item}; the next sweep tries again", what, describe(item));
                }
            }
            if (batch.Count < _options.BatchSize || handled < batch.Count) return;
        }
    }

    private Task RecoverDeadExecutionsAsync(CancellationToken ct) =>
        BatchesAsync("expired execution", c => _coordinator.ListExpiredAsync(_options.BatchSize, c), RecoverAsync, e => $"of task {e.Task.TaskId}", ct);

    /// <summary>
    /// An execution's lease expired: its instance died. Unless another execution of the task runs, a task that is not terminal
    /// becomes FAILED. The expired lease is always removed, so it is not listed again.
    /// </summary>
    private async Task<bool> RecoverAsync((TaskKey Task, string ExecutionId) expired, CancellationToken ct)
    {
        var (key, executionId) = expired;
        if (!await _coordinator.IsRunningAsync(key, ct).ConfigureAwait(false)
            && await _tasks.GetAsync(key, ct).ConfigureAwait(false) is { } record
            && !record.Task.Status.State.IsTerminal())
        {
            if (await _handler.CommitSystemStatusAsync(key, TaskState.Failed, StoppedUnexpectedlyText, record.Version, ct).ConfigureAwait(false))
                _logger.LogWarning("Task {TaskId}: its execution stopped without ending (its instance died); the task failed", key.TaskId);
        }
        await _coordinator.UnregisterAsync(key, executionId, ct).ConfigureAwait(false);
        return true;
    }

    private Task CancelInterruptedAsync(TimeSpan timeout, CancellationToken ct)
    {
        var cutoff = Before(_time.GetUtcNow(), timeout);
        return BatchesAsync("interrupted task", c => _tasks.ListInterruptedBeforeAsync(cutoff, _options.BatchSize, c),
            (key, c) => CancelIfWaitingAsync(key, cutoff, c), key => key.TaskId, ct);
    }

    /// <summary>
    /// Cancels a task that is still interrupted since before <paramref name="cutoff"/> and that no execution runs. A task
    /// that changed meanwhile (a follow-up arrived) is skipped; the next sweep looks at it again.
    /// </summary>
    private async Task<bool> CancelIfWaitingAsync(TaskKey key, DateTimeOffset cutoff, CancellationToken ct)
    {
        if (await _tasks.GetAsync(key, ct).ConfigureAwait(false) is not { } record) return true;
        var status = record.Task.Status;
        if (!status.State.IsInterrupted() || (status.Timestamp ?? record.CreatedAt) >= cutoff) return true;
        if (await _coordinator.IsRunningAsync(key, ct).ConfigureAwait(false)) return false;
        if (!await _handler.CommitSystemStatusAsync(key, TaskState.Canceled, InterruptedTimeoutText, record.Version, ct).ConfigureAwait(false)) return false;
        _logger.LogInformation("Task {TaskId} waited for input longer than {Timeout}; cancelled", key.TaskId, _interruptedTimeout);
        return true;
    }

    private Task PurgeExpiredTasksAsync(CancellationToken ct)
    {
        var cutoff = Before(_time.GetUtcNow(), _options.TaskRetention);
        return BatchesAsync("expired task", c => _tasks.ListExpiredAsync(cutoff, _options.BatchSize, c),
            async (key, c) =>
            {
                await _admin.PurgeAsync(key, c).ConfigureAwait(false);
                return true;
            }, key => key.TaskId, ct);
    }

    private Task DeleteOrphanContextsAsync(CancellationToken ct)
    {
        var cutoff = Before(_time.GetUtcNow(), _options.OrphanContextRetention);
        // False: a task uses it again, or it is gone already. Either way it is not an orphan to delete now.
        return BatchesAsync("orphan context", c => _tasks.ListOrphanContextsAsync(cutoff, _options.BatchSize, c),
            async (context, c) => await _tasks.DeleteContextAsync(context.Tenant, context.ContextId, c).ConfigureAwait(false),
            context => context.ContextId, ct);
    }

    /// <summary><paramref name="now"/> minus <paramref name="age"/>, or the earliest time when that is out of range.</summary>
    private static DateTimeOffset Before(DateTimeOffset now, TimeSpan age) =>
        now - DateTimeOffset.MinValue > age ? now - age : DateTimeOffset.MinValue;
}
