namespace A2A.Client.Sse;

/// <summary>
///     The keep-alive timeout of one SSE stream (<see cref="A2AClientOptions.StreamKeepAliveTimeout" />): one timer and one
///     linked token source for the life of the stream. <see cref="Arm" /> before a read starts the countdown,
///     <see cref="Disarm" /> after it stops it, so only time spent waiting for bytes counts, and a read is two cheap
///     timer changes, with no allocation.
/// </summary>
internal sealed class StreamIdleTimer : IDisposable
{
    private readonly CancellationToken _caller;
    private readonly object _gate = new();
    private readonly ITimer _timer;
    private bool _armed;
    private CancellationTokenSource _cts;
    private bool _fired;

    public StreamIdleTimer(TimeSpan timeout, TimeProvider timeProvider, CancellationToken caller)
    {
        Timeout = timeout;
        _caller = caller;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(caller);
        _timer = timeProvider.CreateTimer(static s => ((StreamIdleTimer)s!).Fire(), this,
            System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
    }

    public TimeSpan Timeout { get; }

    /// <summary>True when the timer cancelled the read and the caller did not cancel: the stream went silent.</summary>
    public bool Expired
    {
        get
        {
            lock (_gate) return _fired && !_caller.IsCancellationRequested;
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
        {
            _armed = false;
            _cts.Dispose();
        }
    }

    /// <summary>Starts the countdown. The token is the caller's, also cancelled when the countdown ends.</summary>
    public CancellationToken Arm()
    {
        CancellationToken token;
        lock (_gate)
        {
            _armed = true;
            token = _cts.Token;
        }

        _timer.Change(Timeout, System.Threading.Timeout.InfiniteTimeSpan);
        return token;
    }

    /// <summary>
    ///     Stops the countdown. When the timer fired just as the read completed with bytes, the spent token source is
    ///     replaced, so the next read does not start cancelled.
    /// </summary>
    public void Disarm()
    {
        _timer.Change(System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
        lock (_gate)
        {
            _armed = false;
            if (!_fired || _caller.IsCancellationRequested) return;
            _cts.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(_caller);
            _fired = false;
        }
    }

    /// <summary>The error of a silent stream: a dropped connection, which the resumable calls reconnect.</summary>
    public IOException Error(Exception? cause)
    {
        return new IOException(
            $"The stream received nothing for {Timeout.TotalSeconds:0.###} s after the server's keep-alives " +
            "(A2AClientOptions.StreamKeepAliveTimeout): the connection is treated as dropped.",
            new TimeoutException(null, cause));
    }

    private void Fire()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (!_armed) return; // the read completed first
            _armed = false;
            _fired = true;
            cts = _cts;
        }

        try
        {
            cts.Cancel(); // outside the lock: cancellation runs the read's callbacks
        }
        catch (ObjectDisposedException)
        {
            // The stream ended in the meantime.
        }
    }
}
