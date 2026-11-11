namespace A2A.Client.Push;

/// <summary>Why <see cref="PushTaskTracker" /> asked the server for a task.</summary>
public enum PushCheckReason
{
    /// <summary>The deadline passed with no final notification.</summary>
    DeadlinePassed,

    /// <summary>A notification skipped one or more sequence numbers, so a notification was lost.</summary>
    SequenceGap
}

/// <summary>
///     The result of a check by <see cref="PushTaskTracker" />, given to its callback.
/// </summary>
public sealed class PushTaskCheck
{
    /// <summary>Creates a check result.</summary>
    public PushTaskCheck(string taskId, PushCheckReason reason, AgentTask? task, Exception? error)
    {
        TaskId = taskId;
        Reason = reason;
        Task = task;
        Error = error;
    }

    /// <summary>The task that was checked.</summary>
    public string TaskId { get; }

    /// <summary>Why it was checked.</summary>
    public PushCheckReason Reason { get; }

    /// <summary>The task as <c>GetTask</c> returned it, or null when the call failed.</summary>
    public AgentTask? Task { get; }

    /// <summary>The exception when <c>GetTask</c> failed. The task stays tracked and is checked again after its timeout.</summary>
    public Exception? Error { get; }
}

/// <summary>A task watched by <see cref="PushTaskTracker" />. Immutable: a store keeps it as given.</summary>
public sealed class PushTrackedTask
{
    /// <summary>Creates an entry.</summary>
    public PushTrackedTask(string taskId, DateTimeOffset deadline, TimeSpan timeout, long? lastSequence)
    {
        TaskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
        Deadline = deadline;
        Timeout = timeout;
        LastSequence = lastSequence;
    }

    /// <summary>The task id.</summary>
    public string TaskId { get; }

    /// <summary>When the next check is due, if no final notification arrives first.</summary>
    public DateTimeOffset Deadline { get; }

    /// <summary>The timeout the task was registered with. A failed or non-final check sets the next deadline this far ahead.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>The highest <c>X-A2A-Notification-Sequence</c> seen for the task, or null.</summary>
    public long? LastSequence { get; }

    internal PushTrackedTask WithLastSequence(long? lastSequence)
    {
        return new PushTrackedTask(TaskId, Deadline, Timeout, lastSequence);
    }

    internal PushTrackedTask WithDeadline(DateTimeOffset deadline)
    {
        return new PushTrackedTask(TaskId, deadline, Timeout, LastSequence);
    }
}

/// <summary>
///     Stores the tasks a <see cref="PushTaskTracker" /> watches. Implement it over a database so tracking survives a
///     restart.
///     Implementations must be thread-safe.
/// </summary>
public interface IPushTaskStore
{
    /// <summary>Adds the task, or replaces the entry with the same id.</summary>
    Task SaveAsync(PushTrackedTask task, CancellationToken cancellationToken);

    /// <summary>Returns the entry, or null when the task is not tracked.</summary>
    Task<PushTrackedTask?> GetAsync(string taskId, CancellationToken cancellationToken);

    /// <summary>Returns up to <paramref name="max" /> entries whose deadline is at or before <paramref name="now" />.</summary>
    Task<IReadOnlyList<PushTrackedTask>> ListDueAsync(DateTimeOffset now, int max, CancellationToken cancellationToken);

    /// <summary>Removes the entry. Does nothing when there is none.</summary>
    Task RemoveAsync(string taskId, CancellationToken cancellationToken);
}

/// <summary>The default <see cref="IPushTaskStore" />: entries are kept in memory and lost on restart. Thread-safe.</summary>
public sealed class InMemoryPushTaskStore : IPushTaskStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PushTrackedTask> _tasks = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SaveAsync(PushTrackedTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            _tasks[task.TaskId] = task;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PushTrackedTask?> GetAsync(string taskId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_tasks.TryGetValue(taskId, out var t) ? t : null);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PushTrackedTask>> ListDueAsync(DateTimeOffset now, int max,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var due = _tasks.Values.Where(t => t.Deadline <= now).OrderBy(t => t.Deadline).Take(max).ToList();
            return Task.FromResult<IReadOnlyList<PushTrackedTask>>(due);
        }
    }

    /// <inheritdoc />
    public Task RemoveAsync(string taskId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _tasks.Remove(taskId);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
///     Notices lost webhooks. A webhook that was acknowledged and then lost cannot be detected by the server, so the
///     client
///     watches for it: register a task with a deadline, and report every notification you receive. When the deadline
///     passes with no
///     final state, or a notification skips a sequence number (<c>X-A2A-Notification-Sequence</c>), the tracker calls
///     <c>GetTask</c>
///     and gives the result to your callback. A webhook is a hint; the task is the truth.
/// </summary>
/// <remarks>
///     "Final" means the task is terminal or interrupted (input or auth required), as in
///     <see cref="A2AClient.WaitForTaskAsync" />:
///     no more notifications come until you act. The entry is then removed. After a check that finds the task still
///     running, the
///     next deadline is the registered timeout from now. A failed <c>GetTask</c> is reported with
///     <see cref="PushTaskCheck.Error" />
///     and retried after the timeout. Exceptions thrown by the callback are swallowed, so one bad callback cannot stop the
///     sweep.
///     Overdue entries are found by a periodic sweep (default every 5 seconds), so a check may run a little after the
///     deadline.
///     Dispose the tracker to stop the sweep. Thread-safe.
/// </remarks>
public sealed class PushTaskTracker : IDisposable
{
    private const int SweepBatch = 100;
    private readonly HashSet<string> _checking = new(StringComparer.Ordinal);

    private readonly IA2AClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<PushTaskCheck, CancellationToken, Task> _onCheck;
    private readonly IPushTaskStore _store;
    private readonly TimeProvider _time;
    private readonly ITimer? _timer;
    private int _sweeping;

    /// <summary>Creates a tracker.</summary>
    /// <param name="client">Used to call <c>GetTask</c>.</param>
    /// <param name="onCheck">Called with the result of every check. This is how the app learns the task state.</param>
    /// <param name="store">Where tracked tasks are kept. Default <see cref="InMemoryPushTaskStore" />.</param>
    /// <param name="time">The clock. Default <see cref="TimeProvider.System" />.</param>
    /// <param name="sweepInterval">
    ///     How often overdue entries are looked for. Default 5 seconds.
    ///     <see cref="Timeout.InfiniteTimeSpan" /> turns the timer off; then call <see cref="CheckDueAsync" /> yourself.
    /// </param>
    public PushTaskTracker(IA2AClient client, Func<PushTaskCheck, CancellationToken, Task> onCheck,
        IPushTaskStore? store = null, TimeProvider? time = null, TimeSpan? sweepInterval = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _onCheck = onCheck ?? throw new ArgumentNullException(nameof(onCheck));
        _store = store ?? new InMemoryPushTaskStore();
        _time = time ?? TimeProvider.System;
        var interval = sweepInterval ?? TimeSpan.FromSeconds(5);
        if (interval != Timeout.InfiniteTimeSpan)
        {
            if (interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(sweepInterval), interval,
                    "The sweep interval must be positive.");
            _timer = _time.CreateTimer(_ => _ = SweepFromTimerAsync(), null, interval, interval);
        }
    }

    /// <summary>Stops the sweep timer.</summary>
    public void Dispose()
    {
        _timer?.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    ///     Starts watching <paramref name="taskId" />. If no final notification arrives within <paramref name="timeout" />,
    ///     the task is checked.
    ///     Tracking a task again replaces its entry.
    /// </summary>
    public async Task TrackAsync(string taskId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(taskId)) throw new ArgumentException("The task id is required.", nameof(taskId));
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be positive and finite.");
        await _store
            .SaveAsync(new PushTrackedTask(taskId, _time.GetUtcNow() + timeout, timeout, null), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     True when <paramref name="taskId" /> is tracked. Set <see cref="PushNotificationReceiver.ExpectedTask" /> to
    ///     this method so the receiver refuses notifications for tasks this client did not create (spec §4.3.3).
    /// </summary>
    public async ValueTask<bool> IsTrackedAsync(string taskId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(taskId)) return false;
        return await _store.GetAsync(taskId, cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>
    ///     Reports a notification you received (the handler of <see cref="PushNotificationReceiver.HandleAsync" /> is the
    ///     place). Ignored when the task is not tracked.
    /// </summary>
    public Task NotifyAsync(PushDelivery delivery, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return NotifyAsync(delivery.Event, delivery.Sequence, cancellationToken);
    }

    /// <summary>
    ///     Reports a notification you received. A final state removes the task from tracking. A <paramref name="sequence" />
    ///     higher
    ///     than the last seen one plus one means a notification was lost, and the task is checked at once. Ignored when the
    ///     task is not tracked.
    /// </summary>
    public async Task NotifyAsync(StreamResponse evt, long? sequence = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var (taskId, state) = PushEvents.Inspect(evt);
        if (taskId is null) return;

        bool gap;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = await _store.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (entry is null) return;
            if (state is { } s && PushEvents.IsFinal(s))
            {
                await _store.RemoveAsync(taskId, cancellationToken).ConfigureAwait(false);
                return;
            }

            gap = sequence is { } seq && entry.LastSequence is { } last && seq > last + 1;
            if (sequence is { } n && (entry.LastSequence is null || n > entry.LastSequence))
                await _store.SaveAsync(entry.WithLastSequence(n), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (gap) await CheckAsync(taskId, PushCheckReason.SequenceGap, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks every task whose deadline has passed. The timer calls this; call it yourself when the timer is off.</summary>
    public async Task CheckDueAsync(CancellationToken cancellationToken = default)
    {
        var due = await _store.ListDueAsync(_time.GetUtcNow(), SweepBatch, cancellationToken).ConfigureAwait(false);
        foreach (var entry in due)
            await CheckAsync(entry.TaskId, PushCheckReason.DeadlinePassed, cancellationToken).ConfigureAwait(false);
    }

    private async Task SweepFromTimerAsync()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;
        try
        {
            await CheckDueAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            /* a store or client failure must not kill the timer; the next sweep tries again */
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
        }
    }

    private async Task CheckAsync(string taskId, PushCheckReason reason, CancellationToken cancellationToken)
    {
        lock (_checking)
        {
            if (!_checking.Add(taskId)) return; // a check of this task is already running
        }

        try
        {
            AgentTask? task = null;
            Exception? error = null;
            try
            {
                task = await _client
                    .GetTaskAsync(new GetTaskRequest { Id = taskId, HistoryLength = 0 }, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                error = e;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var entry = await _store.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
                if (entry is not null) // it may have been removed by a final notification meanwhile
                {
                    if (task is not null && PushEvents.IsFinal(task.Status.State))
                        await _store.RemoveAsync(taskId, cancellationToken).ConfigureAwait(false);
                    else
                        await _store.SaveAsync(entry.WithDeadline(_time.GetUtcNow() + entry.Timeout), cancellationToken)
                            .ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }

            try
            {
                await _onCheck(new PushTaskCheck(taskId, reason, task, error), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                /* the app's callback failed; tracking is already up to date */
            }
        }
        finally
        {
            lock (_checking)
            {
                _checking.Remove(taskId);
            }
        }
    }
}