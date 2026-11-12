using System.Net;

namespace A2A.Client.Resilience;

/// <summary>Which failures of a unary call may be retried.</summary>
public enum RetryMode
{
    /// <summary>Never retried.</summary>
    None,

    /// <summary>Read-only or naturally idempotent calls: any transport failure, HTTP 503/429, or explicit <c>retryable</c>.</summary>
    Idempotent,

    /// <summary>
    ///     Calls that can create state on a server that ignores <c>Idempotency-Key</c> (SendMessage): HTTP 503/429, explicit
    ///     <c>retryable</c>, and only those transport failures where the request provably never reached the server.
    /// </summary>
    UnsentOnly
}

/// <summary>
///     Retry rules for client calls (spec 7.8): at most <see cref="A2AClientOptions.MaxRetries" /> retries, exponential
///     backoff with
///     jitter, <c>Retry-After</c> honoured. Retries only on transport failure (see <see cref="RetryMode" />), HTTP 503/429
///     or an
///     error the server explicitly marked retryable (<c>metadata.retryable</c> = "true"). The server-side default
///     (<see cref="A2AErrors.DefaultRetryable" />) is not used.
/// </summary>
/// <remarks>
///     Public so that every transport retries by the same rules: the built-in HTTP transports, the gRPC transport (a
///     separate
///     package) and third-party <see cref="IA2ATransportFactory" /> transports.
/// </remarks>
public static class RetryPolicy
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    ///     Longest computed backoff between two attempts (a server's Retry-After is bounded by
    ///     <see cref="MaxRetryAfter" />).
    /// </summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    /// <summary>A server asking for a longer wait than this is not retried: the error is surfaced instead.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     True when <paramref name="error" /> may be retried under <paramref name="mode" />. <paramref name="retryAfter" />
    ///     is the
    ///     wait the server asked for.
    /// </summary>
    public static bool ShouldRetry(RetryMode mode, Exception error, out TimeSpan? retryAfter)
    {
        retryAfter = null;
        if (mode == RetryMode.None) return false;
        switch (error)
        {
            case A2AException a2a:
                retryAfter = a2a.RetryAfter;
                if (retryAfter > MaxRetryAfter) return false;
                // Only what the server said: HTTP 503/429, or an explicit metadata.retryable = "true". Errors the client
                // synthesized from a bare HTTP status, or server errors without that metadata, are not retried.
                return IsRetryable(a2a);
            case HttpRequestException transport:
                if (IsBlockedAddress(transport)) return false; // permanent: the SSRF guard refuses that target every time
                return mode == RetryMode.Idempotent || NeverReachedServer(transport);
            default:
                return false;
        }
    }

    /// <summary>
    ///     True for HTTP 503/429 or an explicit <c>metadata.retryable</c> = "true": the errors a server marks as worth
    ///     another try.
    /// </summary>
    internal static bool IsRetryable(A2AException error)
    {
        return error.HttpStatus is (int)HttpStatusCode.ServiceUnavailable or 429 /* Too Many Requests */
               || (A2AErrors.TryGetRetryableFromDetails(error.Details, out var explicitRetryable) && explicitRetryable);
    }

    /// <summary>
    ///     True when the SSRF guard refused the target, thrown directly or wrapped by the handler around its connect
    ///     callback.
    /// </summary>
    internal static bool IsBlockedAddress(HttpRequestException error) =>
#if NETSTANDARD2_0
        false; // the netstandard2.0 build has no SSRF guard
#else
        error is A2A.Security.Http.BlockedAddressException ||
        error.InnerException is A2A.Security.Http.BlockedAddressException;
#endif

    /// <summary>
    ///     True when the connection, name lookup, proxy tunnel or TLS handshake failed: no request byte reached the server, so
    ///     a
    ///     retry cannot duplicate work. Everything else (a reset mid-request or mid-response, an early end of the response,
    ///     unknown) may have reached it.
    /// </summary>
    internal static bool NeverReachedServer(HttpRequestException error) =>
#if NETSTANDARD2_0
        // netstandard2.0 has no HttpRequestError, so it cannot tell. Not retrying is the safe answer.
        false;
#else
        error.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
            or HttpRequestError.ProxyTunnelError or HttpRequestError.SecureConnectionError;
#endif

    /// <summary>
    ///     True when <paramref name="error" /> proves that no request reached the server: the first
    ///     <see cref="HttpRequestException" /> in its <see cref="Exception.InnerException" /> chain is one that
    ///     <see cref="NeverReachedServer(HttpRequestException)" /> accepts and the SSRF guard did not refuse. The chain is walked
    ///     because a transport may wrap the failure, as the gRPC transport does (an <see cref="A2AException" /> over the
    ///     <c>RpcException</c>, whose inner exception is the <see cref="HttpRequestException" />). A cancellation or timeout
    ///     is never such a proof. Used by interface fallback (<see cref="A2AClientOptions.InterfaceFallback" />).
    /// </summary>
    internal static bool ProvablyUnsent(Exception error)
    {
        if (error is OperationCanceledException) return false;
        var depth = 0;
        for (var e = error; e is not null && depth < 8; e = e.InnerException, depth++)
        {
            if (e is OperationCanceledException) return false;
            if (e is HttpRequestException transport) return !IsBlockedAddress(transport) && NeverReachedServer(transport);
        }

        return false;
    }

    /// <summary>
    ///     Wait before attempt <paramref name="nextAttempt" /> (2 or later): the server's value, else 200 ms * 2^(n-2) +-20 %,
    ///     at most 10 s.
    /// </summary>
    public static TimeSpan Delay(int nextAttempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } fromServer) return fromServer < TimeSpan.Zero ? TimeSpan.Zero : fromServer;
        var backoff = BaseDelay * Math.Pow(2, Math.Min(nextAttempt - 2, 16));
        if (backoff > MaxBackoff) backoff = MaxBackoff;
        var jitter = 1 + (Random.Shared.NextDouble() * 2 - 1) * 0.2;
        return backoff * jitter;
    }
}