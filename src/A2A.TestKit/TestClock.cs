namespace A2A.TestKit;

/// <summary>
/// A <see cref="TimeProvider"/> whose time moves only when you call <see cref="Advance"/> or <see cref="SetUtcNow"/>.
/// Thread-safe. <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> and
/// <c>new CancellationTokenSource(delay, clock)</c> work with it.
/// </summary>
/// <remarks>
/// <see cref="Advance"/> fires the timers that fall due, in due order, and sets the time to each timer's due time while its
/// callback runs. Callbacks run on the thread that called <see cref="Advance"/>, outside the lock, so a callback may create
/// or change timers and read the time. A timer created or changed with a due time of zero is due at once: it fires on a
/// thread pool thread, like a timer of the real clock, so code that stores the timer after creating it keeps working.
/// An exception thrown by a callback does not stop the other timers; <see cref="Advance"/> throws it afterwards.
/// </remarks>
public sealed class TestClock : TimeProvider
{
    private const long MaxMilliseconds = 4294967294;

    private readonly object _gate = new();
    private readonly DateTimeOffset _start;
    private readonly List<ClockTimer> _timers = [];
    private DateTimeOffset _now;
    private DateTimeOffset _horizon; // the latest target any Advance asked for, so concurrent calls add up
    private long _sequence;

    /// <summary>Creates a clock at <paramref name="start"/>. Default: 2026-01-01T00:00:00Z.</summary>
    public TestClock(DateTimeOffset? start = null)
    {
        _start = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _now = _horizon = _start;
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        lock (_gate) return (_now - _start).Ticks;
    }

    /// <summary>Moves the time forward by <paramref name="by"/> and fires the timers that fall due on the way.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="by"/> is negative.</exception>
    /// <exception cref="AggregateException">One or more timer callbacks threw. All timers still fired.</exception>
    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(by), by, "The clock cannot go back.");
        DateTimeOffset target;
        lock (_gate)
        {
            target = _horizon + by;
            _horizon = target;
        }
        RunTo(target);
    }

    /// <summary>Moves the time forward to <paramref name="value"/> and fires the timers that fall due on the way.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is earlier than the current time.</exception>
    /// <exception cref="AggregateException">One or more timer callbacks threw. All timers still fired.</exception>
    public void SetUtcNow(DateTimeOffset value)
    {
        lock (_gate)
        {
            if (value < _horizon) throw new ArgumentOutOfRangeException(nameof(value), value, $"The clock cannot go back (now {_horizon:O}).");
            _horizon = value;
        }
        RunTo(value);
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        CheckInterval(dueTime, nameof(dueTime));
        CheckInterval(period, nameof(period));
        var timer = new ClockTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private static void CheckInterval(TimeSpan value, string name)
    {
        var ms = (long)value.TotalMilliseconds;
        if (value != Timeout.InfiniteTimeSpan && (ms < 0 || ms > MaxMilliseconds))
            throw new ArgumentOutOfRangeException(name, value, "The interval must be Timeout.InfiniteTimeSpan or between zero and 4294967294 milliseconds.");
    }

    private void RunTo(DateTimeOffset target)
    {
        List<Exception>? errors = null;
        while (true)
        {
            ClockTimer timer;
            lock (_gate)
            {
                var next = NextDue(target);
                if (next is null)
                {
                    if (target > _now) _now = target;
                    break;
                }
                timer = next;
                if (timer.Due!.Value > _now) _now = timer.Due.Value;
                if (timer.Period > TimeSpan.Zero)
                {
                    timer.Due += timer.Period;
                    timer.Order = ++_sequence;
                }
                else
                {
                    timer.Due = null;
                }
            }
            try
            {
                timer.Callback(timer.State);
            }
            catch (Exception e)
            {
                (errors ??= []).Add(e);
            }
        }
        if (errors is not null) throw new AggregateException("A timer callback threw while the clock advanced.", errors);
    }

    private ClockTimer? NextDue(DateTimeOffset target)
    {
        ClockTimer? best = null;
        foreach (var t in _timers)
        {
            if (t.Due is not { } due || due > target) continue;
            if (best is null || due < best.Due || (due == best.Due && t.Order < best.Order)) best = t;
        }
        return best;
    }

    private void FireDueNow()
    {
        DateTimeOffset now;
        lock (_gate) now = _now;
        RunTo(now);
    }

    private sealed class ClockTimer(TestClock clock, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public DateTimeOffset? Due; // guarded by the clock's gate
        public TimeSpan Period;
        public long Order;
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            CheckInterval(dueTime, nameof(dueTime));
            CheckInterval(period, nameof(period));
            var fireNow = false;
            lock (clock._gate)
            {
                if (_disposed) return false;
                if (!clock._timers.Contains(this)) clock._timers.Add(this);
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    Due = null;
                }
                else
                {
                    Due = clock._now + dueTime;
                    Order = ++clock._sequence;
                    fireNow = dueTime == TimeSpan.Zero;
                }
            }
            if (fireNow) ThreadPool.UnsafeQueueUserWorkItem(static c => c.FireDueNow(), clock, preferLocal: false);
            return true;
        }

        public void Dispose()
        {
            lock (clock._gate)
            {
                _disposed = true;
                Due = null;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
