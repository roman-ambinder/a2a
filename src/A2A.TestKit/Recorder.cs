using System.Diagnostics;

namespace A2A.TestKit;

/// <summary>A thread-safe list that wakes waiters when an item is added. Waiting is signal-driven; only the timeout uses real time.</summary>
internal sealed class Recorder<T>
{
    private readonly object _gate = new();
    private readonly List<T> _items = [];
    private TaskCompletionSource _changed = NewSignal();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Add(T item)
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            _items.Add(item);
            old = _changed;
            _changed = NewSignal();
        }
        old.TrySetResult();
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate) return _items.ToArray();
    }

    public void Clear()
    {
        lock (_gate) _items.Clear();
    }

    /// <summary>Waits until <paramref name="find"/> finds a result in the recorded items. Checks past and future items.</summary>
    public async Task<TResult> WaitAsync<TResult>(Func<IReadOnlyList<T>, (bool Found, TResult Value)> find, TimeSpan timeout,
        Func<IReadOnlyList<T>, string> describeTimeout, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var expired = false;
        while (true)
        {
            T[] snapshot;
            Task changed;
            lock (_gate)
            {
                snapshot = _items.ToArray();
                changed = _changed.Task;
            }
            var (found, value) = find(snapshot);
            if (found) return value;
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = timeout - watch.Elapsed;
            if (expired || remaining <= TimeSpan.Zero) throw new A2AAssertionException(describeTimeout(snapshot));
            try
            {
                await changed.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                expired = true; // look once more: an item may have arrived as the time ran out
            }
        }
    }
}
