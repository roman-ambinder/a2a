using System.Net;

namespace A2A.Client.Resilience;

/// <summary>
/// Retry rules for idempotent client calls (spec 7.8): at most <see cref="MaxAttempts"/> attempts, exponential backoff with
/// jitter, <c>Retry-After</c> honoured. Retries only on transport failure, HTTP 503/429 or an error the server explicitly
/// marked retryable (<c>metadata.retryable</c> = "true"). The server-side default (<see cref="A2AErrors.DefaultRetryable"/>) is not used.
/// </summary>
internal static class RetryPolicy
{
    public const int MaxAttempts = 3;

    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>A server asking for a longer wait than this is not retried: the error is surfaced instead.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    /// True when <paramref name="error"/> may be retried. <paramref name="retryAfter"/> is the wait the server asked for.
    /// </summary>
    public static bool ShouldRetry(Exception error, out TimeSpan? retryAfter)
    {
        retryAfter = null;
        switch (error)
        {
            case A2AException a2a:
                retryAfter = a2a.RetryAfter;
                if (retryAfter > MaxRetryAfter) return false;
                // Only what the server said: HTTP 503/429, or an explicit metadata.retryable = "true". Errors the client
                // synthesized from a bare HTTP status, or server errors without that metadata, are not retried.
                return a2a.HttpStatus is (int)HttpStatusCode.ServiceUnavailable or (int)HttpStatusCode.TooManyRequests
                    || (A2AErrors.TryGetRetryableFromDetails(a2a.Details, out var explicitRetryable) && explicitRetryable);
            case HttpRequestException:
                return true;
            default:
                return false;
        }
    }

    /// <summary>Wait before attempt <paramref name="nextAttempt"/> (2 or 3): the server's value, else 200 ms * 2^(n-2) +-20 %.</summary>
    public static TimeSpan Delay(int nextAttempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } fromServer) return fromServer < TimeSpan.Zero ? TimeSpan.Zero : fromServer;
        var backoff = BaseDelay * Math.Pow(2, nextAttempt - 2);
        var jitter = 1 + ((Random.Shared.NextDouble() * 2) - 1) * 0.2;
        return backoff * jitter;
    }
}
