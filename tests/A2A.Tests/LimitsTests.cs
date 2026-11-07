using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace A2A.Tests;

public class LimitsTests
{
    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    private const string OneMessage = """{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"hi"}]}}""";

    private static HttpRequestMessage SendRequest(string binding, string paramsJson, string user = "alice")
    {
        var rpc = binding == ProtocolBindings.JsonRpc;
        var req = new HttpRequestMessage(HttpMethod.Post, rpc ? "/a2a" : "/a2a/message:send")
        {
            Content = new StringContent(
                rpc ? $$"""{"jsonrpc":"2.0","id":1,"method":"{{A2AProtocol.Methods.SendMessage}}","params":{{paramsJson}}}""" : paramsJson,
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        req.Headers.Add("X-Test-User", user);
        return req;
    }

    private static HttpRequestMessage GetTaskRequest(string binding, string user = "alice")
    {
        var rpc = binding == ProtocolBindings.JsonRpc;
        var req = rpc
            ? new HttpRequestMessage(HttpMethod.Post, "/a2a")
            {
                Content = new StringContent($$$"""{"jsonrpc":"2.0","id":1,"method":"{{{A2AProtocol.Methods.GetTask}}}","params":{"id":"nope"}}""", Encoding.UTF8, "application/json"),
            }
            : new HttpRequestMessage(HttpMethod.Get, "/a2a/tasks/nope");
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        req.Headers.Add("X-Test-User", user);
        return req;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Send(TestAgentHost host, HttpRequestMessage req)
    {
        using var client = host.Http();
        using var resp = await client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string ErrorText(JsonElement body) => body.GetProperty("error").GetRawText();

    private static string Deep(int levels) => string.Concat(Enumerable.Repeat("{\"a\":", levels)) + "1" + new string('}', levels);

    // ---- defaults ----

    [Fact]
    public void Defaults_match_the_spec()
    {
        var l = new A2ALimits();
        Assert.Equal(10 * 1024 * 1024, l.MaxRequestBodyBytes);
        Assert.Equal(100, l.MaxPartsPerMessage);
        Assert.Equal(8 * 1024 * 1024, l.MaxRawPartBytes);
        Assert.Equal(32, l.MaxJsonDepth);
        Assert.Equal(100, l.MaxHistoryLengthReturned);
        Assert.Equal(100, l.MaxListPageSize);
        Assert.Equal(10, l.MaxConcurrentStreamsPerTask);
        Assert.Equal(50, l.MaxConcurrentStreamsPerPrincipal);
        Assert.Equal(4 * 1024 * 1024, l.MaxEventBytes);
        Assert.Equal(TimeSpan.FromHours(1), l.MaxStreamDuration);
        Assert.Equal(TimeSpan.FromMinutes(5), l.StreamReauthInterval);
        Assert.Equal(10, l.MaxPushConfigsPerTask);
        Assert.NotNull(new A2AServerOptions().Limits);
        Assert.True(new A2AServerOptions().EnableRateLimiting);
    }

    // ---- body size ----

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Body_over_limit_returns_413_payload_too_large(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.Limits.MaxRequestBodyBytes = 1024);
        var big = $$$"""{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"{{{new string('x', 2048)}}}"}]}}""";
        var (status, body) = await Send(host, SendRequest(binding, big));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Contains("PAYLOAD_TOO_LARGE", ErrorText(body));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Body_under_limit_is_accepted(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.Limits.MaxRequestBodyBytes = 1024);
        var (status, _) = await Send(host, SendRequest(binding, OneMessage));
        Assert.Equal(HttpStatusCode.OK, status);
    }

    // ---- parts ----

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Too_many_parts_rejected(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var message = new Message
        {
            MessageId = "m1", Role = Role.User,
            Parts = Enumerable.Range(0, 101).Select(i => (Part)Part.FromText($"p{i}")).ToList(),
        };
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding).SendMessageAsync(message));
        Assert.Equal(A2AErrorCode.InvalidRequest, ex.Code);
        Assert.Contains("100", ex.Message);
    }

    [Fact]
    public void Exactly_the_part_limit_is_accepted()
    {
        var message = new Message { MessageId = "m1", Role = Role.User, Parts = Enumerable.Range(0, 100).Select(i => (Part)Part.FromText("p")).ToList() };
        RequestLimitValidator.Validate(message, new A2ALimits());
    }

    [Fact]
    public void Raw_part_over_limit_rejected()
    {
        var limits = new A2ALimits { MaxRawPartBytes = 16 };
        var message = new Message { MessageId = "m1", Role = Role.User, Parts = [Part.FromBytes(new byte[32], "application/octet-stream")] };
        var ex = Assert.Throws<A2AException>(() => RequestLimitValidator.Validate(message, limits));
        Assert.Equal(413, ex.HttpStatus);
        RequestLimitValidator.Validate(new Message { MessageId = "m2", Role = Role.User, Parts = [Part.FromBytes(new byte[16], "application/octet-stream")] }, limits);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Raw_part_over_limit_returns_413_on_the_wire(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.Limits.MaxRawPartBytes = 16);
        var raw = Convert.ToBase64String(new byte[32]);
        var json = $$$"""{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"raw":"{{{raw}}}"}]}}""";
        var (status, body) = await Send(host, SendRequest(binding, json));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Contains("PAYLOAD_TOO_LARGE", ErrorText(body));
    }

    // ---- JSON shape ----

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Duplicate_json_property_rejected(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        const string dup = """{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"a"}]},"message":{"messageId":"m2","role":"ROLE_USER","parts":[{"text":"b"}]}}""";
        var (status, body) = await Send(host, SendRequest(binding, dup));
        AssertInvalidParams(binding, status, body);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Json_deeper_than_32_rejected(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        // 40 sits between the request limit (32) and the serializer ceiling (64); 100 is beyond both. Same answer for both.
        foreach (var levels in new[] { 40, 100 })
        {
            var json = $$$"""{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"a"}],"metadata":{{{Deep(levels)}}}}}""";
            var (status, body) = await Send(host, SendRequest(binding, json));
            AssertInvalidParams(binding, status, body);
        }
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Request_at_the_maximum_depth_is_stored_and_served_back(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        // JSON-RPC wraps params two levels deep (root, params, message, metadata), so 29 nested objects reach depth 32 exactly.
        var json = $$$"""{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"task go"}],"metadata":{{{Deep(29)}}}}}""";
        var (status, body) = await Send(host, SendRequest(binding, json));
        Assert.Equal(HttpStatusCode.OK, status);
        var task = binding == ProtocolBindings.JsonRpc ? body.GetProperty("result").GetProperty("task") : body.GetProperty("task");
        var id = task.GetProperty("id").GetString()!;

        foreach (var b in new[] { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson })
        {
            var fetched = await host.Client(b).GetTaskAsync(id);
            Assert.Contains(fetched.History!, m => m.Role == Role.User && m.Metadata is { Count: > 0 });
        }
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Json_within_depth_limit_is_accepted(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var json = $$$"""{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"a"}],"metadata":{{{Deep(10)}}}}}""";
        var (status, _) = await Send(host, SendRequest(binding, json));
        Assert.Equal(HttpStatusCode.OK, status);
    }

    private static void AssertInvalidParams(string binding, HttpStatusCode status, JsonElement body)
    {
        if (binding == ProtocolBindings.JsonRpc)
        {
            // JSON-RPC errors travel in a 200 response.
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal((int)A2AErrorCode.InvalidParams, body.GetProperty("error").GetProperty("code").GetInt32());
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal(400, body.GetProperty("error").GetProperty("code").GetInt32());
        }
    }

    [Fact]
    public void Net8_duplicate_checker_detects_duplicates()
    {
        static bool Check(string json) => HttpPlumbing.ContainsDuplicateProperties(Encoding.UTF8.GetBytes(json), 32);

        Assert.True(Check("{\"a\":1,\"a\":2}"));
        Assert.True(Check("{\"x\":{\"a\":1,\"a\":2}}"));
        Assert.True(Check("{\"a\":1,\"\\u0061\":2}"));          // escaped spelling of the same name
        Assert.True(Check("[{\"b\":[{\"c\":1,\"c\":2}]}]"));
        Assert.False(Check("{\"a\":1,\"b\":{\"a\":2}}"));        // same name at another level
        Assert.False(Check("[{\"a\":1},{\"a\":2}]"));            // same name in sibling objects
        Assert.False(Check("{\"a\":1,\"A\":2}"));                // names are case sensitive
        Assert.False(Check("{}"));
    }

    [Fact]
    public void Net8_duplicate_checker_enforces_depth()
    {
        Assert.ThrowsAny<JsonException>(() => HttpPlumbing.ContainsDuplicateProperties(Encoding.UTF8.GetBytes(Deep(40)), 32));
        Assert.False(HttpPlumbing.ContainsDuplicateProperties(Encoding.UTF8.GetBytes(Deep(10)), 32));
    }

    // ---- history cap ----

    [Fact]
    public async Task History_capped_without_history_length()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.Limits.MaxHistoryLengthReturned = 2);
        var store = host.App.Services.GetRequiredService<TaskStore>();
        var now = DateTimeOffset.UtcNow;
        var task = new AgentTask
        {
            Id = "t-history", ContextId = "c-history",
            Status = new AgentTaskStatus { State = TaskState.Completed },
            History = Enumerable.Range(0, 4).Select(i => Message.FromUser($"m{i}")).ToList(),
        };
        await store.GetOrCreateContextAsync(new ContextRecord("", "alice", "c-history", now));
        await store.CreateAsync(new TaskRecord("", "alice", task, 0, now, now));

        var client = host.Client(ProtocolBindings.HttpJson);
        var capped = await client.GetTaskAsync("t-history");
        Assert.Equal(["m2", "m3"], capped.History!.Select(m => m.GetText()));
        Assert.Single((await client.GetTaskAsync("t-history", 1)).History!);
        Assert.Equal(2, (await client.GetTaskAsync("t-history", 50)).History!.Count);   // a larger request is capped too
        Assert.Null((await client.GetTaskAsync("t-history", 0)).History);
    }

    // ---- rate limiting ----

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Rate_limit_returns_429_with_retry_after(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddA2ARateLimiting(o => { o.TokenLimit = 2; o.TokensPerPeriod = 1; o.ReplenishmentPeriod = TimeSpan.FromHours(1); }));
        for (var i = 0; i < 2; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Send(host, GetTaskRequest(binding))).Status);

        using var client = host.Http();
        using var resp = await client.SendAsync(GetTaskRequest(binding));
        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.True(resp.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero, "Retry-After header is missing or not positive");
        var text = await resp.Content.ReadAsStringAsync();
        Assert.Contains("RATE_LIMITED", text);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Rate_limit_partitions_by_principal(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddA2ARateLimiting(o => { o.TokenLimit = 2; o.TokensPerPeriod = 1; o.ReplenishmentPeriod = TimeSpan.FromHours(1); }));
        for (var i = 0; i < 2; i++) await Send(host, GetTaskRequest(binding, "alice"));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(host, GetTaskRequest(binding, "alice"))).Status);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Send(host, GetTaskRequest(binding, "bob"))).Status);
    }

    [Fact]
    public async Task Rate_limit_cannot_be_bypassed_by_varying_the_tenant()
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddA2ARateLimiting(o => { o.TokenLimit = 2; o.TokensPerPeriod = 1; o.ReplenishmentPeriod = TimeSpan.FromHours(1); }));
        HttpRequestMessage Req(string tenant, string user)
        {
            var r = new HttpRequestMessage(HttpMethod.Get, $"/a2a/{tenant}/tasks/nope");
            r.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
            r.Headers.Add("X-Test-User", user);
            return r;
        }
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Send(host, Req("t1", "alice"))).Status);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Send(host, Req("t2", "alice"))).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(host, Req("t3", "alice"))).Status);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Send(host, Req("t1", "bob"))).Status);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Host_supplied_a2a_policy_replaces_the_default(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 418; // default policy would answer 429 and allow 20 requests
            o.AddPolicy("a2a", _ => RateLimitPartition.GetFixedWindowLimiter("all", __ =>
                new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
        }));
        Assert.NotEqual((HttpStatusCode)418, (await Send(host, GetTaskRequest(binding))).Status);
        using var client = host.Http();
        using var resp = await client.SendAsync(GetTaskRequest(binding));
        Assert.Equal((HttpStatusCode)418, resp.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Rate_limit_can_be_disabled(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.EnableRateLimiting = false, useA2A: false,
            services: s => s.AddA2ARateLimiting(o => { o.TokenLimit = 1; o.TokensPerPeriod = 1; o.ReplenishmentPeriod = TimeSpan.FromHours(1); }));
        for (var i = 0; i < 5; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Send(host, GetTaskRequest(binding))).Status);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Handler_rate_limit_error_carries_retry_after_on_the_wire(string binding)
    {
        // Closes the Task 2 gap: the 429 raised inside the handler reaches the client with its Retry-After header.
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddSingleton<TaskStore, RateLimitedTaskStore>());
        using var client = host.Http();
        using var resp = await client.SendAsync(GetTaskRequest(binding));
        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(3), resp.Headers.RetryAfter?.Delta);
    }

    // ---- startup check for the rate limiter middleware ----

    [Fact]
    public async Task Startup_fails_without_the_rate_limiter_middleware()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestAgentHost.StartAsync(useA2A: false));
        Assert.Equal("Call app.UseA2A() (it adds the rate limiter middleware) or set A2AServerOptions.EnableRateLimiting = false.", ex.Message);
    }

    [Fact]
    public async Task Startup_succeeds_without_the_middleware_when_rate_limiting_is_disabled()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.EnableRateLimiting = false, useA2A: false);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Startup_style_hosting_checks_the_rate_limiter_after_start(bool useA2A, bool expectStopped)
    {
        using var host = new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(s =>
            {
                s.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
                s.AddAuthorization();
                s.AddRouting();
                s.AddA2AServer(o => o.AgentCard = TestAgentHost.DefaultCard()).AddAgentExecutor<ScenarioAgent>();
                s.AddA2AServerAspNetCore();
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                if (useA2A) app.UseA2A();
                app.UseAuthorization();
                app.UseEndpoints(e => e.MapA2A("/a2a").RequireAuthorization());
            })).Build();
        await host.StartAsync();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        Assert.Equal(expectStopped, lifetime.ApplicationStopping.IsCancellationRequested);
        await host.StopAsync();
    }
}
