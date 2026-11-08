using System.Diagnostics;
using System.Threading.Channels;
using A2A.Diagnostics;
using A2A.Server.Streaming;

namespace A2A.Server.Execution;

/// <summary>
/// One run of an agent for a task: the executor writes to <see cref="Incoming"/>, the engine stores each event and passes it
/// on through <see cref="Outgoing"/> to the request that started the run. Both channels hold at most
/// <paramref name="capacity"/> events.
/// </summary>
internal sealed class Execution(string taskId, CancellationTokenSource cts, int capacity)
{
    private volatile bool _detached;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string TaskId { get; } = taskId;
    public CancellationTokenSource Cts { get; } = cts;

    /// <summary>The run's span (<see cref="A2ATelemetry.SpanNames.AgentExecution"/>), or null when nobody listens.</summary>
    public Activity? Activity { get; init; }

    /// <summary>
    /// Ends the run's span. A run that left the task FAILED gets an error status (unless a more specific error was recorded,
    /// for example the time limit).
    /// </summary>
    public void EndActivity()
    {
        if (Activity is not { } activity) return;
        if (activity.Status == ActivityStatusCode.Unset && activity.GetTagItem(A2ATelemetry.Tags.TaskState) is string state
            && string.Equals(state, nameof(TaskState.Failed), StringComparison.Ordinal))
        {
            activity.SetTag(A2ATelemetry.Tags.ErrorType, "TaskFailed");
            activity.SetStatus(ActivityStatusCode.Error, "TaskFailed");
        }
        activity.Dispose();
    }

    private int _outcome; // 0 running, 1 the agent returned (or threw), 2 timed out

    /// <summary>The agent returned or threw. False when the execution had already timed out.</summary>
    public bool TryMarkReturned() => Interlocked.CompareExchange(ref _outcome, 1, 0) != 2;

    /// <summary>The time limit expired. False when the agent had already returned.</summary>
    public bool TryMarkTimedOut() => Interlocked.CompareExchange(ref _outcome, 2, 0) == 0;

    /// <summary>
    /// The agent's events, read by the engine. When it is full the agent's next write waits: a fast agent is throttled to the
    /// engine's pace. Only the executor writes here, never under the task lock.
    /// </summary>
    public Channel<StreamResponse> Incoming { get; } = Channel.CreateBounded<StreamResponse>(new BoundedChannelOptions(Math.Max(1, capacity))
    {
        FullMode = BoundedChannelFullMode.Wait, SingleReader = true,
    });

    /// <summary>
    /// The stored events for the request that started the run. Written under the task lock, so a write never waits: when the
    /// reader is behind, the event is dropped (DropWrite). The first event (the task, its snapshot, or a direct reply) is written
    /// to an empty channel and always kept; every later event is stored before it is written, and the reader fills any gap in
    /// sequences from the event store.
    /// </summary>
    public Channel<SequencedEvent> Outgoing { get; } = Channel.CreateBounded<SequencedEvent>(new BoundedChannelOptions(Math.Max(1, capacity))
    {
        FullMode = BoundedChannelFullMode.DropWrite, SingleWriter = true, SingleReader = true,
    });
    public Task Completion => _done.Task;

    public void Cancel()
    {
        try { Cts.Cancel(); }
        catch (ObjectDisposedException) { /* already finished */ }
    }

    public void MarkDone()
    {
        Cts.Dispose();
        _done.TrySetResult();
    }

    private long _lastSequence;

    /// <summary>
    /// The highest sequence passed to <see cref="Emit"/>, whether or not the bounded channel kept it. After a storage failure the
    /// initiating stream reads the events it missed from the store up to here, and never the FAILED event stored after them.
    /// </summary>
    public long LastSequence => Volatile.Read(ref _lastSequence);

    public void Emit(SequencedEvent evt)
    {
        // Single writer (the engine's consumer, under the task lock), so a plain compare is enough.
        if (evt.Sequence > _lastSequence) Volatile.Write(ref _lastSequence, evt.Sequence);
        if (!_detached) Outgoing.Writer.TryWrite(evt);
    }

    /// <summary>True once the events of this execution could not be stored: the initiating request gets an error.</summary>
    public bool Failed => _failed;

    private volatile bool _failed;

    /// <summary>Marks the storage failure and ends the outgoing events with <paramref name="error"/>.</summary>
    public void Fail(Exception error)
    {
        _failed = true;
        Outgoing.Writer.TryComplete(error);
    }

    /// <summary>The initiating request stopped listening; drop further outgoing events.</summary>
    public void Detach()
    {
        _detached = true;
        while (Outgoing.Reader.TryRead(out _)) { }
    }
}
