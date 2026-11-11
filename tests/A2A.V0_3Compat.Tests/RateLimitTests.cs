using System.Net;
using System.Text.Json.Nodes;
using A2A.Server.AspNetCore;

namespace A2A.V0_3Compat.Tests;

/// <summary>A rate limit rejection on a v0.3 route is answered in the v0.3 format, as every other v0.3 error is.</summary>
public class RateLimitTests
{
    [Fact]
    public async Task A_v03_rest_route_gets_its_rate_limit_rejection_in_the_v03_error_format()
    {
        await using var host = await V03Host.StartAsync(services: s =>
            s.AddA2ARateLimiting(o => { o.TokenLimit = 1; o.TokensPerPeriod = 1; o.ReplenishmentPeriod = TimeSpan.FromHours(1); }));
        using var http = host.Http();
        using (await http.GetAsync("/a2a/v1/tasks/nope")) { } // uses the one token

        using var response = await http.GetAsync("/a2a/v1/tasks/nope");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero, "Retry-After is missing");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        // v0.3 HTTP+JSON errors are { code, message }, not the v1.0 google.rpc.Status body ({ error: { ... } }).
        Assert.False(body.ContainsKey("error"), body.ToJsonString());
        Assert.Equal((int)A2AErrorCode.RateLimited, body["code"]!.GetValue<int>());
        Assert.False(string.IsNullOrEmpty(body["message"]!.GetValue<string>()));
    }
}
