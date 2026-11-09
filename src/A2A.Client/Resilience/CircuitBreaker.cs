using System.Diagnostics.Metrics;
using System.IO;
using System.Runtime.CompilerServices;
using A2A.Diagnostics;

namespace A2A.Client.Resilience;

internal enum CircuitState { Closed, Open, HalfOpen }

/// <summary>What a finished call says about the peer.</summary>
internal enum CircuitOutcome
{
    /// <summary>The peer answered: it is up.</summary>
    Healthy,

    /// <summary>The peer looks unhealthy: counts toward opening.</summary>
    Failure,

    /// <summary>Says nothing either way (a caller's cancellation, a bare server error, a local fault).</summary>
    Ignored,
}

/// <summary>
/// Stops calls to one peer after repeated failures. <b>Closed</b>: calls flow, and consecutive failures are counted.
/// <b>Open</b>: after the threshold, calls are refused with <see cref="CircuitOpenException"/> and no request is sent, until
/// the open duration has passed. <b>Half-open</b>: one call (the probe) is let through. If it succeeds the circuit closes; if
/// it fails the circuit opens again for a full duration. Other calls are refused while the probe runs.
/// </summary>
/// <remarks>
/// A call that began before the last state change is ignored when it ends: it says nothing about the current state.
/// A probe that hangs is replaced after one open duration. Time comes from <see cref="TimeProvider.GetUtcNow"/>.
/// </remarks>
internal sealed class CircuitBreaker
{
    internal const string StateChangesCounterName = "a2a.client.circuit.state_changes";
    internal const string StateTag = "a2a.circuit.state";
    private static readonly Meter Meter = new("A2A");
    private static readonly Counter<long> StateChanges = Meter.CreateCounter<long>(StateChangesCounterName, description: "Circuit breaker state changes of A2A clients.");

    private readonly object _gate = new();
    private readonly int _threshold;
    private readonly TimeSpan _openDuration;
    private readonly TimeProvider _time;
    private readonly string? _agentName;
    private CircuitState _state = CircuitState.Closed;
    private int _failures;
    private long _generation;
    private DateTimeOffset _nextProbeAt;

    public CircuitBreaker(CircuitBreakerOptions options, TimeProvider time, string? agentName)
    {
        ArgumentNullException.ThrowIfNull(options);
        _threshold = options.FailureThreshold;
        _openDuration = options.OpenDuration;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _agentName = agentName;
    }

    internal CircuitState State { get { lock (_gate) return _state; } }

    public async Task<T> RunAsync<T>(Func<Task<T>> call)
    {
        var generation = Enter();
        try
        {
            var result = await call().ConfigureAwait(false);
            Record(generation, CircuitOutcome.Healthy);
            return result;
        }
        catch (Exception ex)
        {
            Record(generation, Classify(ex));
            throw;
        }
    }

    /// <summary>A stream is judged by its opening: the first event (or a clean end) is Healthy; an error before it is classified. Later errors are not counted.</summary>
    public async IAsyncEnumerable<T> RunStreamAsync<T>(IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var generation = Enter();
        var judged = false;
        var items = source.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool more;
                try { more = await items.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception ex) when (!judged) { judged = true; Record(generation, Classify(ex)); throw; }
                if (!judged) { judged = true; Record(generation, CircuitOutcome.Healthy); }
                if (!more) yield break;
                yield return items.Current;
            }
        }
        finally { await items.DisposeAsync().ConfigureAwait(false); }
    }

    private long Enter()
    {
        var entered = false;
        long generation;
        lock (_gate)
        {
            if (_state != CircuitState.Closed)
            {
                var now = _time.GetUtcNow();
                if (now < _nextProbeAt) throw new CircuitOpenException(_agentName, _nextProbeAt - now);
                if (_state == CircuitState.Open) { MoveTo(CircuitState.HalfOpen); entered = true; }
                _nextProbeAt = now + _openDuration; // one probe at a time; a hung probe is replaced after OpenDuration
            }
            generation = _generation;
        }
        if (entered) Emit(CircuitState.HalfOpen);
        return generation;
    }

    private void Record(long generation, CircuitOutcome outcome)
    {
        CircuitState? changed = null;
        lock (_gate)
        {
            if (generation != _generation) return; // a call that began before the last state change says nothing about the current one
            switch (outcome)
            {
                case CircuitOutcome.Healthy:
                    if (_state == CircuitState.HalfOpen) changed = CircuitState.Closed; else _failures = 0;
                    break;
                case CircuitOutcome.Failure:
                    if (_state == CircuitState.HalfOpen || ++_failures >= _threshold) changed = CircuitState.Open;
                    break;
                case CircuitOutcome.Ignored:
                    if (_state == CircuitState.HalfOpen) _nextProbeAt = _time.GetUtcNow(); // the probe told nothing: let the next call try
                    break;
            }
            if (changed is { } next) { MoveTo(next); if (next == CircuitState.Open) _nextProbeAt = _time.GetUtcNow() + _openDuration; }
        }
        if (changed is { } state) Emit(state);
    }

    private void MoveTo(CircuitState next)
    {
        _state = next;
        _failures = 0;
        _generation++;
    }

    private void Emit(CircuitState state)
    {
        var name = state switch { CircuitState.Closed => "closed", CircuitState.Open => "open", _ => "half_open" };
        var stateTag = new KeyValuePair<string, object?>(StateTag, name);
        if (string.IsNullOrEmpty(_agentName)) StateChanges.Add(1, stateTag);
        else StateChanges.Add(1, new KeyValuePair<string, object?>(A2ATelemetry.Tags.AgentName, _agentName), stateTag);
    }

    internal static CircuitOutcome Classify(Exception error) => error switch
    {
        A2AException a2a => Classify(a2a),
        HttpRequestException or IOException or TimeoutException => CircuitOutcome.Failure,
        OperationCanceledException { InnerException: TimeoutException } => CircuitOutcome.Failure, // the call timeout; a caller's cancellation has no TimeoutException inside
        _ => CircuitOutcome.Ignored,
    };

    private static CircuitOutcome Classify(A2AException error)
    {
        // The peer answered. A 4xx, throttling or a task-level error says nothing about its health.
        if (error.HttpStatus is >= 400 and < 500 || error.Code is A2AErrorCode.RateLimited or A2AErrorCode.TaskNotFound or A2AErrorCode.TaskNotCancelable)
            return CircuitOutcome.Healthy;
        if (RetryPolicy.IsRetryable(error)) return CircuitOutcome.Failure;
        // Not retryable, so not counted; but not proof of health either, so a run of failures is not reset.
        return error.HttpStatus >= 500 || error.Code is A2AErrorCode.Internal or A2AErrorCode.InvalidAgentResponse
            ? CircuitOutcome.Ignored : CircuitOutcome.Healthy;
    }
}
