using A2A.Server.Audit;

namespace A2A.Tests.Audit;

/// <summary>Keeps every event it is given. Waits are signals, never delays.</summary>
public class RecordingAuditSink : A2AAuditSink
{
    private readonly object _gate = new();
    private readonly List<A2AAuditEvent> _events = [];
    private readonly List<(Func<IReadOnlyList<A2AAuditEvent>, bool> Until, TaskCompletionSource Done)> _waiters = [];

    /// <summary>When it returns true for an event, the write throws and the event is not kept.</summary>
    public Func<A2AAuditEvent, bool>? FailWhen { get; set; }

    /// <summary>Every write that was tried, kept or not.</summary>
    public int Attempts { get; private set; }

    public IReadOnlyList<A2AAuditEvent> Events
    {
        get { lock (_gate) return _events.ToList(); }
    }

    public IReadOnlyList<A2AAuditEvent> Of(A2AAuditAction action) => Events.Where(e => e.Action == action).ToList();

    public override ValueTask WriteAsync(A2AAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Attempts++;
            if (FailWhen?.Invoke(auditEvent) == true) throw new InvalidOperationException("the audit store is down");
            _events.Add(auditEvent);
            foreach (var w in _waiters.Where(w => w.Until(_events)).ToList())
            {
                _waiters.Remove(w);
                w.Done.TrySetResult();
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Completes when <paramref name="until"/> is true for the events kept so far (also when it is already).</summary>
    public Task WaitUntilAsync(Func<IReadOnlyList<A2AAuditEvent>, bool> until)
    {
        lock (_gate)
        {
            if (until(_events)) return Task.CompletedTask;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((until, done));
            return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}

/// <summary>Never finishes and ignores its token, like a sink whose database hangs.</summary>
public sealed class HangingAuditSink : A2AAuditSink
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Writes;

    public override async ValueTask WriteAsync(A2AAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Writes);
        Entered.TrySetResult();
        await Release.Task; // the token is ignored on purpose
    }
}
