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
    Fail,
}

/// <summary>Sorts the exception of a send into what happens next. A pure function, so its table can be tested alone.</summary>
internal static class ClientOutboxFailures
{
    /// <summary>
    /// Sorts <paramref name="error"/>. <paramref name="retryAfter"/> is the peer's <c>Retry-After</c>, when it gave one.
    /// A cancelled dispatcher is not an error: callers check that before they call this.
    /// </summary>
    public static ClientOutboxFailureKind Classify(Exception error, out TimeSpan? retryAfter)
    {
        retryAfter = null;
        switch (error)
        {
            case A2AException a2a:
                retryAfter = a2a.RetryAfter;
                if (a2a.HttpStatus is 503 or 429 or 401 or 403 or 408 || a2a.IsRetryable) return ClientOutboxFailureKind.Retry;
                // The peer answered with a server error: it may have started the work before it failed.
                if (a2a.HttpStatus is >= 500 and <= 599) return ClientOutboxFailureKind.RetryInDoubt;
                return ClientOutboxFailureKind.Fail;
            case HttpRequestException when RetryPolicy.ShouldRetry(RetryMode.UnsentOnly, error, out _):
                return ClientOutboxFailureKind.Retry;
            default:
                // Timeouts, dropped connections and anything unknown: the peer may have the message.
                return ClientOutboxFailureKind.RetryInDoubt;
        }
    }
}
