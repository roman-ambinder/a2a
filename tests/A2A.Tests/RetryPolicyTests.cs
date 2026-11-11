using System.Net.Http;
using A2A.Client.Resilience;
using A2A.Security.Http;

namespace A2A.Tests;

/// <summary>A target the SSRF guard blocks stays blocked: retrying it only adds backoff before the same failure.</summary>
public class RetryPolicyTests
{
    public static TheoryData<RetryMode> Modes => new() { RetryMode.Idempotent, RetryMode.UnsentOnly };

    [Theory]
    [MemberData(nameof(Modes))]
    public void A_blocked_address_is_not_retried(RetryMode mode)
    {
        Assert.False(RetryPolicy.ShouldRetry(mode, new BlockedAddressException("blocked"), out _));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void A_blocked_address_reported_as_a_connection_failure_is_not_retried(RetryMode mode)
    {
        // What SocketsHttpHandler makes of an exception thrown by the guard's connect callback.
        var wrapped = new HttpRequestException(HttpRequestError.ConnectionError, "connect failed", new BlockedAddressException("blocked"));
        Assert.False(RetryPolicy.ShouldRetry(mode, wrapped, out _));
    }

    [Fact]
    public void An_ordinary_connection_failure_is_still_retried()
    {
        var failure = new HttpRequestException(HttpRequestError.ConnectionError, "connect failed");
        Assert.True(RetryPolicy.ShouldRetry(RetryMode.UnsentOnly, failure, out _));
        Assert.True(RetryPolicy.ShouldRetry(RetryMode.Idempotent, failure, out _));
    }
}
