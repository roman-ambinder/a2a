using System.Runtime.CompilerServices;
using System.Threading.Channels;
using A2A.Server.Storage;

namespace A2A.Server.Coordination;

/// <summary>
/// A process-local <see cref="ExecutionCoordinator"/>: leases, cancel requests and sweep leases live in this process only.
/// Its clock is the <see cref="TimeProvider"/> it is given. The default; replace it (for example with the Redis coordinator)
/// when several instances serve one agent.
/// </summary>
public sealed class InMemoryExecutionCoordinator : ExecutionCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<TaskKey, Dictionary<string, DateTimeOffset>> _leases = [];
    private readonly Dictionary<string, DateTimeOffset> _sweepLeases = new(StringComparer.Ordinal);
    private readonly List<Channel<TaskKey>> _cancelReaders = [];
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;

    /// <summary>Creates the coordinator. Throws <see cref="InvalidOperationException"/> for invalid options (<see cref="CoordinationOptions.Validate"/>).</summary>
    public InMemoryExecutionCoordinator(CoordinationOptions? options = null, TimeProvider? timeProvider = null)
    {
        options ??= new CoordinationOptions();
        options.Validate();
        _ttl = options.LeaseTtl;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public override ValueTask RegisterAsync(TaskKey task, string executionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        lock (_gate)
        {
            if (!_leases.TryGetValue(task, out var executions)) _leases[task] = executions = new(StringComparer.Ordinal);
            executions[executionId] = _time.GetUtcNow() + _ttl;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask HeartbeatAsync(TaskKey task, string executionId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_leases.TryGetValue(task, out var executions) && executions.TryGetValue(executionId, out var expiry) && expiry > now)
                executions[executionId] = now + _ttl;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask UnregisterAsync(TaskKey task, string executionId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_leases.TryGetValue(task, out var executions) && executions.Remove(executionId) && executions.Count == 0) _leases.Remove(task);
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask<bool> IsRunningAsync(TaskKey task, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            return ValueTask.FromResult(_leases.TryGetValue(task, out var executions) && executions.Values.Any(expiry => expiry > now));
        }
    }

    /// <inheritdoc />
    public override ValueTask RequestCancelAsync(TaskKey task, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var reader in _cancelReaders) reader.Writer.TryWrite(task);
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<TaskKey> CancelRequestsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<TaskKey>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate) _cancelReaders.Add(channel);
        try
        {
            await foreach (var task in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return task;
        }
        finally
        {
            lock (_gate) _cancelReaders.Remove(channel);
        }
    }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<(TaskKey Task, string ExecutionId)>> ListExpiredAsync(int max, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            IReadOnlyList<(TaskKey, string)> expired = _leases
                .SelectMany(t => t.Value.Where(e => e.Value <= now).Select(e => (Task: t.Key, ExecutionId: e.Key, Expiry: e.Value)))
                .OrderBy(e => e.Expiry)
                .Take(max)
                .Select(e => (e.Task, e.ExecutionId))
                .ToList();
            return ValueTask.FromResult(expired);
        }
    }

    /// <inheritdoc />
    public override ValueTask<bool> TryAcquireSweepLeaseAsync(string name, TimeSpan duration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_sweepLeases.TryGetValue(name, out var until) && until > now) return ValueTask.FromResult(false);
            _sweepLeases[name] = now + duration;
            return ValueTask.FromResult(true);
        }
    }
}
