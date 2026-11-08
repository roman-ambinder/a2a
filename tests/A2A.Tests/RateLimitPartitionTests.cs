using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace A2A.Tests;

/// <summary>
/// The "a2a" rate limit runs before authorization, so anonymous callers must not be able to create partitions with random
/// tenant names, and an idle partition must really free its bucket (and the bucket's timer).
/// </summary>
public class RateLimitPartitionTests
{
    private sealed class NameResolver : IPrincipalResolver
    {
        public string? Resolve(ClaimsPrincipal user) => user.Identity?.Name;
    }

    private static A2ARateLimiterPolicy NewPolicy() =>
        new(Options.Create(new A2ARateLimitOptions()), new RouteTenantResolver(), new NameResolver(), []);

    private static DefaultHttpContext Request(string tenant, string? user)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.RouteValues["tenant"] = tenant;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        if (user is not null) ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "test", ClaimTypes.Name, ClaimTypes.Role));
        return ctx;
    }

    [Fact]
    public async Task Disposing_the_limiter_disposes_the_inner_bucket()
    {
        var inner = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromMinutes(1), AutoReplenishment = true,
        });
        using var perPrincipal = PartitionedRateLimiter.Create<string, string>(
            p => RateLimitPartition.GetNoLimiter(p));
        var limiter = new TenantAndPrincipalLimiter(inner, perPrincipal, "user:x");

        // The partitioned limiter's idle cleanup calls DisposeAsync.
        await limiter.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => inner.AttemptAcquire());
    }

    [Fact]
    public void Anonymous_callers_use_one_partition_whatever_the_tenant()
    {
        using var policy = NewPolicy();
        var keys = Enumerable.Range(0, 1000)
            .Select(i => policy.GetPartition(Request("random-" + Guid.NewGuid().ToString("N") + i, user: null)).PartitionKey)
            .Distinct()
            .ToList();

        Assert.Single(keys);
    }

    [Fact]
    public void Authenticated_callers_get_a_partition_per_tenant_and_caller()
    {
        using var policy = NewPolicy();
        string Key(string tenant, string user) => policy.GetPartition(Request(tenant, user)).PartitionKey;

        Assert.NotEqual(Key("a", "alice"), Key("b", "alice"));
        Assert.NotEqual(Key("a", "alice"), Key("a", "bob"));
        Assert.Equal(Key("a", "alice"), Key("a", "alice"));
        Assert.NotEqual(Key("a", "alice"), policy.GetPartition(Request("a", user: null)).PartitionKey);
    }
}
