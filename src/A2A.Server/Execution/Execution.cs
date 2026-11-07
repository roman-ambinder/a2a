using System.Threading.Channels;
using A2A.Server.Streaming;

namespace A2A.Server.Execution;

/// <summary>
/// One run of an agent for a task: the executor writes to <see cref="Incoming"/>, the engine stores each event and passes it
/// on through <see cref="Outgoing"/> to the request that started the run.
/// </summary>
internal sealed class Execution(string taskId, CancellationTokenSource cts)
{
    private volatile bool _detached;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string TaskId { get; } = taskId;
    public CancellationTokenSource Cts { get; } = cts;
    public Channel<StreamResponse> Incoming { get; } = Channel.CreateUnbounded<StreamResponse>(new UnboundedChannelOptions { SingleReader = true });
    public Channel<SequencedEvent> Outgoing { get; } = Channel.CreateUnbounded<SequencedEvent>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
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

    public void Emit(SequencedEvent evt)
    {
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
