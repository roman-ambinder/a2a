namespace A2A.Client.Resilience;

/// <summary>
/// Settings of the client circuit breaker (<see cref="A2AClientOptions.CircuitBreaker"/>). After
/// <see cref="FailureThreshold"/> failed calls in a row, the client stops sending requests to the peer for
/// <see cref="OpenDuration"/>, then lets one call through to test it.
/// </summary>
/// <remarks>
/// Only failures that suggest the peer is unhealthy count: a connection or timeout failure, and HTTP 503 or an error the
/// peer marked retryable. Calls are counted, not attempts: one call with all its retries counts once. A successful call
/// resets the count.
/// </remarks>
public sealed class CircuitBreakerOptions
{
    private int _failureThreshold = 5;
    private TimeSpan _openDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many consecutive failed calls open the circuit. Default 5. One call counts once, including all its retries.
    /// Must be 1 or more.
    /// </summary>
    public int FailureThreshold
    {
        get => _failureThreshold;
        set => _failureThreshold = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "FailureThreshold must be at least 1.");
    }

    /// <summary>How long the circuit stays open before one call may test the peer. Default 30 seconds. Must be greater than zero.</summary>
    public TimeSpan OpenDuration
    {
        get => _openDuration;
        set => _openDuration = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "OpenDuration must be greater than zero.");
    }
}
