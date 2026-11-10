using System.Runtime.CompilerServices;
using A2A.Server.Storage;

namespace A2A.Server.Coordination;

/// <summary>
///     A process-local <see cref="ExecutionCoordinator" />: leases and sweep leases live in this process only, and cancel
///     requests go nowhere (there is no other instance to tell).
///     Its clock is the <see cref="TimeProvider" /> it is given. The default; replace it (for example with the Redis
///     coordinator)
///     when several instances serve one agent.
/// </summary>
public sealed class InMemoryExecutionCoordinator : ExecutionCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<TaskKey, Dictionary<string, DateTimeOffset>> _leases = [];
    private readonly Dictionary<string, DateTimeOffset> _sweepLeases = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;

    /// <summary>
    ///     Creates the coordinator. Throws <see cref="InvalidOperationException" /> for invalid options (
    ///     <see cref="CoordinationOptions.Validate" />).
    /// </summary>
    public InMemoryExecutionCoordinator(CoordinationOptions? options = null, TimeProvider? timeProvider = null)
    {
        options ??= new CoordinationOptions();
        options.Validate();
        _ttl = options.LeaseTtl;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public override ValueTask RegisterAsync(TaskKey task, string executionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        lock (_gate)
        {
            if (!_leases.TryGetValue(task, out var executions))
                _leases[task] = executions = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            executions[executionId] = _time.GetUtcNow() + _ttl;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask<bool> TryRegisterExclusiveAsync(TaskKey task, string executionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (!_leases.TryGetValue(task, out var executions))
                _leases[task] = executions = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            else if (executions.Values.Any(expiry => expiry > now)) return ValueTask.FromResult(false);
            executions[executionId] = now + _ttl;
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public override ValueTask<bool> HeartbeatAsync(TaskKey task, string executionId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (!_leases.TryGetValue(task, out var executions) ||
                !executions.TryGetValue(executionId, out var expiry) || expiry <= now)
                return ValueTask.FromResult(false);
            executions[executionId] = now + _ttl;
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public override ValueTask UnregisterAsync(TaskKey task, string executionId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_leases.TryGetValue(task, out var executions) && executions.Remove(executionId) &&
                executions.Count == 0) _leases.Remove(task);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask<bool> IsRunningAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            return ValueTask.FromResult(_leases.TryGetValue(task, out var executions) &&
                                        executions.Values.Any(expiry => expiry > now));
        }
    }

    /// <summary>Does nothing: this process is the only instance, and the sender cancels its own executions directly.</summary>
    public override ValueTask RequestCancelAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>Yields nothing (there are no other instances) and ends when <paramref name="cancellationToken" /> fires.</summary>
    public override async IAsyncEnumerable<TaskKey> CancelRequestsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        yield break;
    }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<(TaskKey Task, string ExecutionId)>> ListExpiredAsync(int max,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            IReadOnlyList<(TaskKey, string)> expired = _leases
                .SelectMany(t =>
                    t.Value.Where(e => e.Value <= now).Select(e => (Task: t.Key, ExecutionId: e.Key, Expiry: e.Value)))
                .OrderBy(e => e.Expiry)
                .Take(max)
                .Select(e => (e.Task, e.ExecutionId))
                .ToList();
            return ValueTask.FromResult(expired);
        }
    }

    /// <inheritdoc />
    public override ValueTask<bool> TryAcquireSweepLeaseAsync(string name, TimeSpan duration,
        CancellationToken cancellationToken = default)
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