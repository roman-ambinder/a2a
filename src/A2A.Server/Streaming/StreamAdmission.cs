using A2A.Server.Storage;

namespace A2A.Server.Streaming;

/// <summary>
/// Counts open streams per task and per caller (across all tenants) and refuses a stream beyond <see cref="A2ALimits.MaxConcurrentStreamsPerTask"/>
/// or <see cref="A2ALimits.MaxConcurrentStreamsPerPrincipal"/> with <c>RATE_LIMITED</c>. Process-local.
/// </summary>
internal sealed class StreamAdmission(A2ALimits limits)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _perTask = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _perPrincipal = new(StringComparer.Ordinal);

    /// <summary>
    /// Takes a slot for a stream of <paramref name="task"/>. Dispose the result to give it back. The task counter is keyed
    /// by the owner too, so another caller's streams never affect (or reveal) this caller's task. The per-caller counter is
    /// keyed by <paramref name="callerPartition"/> (<see cref="ServerCallContext.LimitPartition"/>) alone, not by tenant,
    /// so a caller cannot raise the cap by using more tenant names: anonymous callers count per remote address.
    /// </summary>
    /// <exception cref="A2AException">RATE_LIMITED when a limit is reached.</exception>
    public IDisposable Enter(TaskKey task, string callerPartition)
    {
        var principal = callerPartition;
        var taskKey = $"{task.Tenant}\u001f{task.Owner}\u001f{task.TaskId}";
        lock (_gate)
        {
            if (Count(_perTask, taskKey) >= limits.MaxConcurrentStreamsPerTask || Count(_perPrincipal, principal) >= limits.MaxConcurrentStreamsPerPrincipal)
                throw A2AException.RateLimited(TimeSpan.FromSeconds(1));
            _perTask[taskKey] = Count(_perTask, taskKey) + 1;
            _perPrincipal[principal] = Count(_perPrincipal, principal) + 1;
        }
        return new Lease(this, taskKey, principal);
    }

    /// <summary>Open streams in total. For tests.</summary>
    internal int OpenStreams
    {
        get { lock (_gate) return _perPrincipal.Values.Sum(); }
    }

    private static int Count(Dictionary<string, int> counts, string key) => counts.TryGetValue(key, out var n) ? n : 0;

    private void Leave(string taskKey, string principal)
    {
        lock (_gate)
        {
            Decrement(_perTask, taskKey);
            Decrement(_perPrincipal, principal);
        }
    }

    private static void Decrement(Dictionary<string, int> counts, string key)
    {
        var n = Count(counts, key) - 1;
        if (n <= 0) counts.Remove(key);
        else counts[key] = n;
    }

    private sealed class Lease(StreamAdmission owner, string taskKey, string principal) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Leave(taskKey, principal);
        }
    }
}
