using A2A.Client.Resilience;

namespace A2A.Client.Outbox;

/// <summary>What the dispatcher does with an entry after a send failed.</summary>
internal enum ClientOutboxFailureKind
{
    /// <summary>Try again. This attempt did not create work on the peer.</summary>
    Retry,

    /// <summary>Try again. This attempt may have created work on the peer.</summary>
    RetryInDoubt,

    /// <summary>The peer refused the message for good. The entry fails.</summary>
    Fail
}

/// <summary>Sorts the exception of a send into what happens next. A pure function, so its table can be tested alone.</summary>
internal static class ClientOutboxFailures
{
    /// <summary>
    ///     Sorts <paramref name="error" />. <paramref name="retryAfter" /> is the peer's <c>Retry-After</c>, when it gave one.
    ///     A cancelled dispatcher is not an error: callers check that before they call this.
    /// </summary>
    public static ClientOutboxFailureKind Classify(Exception error, out TimeSpan? retryAfter)
    {
        retryAfter = null;
        switch (error)
        {
            case A2AException a2a:
                retryAfter = a2a.RetryAfter;
                // JSON-RPC errors arrive with HTTP status 200, so a missing or 2xx status says nothing: use the status of the error code.
                var status = a2a.HttpStatus is { } h and not (>= 200 and <= 299) ? h : A2AErrors.HttpStatus(a2a.Code);
                // Only what the peer said makes an error retryable, as for RetryPolicy: not the default of the code
                // (A2AException.IsRetryable), which is true for every unmarked Internal error.
                if (status is 503 or 429 or 401 or 403 or 408 || RetryPolicy.IsRetryable(a2a))
                    return ClientOutboxFailureKind.Retry;
                // The peer answered with a server error: it may have started the work before it failed. An invalid answer
                // (InvalidAgentResponse, status 500 by its code) lands here too: the peer may have taken the message.
                if (status is >= 500 and <= 599) return ClientOutboxFailureKind.RetryInDoubt;
                return ClientOutboxFailureKind.Fail;
            case HttpRequestException when RetryPolicy.ShouldRetry(RetryMode.UnsentOnly, error, out _):
                return ClientOutboxFailureKind.Retry;
            default:
                // Timeouts, dropped connections and anything unknown: the peer may have the message.
                return ClientOutboxFailureKind.RetryInDoubt;
        }
    }
}