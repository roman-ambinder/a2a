using System.Diagnostics;
using System.Threading.Channels;
using A2A.Diagnostics;
using A2A.Server.Idempotency;
using A2A.Server.Storage;
using A2A.Server.Streaming;

namespace A2A.Server.Execution;

/// <summary>
///     One run of an agent for a task: the executor writes to <see cref="Incoming" />, the engine stores each event and
///     passes it
///     on through <see cref="Outgoing" /> to the request that started the run. Both channels hold at most
///     <paramref name="capacity" /> events.
/// </summary>
internal sealed class Execution(TaskKey key, string id, bool isCancelRun, CancellationTokenSource cts, int capacity)
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile bool _cancelRequested;
    private volatile bool _detached;

    private volatile bool _failed;

    private long _lastSequence;

    private int _outcome; // 0 running, 1 the agent returned (or threw), 2 stopped by the engine (time limit or budget)
    private volatile bool _registered;
    private volatile bool _taskStored;

    /// <summary>The task, owner-scoped.</summary>
    public TaskKey Key { get; } = key;

    public string TaskId => Key.TaskId;

    /// <summary>This execution's id with the <see cref="Coordination.ExecutionCoordinator" />: new for every execution.</summary>
    public string Id { get; } = id;

    /// <summary>
    ///     True for the run of a CancelTask (<see cref="IAgentExecutor.CancelAsync" />). Cancel requests from other
    ///     instances never cancel it.
    /// </summary>
    public bool IsCancelRun { get; } = isCancelRun;

    public CancellationTokenSource Cts { get; } = cts;

    /// <summary>
    ///     True once something asked this execution to stop: <see cref="Cancel" /> (a CancelTask, a cancel from another
    ///     instance, the
    ///     time limit, a store failure). False when only the engine's shutdown fired <see cref="Cts" />: nobody cancelled the
    ///     task.
    /// </summary>
    public bool CancelRequested => _cancelRequested;

    /// <summary>True once the execution's lease is registered with the coordinator.</summary>
    public bool Registered
    {
        get => _registered;
        set => _registered = value;
    }

    /// <summary>
    ///     True from a lost (or lapsed) lease until the heartbeat that registers the execution again has read the task.
    ///     Set and cleared under <see cref="HeartbeatGate" />.
    /// </summary>
    public bool LeaseLost { get; set; }

    /// <summary>
    ///     True once the task is in the task store: from the start for an existing task, after the first stored event for a
    ///     new
    ///     one. Until then a missing task is expected and does not mean that the task was deleted.
    /// </summary>
    public bool TaskStored
    {
        get => _taskStored;
        set => _taskStored = value;
    }

    /// <summary>Held by a running heartbeat, and for good once the execution ends.</summary>
    public SemaphoreSlim HeartbeatGate { get; } = new(1, 1);

    /// <summary>The idempotency claim the heartbeat renews while it is unsettled (for example during a long direct reply).</summary>
    public IdempotencyClaim Claim { get; init; } = IdempotencyClaim.None;

    /// <summary>Whether the tenant's card had push notifications when the execution started; decides its push deliveries.</summary>
    public bool PushEnabled { get; init; }

    /// <summary>
    ///     A2A 1.1: artifacts the client sent with the message that creates the task, already checked and owned by the server.
    ///     They
    ///     are committed right after the task is created, before the agent's first event. Null when none.
    /// </summary>
    public IReadOnlyList<TaskArtifactUpdateEvent>? UserArtifacts { get; init; }

    /// <summary>A2A 1.1: the client input of this execution (<see cref="AgentRequestContext.LiveInput" />), or null.</summary>
    public LiveInputBuffer? LiveInput { get; init; }

    /// <summary>
    ///     The last task this execution measured (<c>TaskSize.Measure</c>) and its size. The next event is measured as this
    ///     size
    ///     plus what it adds when the stored task is that same instance (it is never changed), so the cap check does not walk
    ///     the
    ///     task again for every appended chunk. Used only by the loop that applies this execution's events.
    /// </summary>
    public AgentTask? MeasuredTask { get; set; }

    /// <summary>The size of <see cref="MeasuredTask" />.</summary>
    public long MeasuredBytes { get; set; }

    /// <summary>
    ///     The task record this run's last commit stored, or null. The next event is applied to it without reading the store
    ///     again. It may be stale (another writer committed since): the commit then fails its version check and the engine
    ///     reloads.
    ///     Used only by the loop that applies this execution's events.
    /// </summary>
    public TaskRecord? Committed { get; set; }

    /// <summary>
    ///     The task's usage as this run sees it, shared with the run's <see cref="TaskUpdater" />. Its unstored part
    ///     rides on the next stored event.
    /// </summary>
    public TaskUsageMeter Usage { get; init; } = new();

    /// <summary>The run's span (<see cref="A2ATelemetry.SpanNames.AgentExecution" />), or null when nobody listens.</summary>
    public Activity? Activity { get; init; }

    /// <summary>
    ///     The agent's events, read by the engine. When it is full the agent's next write waits: a fast agent is throttled to
    ///     the
    ///     engine's pace. Only the executor writes here, never under the task lock. The FAILED event of the time limit is
    ///     queued
    ///     here too, so it may wait behind up to <c>capacity</c> (<see cref="StreamingOptions.ChannelCapacity" />) events the
    ///     agent
    ///     wrote before it.
    /// </summary>
    public Channel<StreamResponse> Incoming { get; } = Channel.CreateBounded<StreamResponse>(
        new BoundedChannelOptions(Math.Max(1, capacity))
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true
        });

    /// <summary>
    ///     The stored events for the request that started the run. Written under the task lock, so a write never waits: when
    ///     the
    ///     reader is behind, the event is dropped (DropWrite). The first event (the task, its snapshot, or a direct reply) is
    ///     written
    ///     to an empty channel and always kept; every later event is stored before it is written, and the reader fills any gap
    ///     in
    ///     sequences from the event store.
    /// </summary>
    public Channel<SequencedEvent> Outgoing { get; } = Channel.CreateBounded<SequencedEvent>(
        new BoundedChannelOptions(Math.Max(1, capacity))
        {
            FullMode = BoundedChannelFullMode.DropWrite, SingleWriter = true, SingleReader = true
        });

    public Task Completion => _done.Task;

    /// <summary>
    ///     The highest sequence passed to <see cref="Emit" />, whether or not the bounded channel kept it. After a storage
    ///     failure the
    ///     initiating stream reads the events it missed from the store up to here, and never the FAILED event stored after
    ///     them.
    /// </summary>
    public long LastSequence => Volatile.Read(ref _lastSequence);

    /// <summary>True once the events of this execution could not be stored: the initiating request gets an error.</summary>
    public bool Failed => _failed;

    /// <summary>
    ///     Ends the run's span. A run that left the task FAILED gets an error status (unless a more specific error was
    ///     recorded,
    ///     for example the time limit).
    /// </summary>
    public void EndActivity()
    {
        if (Activity is not { } activity) return;
        if (activity.Status == ActivityStatusCode.Unset && activity.GetTagItem(A2ATelemetry.Tags.TaskState) is string
                                                            state
                                                        && string.Equals(state,
                                                            A2ATelemetry.TaskStateValue(TaskState.Failed),
                                                            StringComparison.Ordinal))
        {
            activity.SetTag(A2ATelemetry.Tags.ErrorType, "TaskFailed");
            activity.SetStatus(ActivityStatusCode.Error, "TaskFailed");
        }

        activity.Dispose();
    }

    /// <summary>
    ///     The agent returned or threw (<see cref="TryMarkReturned" />): the run takes no more input and only ends (its last
    ///     events are stored, its lease is ended).
    /// </summary>
    public bool Returned => Volatile.Read(ref _outcome) == 1;

    /// <summary>The agent returned or threw. False when the engine had already stopped the execution.</summary>
    public bool TryMarkReturned()
    {
        return Interlocked.CompareExchange(ref _outcome, 1, 0) != 2;
    }

    /// <summary>
    ///     The engine stops the run (the time limit expired or the budget was crossed). False when the agent had already
    ///     returned, or the run was stopped already.
    /// </summary>
    public bool TryMarkStopped()
    {
        return Interlocked.CompareExchange(ref _outcome, 2, 0) == 0;
    }

    /// <summary>
    ///     Cancels the execution. Returns the error of a cancellation callback that threw (the token is cancelled anyway), or
    ///     null.
    /// </summary>
    public Exception? Cancel()
    {
        _cancelRequested = true; // before the token fires, so a catch that sees the token also sees this
        try
        {
            Cts.Cancel();
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null; // already finished
        }
        catch (AggregateException ex)
        {
            return ex;
        }
    }

    public void MarkDone()
    {
        LiveInput?.Complete();
        Cts.Dispose();
        _done.TrySetResult();
    }

    public void Emit(SequencedEvent evt)
    {
        // Single writer (the engine's consumer, under the task lock), so a plain compare is enough.
        if (evt.Sequence > _lastSequence) Volatile.Write(ref _lastSequence, evt.Sequence);
        if (!_detached) Outgoing.Writer.TryWrite(evt);
    }

    /// <summary>Marks the storage failure and ends the outgoing events with <paramref name="error" />.</summary>
    public void Fail(Exception error)
    {
        _failed = true;
        Outgoing.Writer.TryComplete(error);
    }

    /// <summary>The initiating request stopped listening; drop further outgoing events.</summary>
    public void Detach()
    {
        _detached = true;
        while (Outgoing.Reader.TryRead(out _))
        {
        }
    }
}