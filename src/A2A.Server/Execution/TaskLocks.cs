using A2A.Server.Storage;

namespace A2A.Server.Execution;

/// <summary>
/// The per-task locks: 64 striped semaphores, picked by tenant and task id. Every change to a task's record and event log,
/// and every publish of its events, happens under its lock, so publish order is sequence order.
/// </summary>
internal sealed class TaskLocks
{
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>The semaphore of a task.</summary>
    public SemaphoreSlim For(string tenant, string taskId) => _locks[Stripe(tenant, taskId)];

    /// <summary>The index of the task's lock (tasks on the same stripe share a lock).</summary>
    public int Stripe(string tenant, string taskId) =>
        // No scope string is built: this runs for every event.
        (HashCode.Combine(StringComparer.Ordinal.GetHashCode(tenant), StringComparer.Ordinal.GetHashCode(taskId)) & 0x7fffffff) % _locks.Length;

    /// <summary>Waits for the task's lock; dispose the result to release it. Nothing is held when the wait is cancelled.</summary>
    public async ValueTask<Releaser> AcquireAsync(TaskKey key, CancellationToken cancellationToken)
    {
        var gate = For(key.Tenant, key.TaskId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    /// <summary>Releases a held task lock when disposed.</summary>
    /// <remarks>Use only in a using statement; do not copy — a copy disposed twice releases the semaphore twice.</remarks>
    public readonly struct Releaser(SemaphoreSlim gate) : IDisposable
    {
        /// <summary>Releases the lock.</summary>
        public void Dispose() => gate?.Release();
    }
}
