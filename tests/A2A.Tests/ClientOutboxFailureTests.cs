using A2A.Client.Outbox;

namespace A2A.Tests;

/// <summary>The table that sorts a failed send: retry, retry in doubt, or fail.</summary>
public sealed class ClientOutboxFailureTests
{
    private static A2AException Http(A2AErrorCode code, int status, bool? retryable = null, TimeSpan? retryAfter = null) =>
        retryable is { } r
            ? new A2AException(code) { HttpStatus = status, Retryable = r, RetryAfter = retryAfter }
            : new A2AException(code) { HttpStatus = status, RetryAfter = retryAfter };

    public static TheoryData<string, Exception, string> Table => new()
    {
        { "503", Http(A2AErrorCode.InvalidRequest, 503), "Retry" },
        { "429", Http(A2AErrorCode.InvalidRequest, 429), "Retry" },
        { "401", Http(A2AErrorCode.Unauthenticated, 401), "Retry" },
        { "403", Http(A2AErrorCode.PermissionDenied, 403), "Retry" },
        { "408", Http(A2AErrorCode.InvalidRequest, 408), "Retry" },
        { "marked retryable", Http(A2AErrorCode.InvalidRequest, 400, retryable: true), "Retry" },
        { "rate limited code", new A2AException(A2AErrorCode.RateLimited), "Retry" },
        { "JSON-RPC internal error (status 200)", Http(A2AErrorCode.Internal, 200), "Retry" },
        { "JSON-RPC unauthenticated (status 200)", Http(A2AErrorCode.Unauthenticated, 200), "Retry" },
        { "JSON-RPC rate limited (status 200)", Http(A2AErrorCode.RateLimited, 200), "Retry" },
        { "JSON-RPC invalid params (status 200)", Http(A2AErrorCode.InvalidParams, 200), "Fail" },
        { "500 not retryable", Http(A2AErrorCode.InvalidRequest, 500), "RetryInDoubt" },
        { "502", Http(A2AErrorCode.InvalidRequest, 502), "RetryInDoubt" },
        { "504", Http(A2AErrorCode.InvalidRequest, 504, retryable: false), "RetryInDoubt" },
        { "400", Http(A2AErrorCode.InvalidRequest, 400), "Fail" },
        { "404", Http(A2AErrorCode.InvalidRequest, 404), "Fail" },
        { "422 key mismatch", A2AException.IdempotencyKeyMismatch(), "Fail" },
        { "protocol error without status", new A2AException(A2AErrorCode.InvalidParams), "Fail" },
        { "version not supported", new A2AException(A2AErrorCode.VersionNotSupported), "Fail" },
        { "connection refused", new HttpRequestException(HttpRequestError.ConnectionError, "refused"), "Retry" },
        { "dns", new HttpRequestException(HttpRequestError.NameResolutionError, "dns"), "Retry" },
        { "http request died after sending", new HttpRequestException(HttpRequestError.ResponseEnded, "reset"), "RetryInDoubt" },
        { "http request, no detail", new HttpRequestException("boom"), "RetryInDoubt" },
        { "timeout", new TimeoutException(), "RetryInDoubt" },
        { "call timeout (cancelled)", new OperationCanceledException(), "RetryInDoubt" },
        { "anything else", new InvalidOperationException("?"), "RetryInDoubt" },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Failure_is_sorted_as_the_table_says(string name, Exception error, string expected)
    {
        Assert.Equal(expected, ClientOutboxFailures.Classify(error, out _).ToString());
        Assert.False(string.IsNullOrEmpty(name));
    }

    [Fact]
    public void Retry_after_is_passed_on()
    {
        ClientOutboxFailures.Classify(Http(A2AErrorCode.InvalidRequest, 503, retryAfter: TimeSpan.FromSeconds(7)), out var retryAfter);
        Assert.Equal(TimeSpan.FromSeconds(7), retryAfter);
        ClientOutboxFailures.Classify(new TimeoutException(), out retryAfter);
        Assert.Null(retryAfter);
    }
}
