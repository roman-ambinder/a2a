namespace A2A.Tests;

/// <summary>Manually advanced clock whose timers fire when <see cref="Advance"/> passes their due time.</summary>
public sealed class FakeTimeProvider(DateTimeOffset? start = null) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = start ?? new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

    // Timestamps follow the manual time too (the base class would read Stopwatch), so elapsed-time code runs on this clock.
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() { lock (_gate) return _now.UtcTicks; }

    /// <summary>Timers that are set to fire (created or changed, and not yet fired, stopped or disposed).</summary>
    public int PendingTimers
    {
        get { lock (_gate) return _timers.Count(t => t.Due is not null); }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<FakeTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.Due is { } d && d <= _now).ToList();
            foreach (var t in due) t.Fired(_now);
        }
        foreach (var t in due) t.Invoke();
    }

    private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; private set; }
        private TimeSpan _period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                if (Due is not null && !owner._timers.Contains(this)) owner._timers.Add(this);
                return true;
            }
        }

        public void Fired(DateTimeOffset now) =>
            Due = _period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan ? now + _period : null;

        public void Invoke() => callback(state);

        public void Dispose()
        {
            lock (owner._gate) { owner._timers.Remove(this); Due = null; }
        }

        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
