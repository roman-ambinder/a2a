using System.Globalization;

namespace A2A.Client.Resilience;

/// <summary>
/// The client refused a call because its circuit breaker is open (<see cref="A2AClientOptions.CircuitBreaker"/>). No request
/// was sent. <see cref="A2AException.RetryAfter"/> is the time until the client tries the peer again.
/// </summary>
public sealed class CircuitOpenException : A2AException
{
    internal CircuitOpenException(string? agentName, TimeSpan retryAfter)
        : base(A2AErrorCode.Internal, BuildMessage(agentName, retryAfter))
    {
        RetryAfter = retryAfter;
    }

    private static string BuildMessage(string? agentName, TimeSpan retryAfter)
    {
        var seconds = Math.Ceiling(Math.Max(0, retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return $"The circuit for {(string.IsNullOrEmpty(agentName) ? "the agent" : $"agent '{agentName}'")} is open because of repeated failures. "
            + $"No request was sent. The next try is in {seconds} s.";
    }
}
