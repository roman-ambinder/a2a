using System.Net;
using System.Text;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace A2A.Tests;

/// <summary>
/// Anonymous callers share one task owner, but not one rate-limit bucket or stream budget: those are per remote IP address.
/// </summary>
public class AnonymousLimitTests
{
    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    private const string IpHeader = "X-Test-IP";

    /// <summary>Sets the connection's remote address from <see cref="IpHeader"/>, like a real connection (or ForwardedHeaders) would.</summary>
    private static void RemoteIpFromHeader(WebApplication app) =>
        app.Use((HttpContext ctx, RequestDelegate next) =>
        {
            if (ctx.Request.Headers.TryGetValue(IpHeader, out var ip)) ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
            return next(ctx);
        });

    private static HttpRequestMessage GetTask(string binding, string ip)
    {
        var req = binding == ProtocolBindings.JsonRpc
            ? new HttpRequestMessage(HttpMethod.Post, "/a2a")
            {
                Content = new StringContent($$$"""{"jsonrpc":"2.0","id":1,"method":"{{{A2AProtocol.Methods.GetTask}}}","params":{"id":"nope"}}""", Encoding.UTF8, "application/json"),
            }
            : new HttpRequestMessage(HttpMethod.Get, "/a2a/tasks/nope");
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        req.Headers.Add(IpHeader, ip);
        return req;
    }

    private static HttpRequestMessage SendStream(string binding, string text, string ip)
    {
        var message = $$$"""{"message":{"messageId":"{{{Guid.NewGuid():N}}}","role":"ROLE_USER","parts":[{"text":"{{{text}}}"}]}}""";
        var req = binding == ProtocolBindings.JsonRpc
            ? new HttpRequestMessage(HttpMethod.Post, "/a2a")
            {
                Content = new StringContent($$"""{"jsonrpc":"2.0","id":1,"method":"{{A2AProtocol.Methods.SendStreamingMessage}}","params":{{message}}}""", Encoding.UTF8, "application/json"),
            }
            : new HttpRequestMessage(HttpMethod.Post, "/a2a/message:stream") { Content = new StringContent(message, Encoding.UTF8, "application/json") };
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        req.Headers.Add(IpHeader, ip);
        return req;
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient http, HttpRequestMessage req)
    {
        using var resp = await http.SendAsync(req);
        return resp.StatusCode;
    }

    [Fact]
    public void Limit_partition_keys_anonymous_callers_by_address_and_never_collides_with_a_principal()
    {
        Assert.Equal("anon:203.0.113.1", ServerCallContext.LimitPartitionFor(ServerCallContext.AnonymousPrincipal, "203.0.113.1"));
        Assert.Equal("anon:unknown", ServerCallContext.LimitPartitionFor(ServerCallContext.AnonymousPrincipal, null));
        Assert.Equal("user:alice", ServerCallContext.LimitPartitionFor("alice", "203.0.113.1"));
        Assert.NotEqual(ServerCallContext.LimitPartitionFor("anon:203.0.113.1", null),
            ServerCallContext.LimitPartitionFor(ServerCallContext.AnonymousPrincipal, "203.0.113.1"));
    }

    [Theory]
    [InlineData("2001:db8:1:2:3:4:5:6", "anon:2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "anon:2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:3::1", "anon:2001:db8:1:3::/64")]
    [InlineData("::ffff:203.0.113.1", "anon:203.0.113.1")]
    [InlineData("203.0.113.1", "anon:203.0.113.1")]
    [InlineData("::1", "anon:::/64")]
    [InlineData("fe80::1%eth0", "anon:fe80::/64")] // the zone id is dropped with the host bits
    [InlineData("::", "anon:::/64")]
    public void Limit_partition_groups_anonymous_ipv6_callers_by_slash_64(string address, string expected)
    {
        Assert.Equal(expected, ServerCallContext.LimitPartitionFor(ServerCallContext.AnonymousPrincipal, address));
        Assert.Equal("user:alice", ServerCallContext.LimitPartitionFor("alice", address));
    }

    [Fact]
    public void Limit_partition_keeps_an_unparseable_address_as_its_own_key()
    {
        Assert.Equal("anon:not-an-ip", ServerCallContext.LimitPartitionFor(ServerCallContext.AnonymousPrincipal, "not-an-ip"));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Anonymous_rate_limit_is_per_remote_address(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.AllowAnonymous = true, requireAuth: false, configurePipeline: RemoteIpFromHeader,
            services: s => s.AddA2ARateLimiting(o => { o.TokenLimit = 2; o.TokensPerPeriod = 1; o.ReplenishmentPeriod = TimeSpan.FromHours(1); }));
        using var http = host.Http();

        for (var i = 0; i < 2; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, await StatusAsync(http, GetTask(binding, "203.0.113.1")));
        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(http, GetTask(binding, "203.0.113.1")));

        // Another address has its own bucket; the first address stays limited.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await StatusAsync(http, GetTask(binding, "203.0.113.2")));
        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(http, GetTask(binding, "203.0.113.1")));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Anonymous_stream_budget_is_per_remote_address(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o =>
        {
            o.AllowAnonymous = true;
            o.Limits.MaxConcurrentStreamsPerPrincipal = 1;
        }, requireAuth: false, configurePipeline: RemoteIpFromHeader,
            services: s => s.AddA2ARateLimiting(o => o.Enabled = false));
        using var http = host.Http();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var first = await http.SendAsync(SendStream(binding, "slow", "203.0.113.1"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", first.Content.Headers.ContentType?.MediaType);

        using var sameAddress = await http.SendAsync(SendStream(binding, "slow", "203.0.113.1"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, sameAddress.StatusCode);

        using var otherAddress = await http.SendAsync(SendStream(binding, "slow", "203.0.113.2"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", otherAddress.Content.Headers.ContentType?.MediaType);
    }
}
