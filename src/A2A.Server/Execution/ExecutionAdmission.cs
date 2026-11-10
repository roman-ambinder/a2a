using A2A.Server.Telemetry;

namespace A2A.Server.Execution;

/// <summary>
///     Limits the agents that run at once on this instance (<see cref="A2ALimits.MaxConcurrentExecutions" /> and
///     <see cref="A2ALimits.MaxConcurrentExecutionsPerTenant" />) and holds a bounded FIFO queue of starts that wait for a
///     slot
///     (<see cref="A2ALimits.MaxQueuedExecutions" />, <see cref="A2ALimits.MaxExecutionQueueWait" />). Process-local, one
///     lock.
/// </summary>
/// <remarks>
///     After every change a pump grants every waiter that can run, in order, so no waiter is ever eligible while it waits.
///     A
///     waiter blocked only by its tenant's limit does not block another tenant's waiter. Limits are read live from the
///     options.
/// </remarks>
internal sealed class ExecutionAdmission(A2ALimits limits, TimeProvider time)
{
    /// <summary>The reason of a refusal: the queue is full (or there is no queue).</summary>
    public const string QueueFull = "queue_full";

    /// <summary>The reason of a refusal: the wait for a slot took too long.</summary>
    public const string QueueTimeout = "queue_timeout";

    /// <summary>The reason of a refusal: the engine is shutting down.</summary>
    public const string Shutdown = "shutdown";

    /// <summary>
    ///     Key in <see cref="Exception.Data" /> of a refusal: its reason (<see cref="QueueFull" />,
    ///     <see cref="QueueTimeout" /> or <see cref="Shutdown" />). Not sent to the client.
    /// </summary>
    public const string ReasonDataKey = "a2a.rejection.reason";

    /// <summary>The <c>reason</c> metadata every refusal carries.</summary>
    public const string CapacityReason = "execution_capacity";

    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _queue = new();
    private readonly Dictionary<string, int> _queuedPerTenant = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _runningPerTenant = new(StringComparer.Ordinal);
    private readonly List<(Func<int, int, bool> Condition, TaskCompletionSource Signal)> _watchers = [];
    private bool _closed;
    private int _running;

    /// <summary>Slots in use. For tests.</summary>
    internal int Running
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>Starts waiting. For tests.</summary>
    internal int Queued
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>Tenants with a running or queued start. For tests: zero when everything is released.</summary>
    internal int TrackedTenants
    {
        get
        {
            lock (_gate)
            {
                return _runningPerTenant.Count + _queuedPerTenant.Count;
            }
        }
    }

    /// <summary>
    ///     Completes when <paramref name="condition" /> (running, queued) holds: at once when it holds now, else after the
    ///     change that
    ///     makes it hold. For tests, so they wait on a signal instead of a delay.
    /// </summary>
    internal Task WhenAsync(Func<int, int, bool> condition)
    {
        lock (_gate)
        {
            if (condition(_running, _queue.Count)) return Task.CompletedTask;
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _watchers.Add((condition, signal));
            return signal.Task;
        }
    }

    // Under the lock: the watchers whose condition holds now. Complete them after the lock is released.
    private List<TaskCompletionSource>? Satisfied()
    {
        if (_watchers.Count == 0) return null;
        List<TaskCompletionSource>? done = null;
        for (var i = _watchers.Count - 1; i >= 0; i--)
        {
            if (!_watchers[i].Condition(_running, _queue.Count)) continue;
            (done ??= []).Add(_watchers[i].Signal);
            _watchers.RemoveAt(i);
        }

        return done;
    }

    private static void Signal(List<TaskCompletionSource>? done)
    {
        if (done is null) return;
        foreach (var signal in done) signal.TrySetResult();
    }

    /// <summary>
    ///     Takes a slot for <paramref name="tenant" />, waiting in the queue when there is none free. Dispose the result to
    ///     give the
    ///     slot back. A start with a free slot never waits and creates no timer. A cancel that loses to a grant still returns
    ///     the
    ///     slot: the caller owns it and disposes it.
    /// </summary>
    /// <param name="tenant">The tenant the agent runs for.</param>
    /// <param name="whileQueued">
    ///     Called once when the start has to queue, after it was queued. The result is disposed when the wait ends. The engine
    ///     uses
    ///     it to renew the idempotency claim while the request waits.
    /// </param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <exception cref="A2AException">
    ///     RATE_LIMITED with <c>reason = execution_capacity</c>: the queue is full, the wait timed
    ///     out, or the engine stopped.
    /// </exception>
    public ValueTask<ExecutionSlot> EnterAsync(string tenant, Func<IDisposable?>? whileQueued,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waiter? waiter = null;
        string? refusal = null;
        TimeSpan wait = default;
        ExecutionSlot? granted = null;
        List<(Waiter, ExecutionSlot)>? pumped;
        List<TaskCompletionSource>? signals;
        lock (_gate)
        {
            pumped = Pump(); // limits may have been raised: earlier waiters go first
            if (CanRun(tenant))
            {
                granted = Take(tenant);
            }
            else if (_closed)
            {
                refusal = Shutdown;
            }
            else if (!QueueEnabled(out wait) || _queue.Count >= limits.MaxQueuedExecutions
                                             || (limits.MaxConcurrentExecutionsPerTenant > 0 &&
                                                 Count(_queuedPerTenant, tenant) >=
                                                 limits.MaxConcurrentExecutionsPerTenant))
            {
                refusal = QueueFull;
            }
            else
            {
                waiter = new Waiter(tenant);
                waiter.Node = _queue.AddLast(waiter);
                _queuedPerTenant[tenant] = Count(_queuedPerTenant, tenant) + 1;
                ServerTelemetry.ExecutionsQueued.Add(1);
            }

            signals = Satisfied();
        }

        Signal(signals);
        Complete(pumped);
        if (granted is not null) return new ValueTask<ExecutionSlot>(granted);
        if (refusal is not null) throw Refuse(refusal);
        return WaitAsync(waiter!, wait, whileQueued, cancellationToken);
    }

    private async ValueTask<ExecutionSlot> WaitAsync(Waiter waiter, TimeSpan wait, Func<IDisposable?>? whileQueued,
        CancellationToken cancellationToken)
    {
        ITimer? timeout = null;
        IDisposable? renewal = null;
        CancellationTokenRegistration registration = default;
        try
        {
            timeout = time.CreateTimer(static state =>
                {
                    var (admission, w) = ((ExecutionAdmission, Waiter))state!;
                    admission.Abandon(w, true);
                },
                (this, waiter), wait, Timeout.InfiniteTimeSpan);
            registration = cancellationToken.Register(static state =>
                {
                    var (admission, w, token) = ((ExecutionAdmission, Waiter, CancellationToken))state!;
                    admission.Abandon(w, false, token: token);
                },
                (this, waiter, cancellationToken));
            if (whileQueued is not null)
                try
                {
                    renewal = whileQueued();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Abandon(waiter, false, ex, cancellationToken);
                }

            return await waiter.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            if (timeout is not null) await timeout.DisposeAsync().ConfigureAwait(false);
            renewal?.Dispose();
        }
    }

    /// <summary>
    ///     Ends a wait that is still queued, under the lock (a grant that came first wins and nothing happens), and fails its
    ///     completion outside the lock.
    /// </summary>
    private void Abandon(Waiter waiter, bool timedOut, Exception? error = null, CancellationToken token = default)
    {
        var removed = false;
        List<TaskCompletionSource>? signals = null;
        lock (_gate)
        {
            if (waiter.Node is { } node)
            {
                _queue.Remove(node);
                waiter.Node = null;
                Decrement(_queuedPerTenant, waiter.Tenant);
                ServerTelemetry.ExecutionsQueued.Add(-1);
                removed = true;
                signals = Satisfied();
            }
        }

        if (!removed) return;
        Signal(signals);
        if (timedOut) waiter.Completion.TrySetException(Refuse(QueueTimeout));
        else if (error is not null) waiter.Completion.TrySetException(error);
        else waiter.Completion.TrySetCanceled(token);
        // No pump: a waiter that leaves frees queue room only, never a slot.
    }

    /// <summary>
    ///     Refuses every waiter (<see cref="Shutdown" />) and turns the queue off. A start with a free slot still proceeds.
    ///     Safe to call more than once.
    /// </summary>
    public void Close()
    {
        List<Waiter> waiters;
        List<TaskCompletionSource>? signals;
        lock (_gate)
        {
            _closed = true;
            waiters = new List<Waiter>(_queue.Count);
            foreach (var w in _queue)
            {
                w.Node = null;
                waiters.Add(w);
                ServerTelemetry.ExecutionsQueued.Add(-1);
            }

            _queue.Clear();
            _queuedPerTenant.Clear();
            signals = Satisfied();
        }

        Signal(signals);
        foreach (var w in waiters) w.Completion.TrySetException(Refuse(Shutdown));
    }

    private bool QueueEnabled(out TimeSpan wait)
    {
        wait = limits.MaxExecutionQueueWait;
        return !_closed && limits.MaxQueuedExecutions > 0 && TimeLimits.IsTimeLimit(wait);
    }

    private bool CanRun(string tenant)
    {
        return (limits.MaxConcurrentExecutions <= 0 || _running < limits.MaxConcurrentExecutions)
               && (limits.MaxConcurrentExecutionsPerTenant <= 0 ||
                   Count(_runningPerTenant, tenant) < limits.MaxConcurrentExecutionsPerTenant);
    }

    // Under the lock.
    private ExecutionSlot Take(string tenant)
    {
        _running++;
        _runningPerTenant[tenant] = Count(_runningPerTenant, tenant) + 1;
        ServerTelemetry.ExecutionsActive.Add(1);
        return new ExecutionSlot(this, tenant);
    }

    internal void Release(string tenant)
    {
        List<(Waiter, ExecutionSlot)>? granted;
        List<TaskCompletionSource>? signals;
        lock (_gate)
        {
            _running--;
            Decrement(_runningPerTenant, tenant);
            ServerTelemetry.ExecutionsActive.Add(-1);
            granted = Pump();
            signals = Satisfied();
        }

        Signal(signals);
        Complete(granted);
    }

    // Under the lock: grants every waiter that can run now, in queue order. A waiter blocked by its tenant is skipped.
    private List<(Waiter, ExecutionSlot)>? Pump()
    {
        List<(Waiter, ExecutionSlot)>? granted = null;
        var node = _queue.First;
        while (node is not null)
        {
            if (limits.MaxConcurrentExecutions > 0 && _running >= limits.MaxConcurrentExecutions) break;
            var next = node.Next;
            var waiter = node.Value;
            if (CanRun(waiter.Tenant))
            {
                _queue.Remove(node);
                waiter.Node = null;
                Decrement(_queuedPerTenant, waiter.Tenant);
                ServerTelemetry.ExecutionsQueued.Add(-1);
                (granted ??= []).Add((waiter, Take(waiter.Tenant)));
            }

            node = next;
        }

        return granted;
    }

    // Outside the lock. A waiter that was completed already (it cannot be, it left the queue under the lock) would leak its slot.
    private static void Complete(List<(Waiter, ExecutionSlot)>? granted)
    {
        if (granted is null) return;
        foreach (var (waiter, slot) in granted)
            if (!waiter.Completion.TrySetResult(slot))
                slot.Dispose();
    }

    private A2AException Refuse(string reason)
    {
        ServerTelemetry.ExecutionsRejected.Add(1,
            new KeyValuePair<string, object?>(ServerTelemetry.RejectionReasonTag, reason));
        var error = new A2AException(A2AErrorCode.RateLimited,
            "The server is at its limit of running agents. Try again later.")
        {
            HttpStatus = 429,
            Retryable = true,
            RetryAfter = RetryAfter(),
            Metadata = new Dictionary<string, string> { ["reason"] = CapacityReason }
        };
        error.Data[ReasonDataKey] = reason;
        return error;
    }

    /// <summary>The wait of a queued start, clamped to 1 to 30 seconds; 1 second when there is no queue.</summary>
    private TimeSpan RetryAfter()
    {
        var wait = limits.MaxExecutionQueueWait;
        if (limits.MaxQueuedExecutions <= 0 || !TimeLimits.IsTimeLimit(wait)) return TimeSpan.FromSeconds(1);
        return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) :
            wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait;
    }

    private static int Count(Dictionary<string, int> counts, string key)
    {
        return counts.TryGetValue(key, out var n) ? n : 0;
    }

    private static void Decrement(Dictionary<string, int> counts, string key)
    {
        var n = Count(counts, key) - 1;
        if (n <= 0) counts.Remove(key);
        else counts[key] = n;
    }

    private sealed class Waiter(string tenant)
    {
        public string Tenant { get; } = tenant;
        public LinkedListNode<Waiter>? Node { get; set; }

        public TaskCompletionSource<ExecutionSlot> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>A slot held by a running agent. Dispose gives it back, once.</summary>
internal sealed class ExecutionSlot(ExecutionAdmission owner, string tenant) : IDisposable
{
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(tenant);
    }
}