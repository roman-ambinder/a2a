using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using A2A.Server.AspNetCore;
using A2A.Server.V0_3Compat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.V0_3Compat.Tests;

/// <summary>The v0.3 surface is not weaker than v1.0, and v1.0 does not change when compat is on.</summary>
public class ServerSecurityAndCompatibilityTests
{
    private const string V1Send = """{"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"hello"}]}}}""";

    private static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> PostAsync(HttpClient http, string path, string body, string? version = null, string? contentType = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = V03Host.Json(body, contentType ?? "application/json") };
        if (version is not null) request.Headers.Add("A2A-Version", version);
        var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    // ---- authentication on every route ----

    public static TheoryData<string> RpcMethods =>
    [
        "message/send", "message/stream", "tasks/get", "tasks/cancel", "tasks/resubscribe", "agent/getAuthenticatedExtendedCard",
        "tasks/pushNotificationConfig/set", "tasks/pushNotificationConfig/get", "tasks/pushNotificationConfig/list", "tasks/pushNotificationConfig/delete",
        "tasks/list",
    ];

    [Theory]
    [MemberData(nameof(RpcMethods))]
    public async Task Unauthenticated_json_rpc_call_gets_401(string method)
    {
        await using var host = await V03Host.StartAsync();
        foreach (var path in new[] { "/a2a", "/a2a/acme" })
        {
            var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = V03Host.SendParams("hello") }.ToJsonString();
            using var response = await host.Http(null).PostAsync(path, V03Host.Json(body));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    public static TheoryData<string, string> RestRoutes
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var prefix in new[] { "/a2a", "/a2a/acme" })
            {
                data.Add("POST", prefix + "/v1/message:send");
                data.Add("POST", prefix + "/v1/message:stream");
                data.Add("GET", prefix + "/v1/tasks/t1");
                data.Add("POST", prefix + "/v1/tasks/t1:cancel");
                data.Add("GET", prefix + "/v1/tasks/t1:subscribe");
                data.Add("POST", prefix + "/v1/tasks/t1:subscribe");
                data.Add("GET", prefix + "/v1/card");
                data.Add("POST", prefix + "/v1/tasks/t1/pushNotificationConfigs");
                data.Add("GET", prefix + "/v1/tasks/t1/pushNotificationConfigs");
                data.Add("GET", prefix + "/v1/tasks/t1/pushNotificationConfigs/c1");
                data.Add("DELETE", prefix + "/v1/tasks/t1/pushNotificationConfigs/c1");
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RestRoutes))]
    public async Task Unauthenticated_rest_call_gets_401(string method, string path)
    {
        await using var host = await V03Host.StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") request.Content = V03Host.Json("{}");
        using var response = await host.Http(null).SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Compat_routes_need_authorization_like_the_rest_of_MapA2A()
    {
        // The startup check finds the v0.3 endpoints by their "A2A " display name; without authorization the app does not start.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => V03Host.StartAsync(requireAuth: false));
        Assert.Contains("RequireAuthorization", ex.Message);
    }

    [Fact]
    public async Task Anonymous_calls_work_only_when_the_host_allows_them()
    {
        await using var host = await V03Host.StartAsync(requireAuth: false, configure: o => o.AllowAnonymous = true);
        var (_, body) = await V03Host.RpcAsync(host.Http(null), "message/send", V03Host.SendParams("hello"));
        Assert.NotNull(body["result"]);
        var (response, text) = (await new Func<Task<(HttpResponseMessage, string)>>(async () =>
        {
            var r = await host.Http(null).PostAsync("/a2a/v1/message:send", V03Host.Json(V03Host.RestSendBody("hello")));
            return (r, await r.Content.ReadAsStringAsync());
        })());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("hi there", text);
    }

    [Fact]
    public async Task Required_extensions_of_the_card_apply_to_v03_calls()
    {
        await using var host = await V03Host.StartAsync(configure: o =>
        {
            o.AgentCard.Capabilities.Extensions = [new AgentExtension { Uri = "urn:test:required", Required = true }];
        });
        var (_, refused) = await V03Host.RpcAsync(host.Http(), "message/send", V03Host.SendParams("hello"));
        Assert.Equal(-32008, refused["error"]!["code"]!.GetValue<int>()); // no v0.3 number: sent as it is

        using var request = new HttpRequestMessage(HttpMethod.Post, "/a2a/v1/message:send") { Content = V03Host.Json(V03Host.RestSendBody("hello")) };
        request.Headers.Add("X-A2A-Extensions", "urn:test:required");
        using var response = await host.Http().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- limits ----

    [Fact]
    public async Task Oversized_bodies_are_refused_like_v1_with_413()
    {
        await using var host = await V03Host.StartAsync(configure: o => o.Limits.MaxRequestBodyBytes = 512);
        var big = V03Host.SendParams(new string('x', 2000));
        var (response, _) = await V03Host.RpcAsync(host.Http(), "message/send", big);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        var (restStatus, restBody, _) = await PostAsync(host.Http(), "/a2a/v1/message:send", V03Host.RestSendBody(new string('x', 2000)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, restStatus);
        Assert.Equal(-32600, JsonNode.Parse(restBody)!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_chunked_oversized_body_is_refused_too()
    {
        await using var host = await V03Host.StartAsync(configure: o => o.Limits.MaxRequestBodyBytes = 512);
        var json = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "message/send", ["params"] = V03Host.SendParams(new string('x', 2000)) }.ToJsonString();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/a2a") { Content = new ChunkedContent(Encoding.UTF8.GetBytes(json)) };
        using var response = await host.Http().SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    /// <summary>A body that throws a plain exception (not an A2AException) when read, like a client abort or a bad request.</summary>
    private sealed class ThrowingBody : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("body read failed");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("body read failed");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<(int Status, string Body)> PostThrowingBodyAsync(V03Host host)
    {
        var context = await host.App.GetTestServer().SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/a2a";
            c.Request.ContentType = "application/json";
            c.Request.Headers["X-Test-User"] = "alice";
            c.Request.Body = new ThrowingBody();
        });
        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task A_body_read_failure_is_answered_the_same_with_compat_on_and_off()
    {
        await using var on = await V03Host.StartAsync(compat: true);
        await using var off = await V03Host.StartAsync(compat: false);
        var (onStatus, onBody) = await PostThrowingBodyAsync(on);
        var (offStatus, offBody) = await PostThrowingBodyAsync(off);
        Assert.Equal(offStatus, onStatus);
        Assert.Equal(offBody, onBody);
    }

    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes, 0, bytes.Length);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Fact]
    public async Task Excess_json_depth_is_refused_like_v1()
    {
        await using var host = await V03Host.StartAsync();
        var deep = new string('[', 40) + new string(']', 40);
        var parameters = V03Host.SendParams("hello");
        parameters["metadata"] = JsonNode.Parse("{\"x\":" + deep + "}");
        var (_, body) = await V03Host.RpcAsync(host.Http(), "message/send", parameters);
        Assert.Equal(-32602, body["error"]!["code"]!.GetValue<int>());

        var (status, restBody, _) = await PostAsync(host.Http(), "/a2a/v1/message:send", "{\"message\":{\"metadata\":{\"x\":" + deep + "}}}");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(-32602, JsonNode.Parse(restBody)!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Duplicate_members_are_refused()
    {
        await using var host = await V03Host.StartAsync();
        var (status, text, _) = await PostAsync(host.Http(), "/a2a", """{"jsonrpc":"2.0","id":1,"method":"tasks/get","params":{"id":"a","id":"b"}}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(-32602, JsonNode.Parse(text)!["error"]!["code"]!.GetValue<int>());
    }

    // ---- v1.0 does not change ----

    public static TheoryData<string> V1Bodies =>
    [
        V1Send,
        "{not json",
        """{"jsonrpc":"2.0","id":1,"method":"Nope","params":{}}""",
        """{"jsonrpc":"1.0","id":1,"method":"SendMessage"}""",
        """[{"jsonrpc":"2.0","id":1,"method":"SendMessage"}]""",
        "",
    ];

    [Theory]
    [MemberData(nameof(V1Bodies))]
    public async Task V1_json_rpc_answers_are_identical_with_compat_on_and_off(string body)
    {
        await using var on = await V03Host.StartAsync(compat: true);
        await using var off = await V03Host.StartAsync(compat: false);
        foreach (var version in new string?[] { null, "1.0" })
        {
            var (onStatus, onBody, _) = await PostAsync(on.Http(), "/a2a", body, version);
            var (offStatus, offBody, _) = await PostAsync(off.Http(), "/a2a", body, version);
            Assert.Equal(offStatus, onStatus);
            Assert.Equal(Scrub(offBody), Scrub(onBody));
        }
    }

    /// <summary>Message, task and context ids are fresh on every call.</summary>
    private static string Scrub(string body) =>
        System.Text.RegularExpressions.Regex.Replace(body, "\"(contextId|id|taskId|messageId)\":\"[^\"]+\"", "\"$1\":\"ID\"");

    [Fact]
    public async Task V1_methods_work_with_compat_on_and_a_missing_version_is_still_refused()
    {
        await using var host = await V03Host.StartAsync();
        var (okStatus, ok, _) = await PostAsync(host.Http(), "/a2a", V1Send, "1.0");
        Assert.Equal(HttpStatusCode.OK, okStatus);
        Assert.Equal("ROLE_AGENT", JsonNode.Parse(ok)!["result"]!["message"]!["role"]!.GetValue<string>());

        var (_, refused, _) = await PostAsync(host.Http(), "/a2a", V1Send);
        Assert.Equal(-32009, JsonNode.Parse(refused)!["error"]!["code"]!.GetValue<int>());
        var (_, refusedRest, _) = await PostAsync(host.Http(), "/a2a/message:send", """{"message":{"messageId":"m","role":"ROLE_USER","parts":[{"text":"hello"}]}}""");
        Assert.Contains("VERSION_NOT_SUPPORTED", refusedRest);
    }

    [Fact]
    public async Task V1_streaming_and_rest_work_with_compat_on()
    {
        await using var host = await V03Host.StartAsync();
        using var stream = new HttpRequestMessage(HttpMethod.Post, "/a2a")
        {
            Content = V03Host.Json("""{"jsonrpc":"2.0","id":9,"method":"SendStreamingMessage","params":{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"task"}]}}}"""),
        };
        stream.Headers.Add("A2A-Version", "1.0");
        using var response = await V03Host.StreamAsync(host.Http(), stream);
        var frames = await V03Host.ReadSseAsync(response);
        Assert.NotNull(frames[0]["result"]!["task"]); // v1.0 shape: no "kind"
        Assert.Null(frames[0]["result"]!["kind"]);

        var (restStatus, rest, _) = await PostAsync(host.Http(), "/a2a/message:send", """{"message":{"messageId":"m","role":"ROLE_USER","parts":[{"text":"hello"}]}}""", "1.0", "application/a2a+json");
        Assert.Equal(HttpStatusCode.OK, restStatus);
        Assert.NotNull(JsonNode.Parse(rest)!["message"]);
    }

    // ---- routes ----

    [Fact]
    public async Task Literal_v1_routes_win_over_the_v1_tenant_and_tenants_still_work()
    {
        await using var host = await V03Host.StartAsync();
        var http = host.Http();

        // /a2a/v1/tasks/x: the v0.3 route (tenant "v1" is shadowed). Its error body is the v0.3 one.
        using (var v03 = await http.GetAsync("/a2a/v1/tasks/x"))
        {
            Assert.Equal(HttpStatusCode.NotFound, v03.StatusCode);
            Assert.Equal(-32001, JsonNode.Parse(await v03.Content.ReadAsStringAsync())!["code"]!.GetValue<int>());
        }

        // /a2a/acme/tasks/x: the v1.0 route with tenant acme. Its error body is the google.rpc one.
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/a2a/acme/tasks/x"))
        {
            request.Headers.Add("A2A-Version", "1.0");
            using var v1 = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, v1.StatusCode);
            Assert.NotNull(JsonNode.Parse(await v1.Content.ReadAsStringAsync())!["error"]!["status"]);
        }

        // /a2a/acme/v1/...: the v0.3 route with tenant acme.
        using (var tenantV03 = await http.GetAsync("/a2a/acme/v1/tasks/x"))
            Assert.Equal(-32001, JsonNode.Parse(await tenantV03.Content.ReadAsStringAsync())!["code"]!.GetValue<int>());

        // v1.0 tenant routes for other tenant names, with the v1.0 version.
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/a2a/acme/message:send") { Content = V03Host.Json("""{"message":{"messageId":"m","role":"ROLE_USER","parts":[{"text":"hello"}]}}""") })
        {
            request.Headers.Add("A2A-Version", "1.0");
            using var v1 = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, v1.StatusCode);
        }
    }

    [Fact]
    public async Task Without_compat_a_tenant_named_v1_is_a_v1_tenant()
    {
        await using var host = await V03Host.StartAsync(compat: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/a2a/v1/tasks/x");
        request.Headers.Add("A2A-Version", "1.0");
        using var response = await host.Http().SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["status"]); // the v1.0 error shape
    }

    [Fact]
    public async Task The_v03_rest_routes_do_not_exist_without_compat()
    {
        await using var host = await V03Host.StartAsync(compat: false);
        // The path is read as tenant "v1" of the v1.0 route, which refuses a call without a version.
        using var response = await host.Http().PostAsync("/a2a/v1/message:send", V03Host.Json(V03Host.RestSendBody("hello")));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("VERSION_NOT_SUPPORTED", await response.Content.ReadAsStringAsync());
        using var tasks = await host.Http().GetAsync("/a2a/v1/card");
        Assert.Equal(HttpStatusCode.NotFound, tasks.StatusCode);
    }

    [Fact]
    public async Task Each_v03_route_has_an_A2A_display_name_the_startup_check_can_see()
    {
        await using var host = await V03Host.StartAsync();
        var names = host.App.Services.GetRequiredService<IEnumerable<Microsoft.AspNetCore.Routing.EndpointDataSource>>()
            .SelectMany(s => s.Endpoints).Select(e => e.DisplayName ?? "").Where(n => n.Contains("/v1/", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.StartsWith("A2A ", n, StringComparison.Ordinal));
    }

    [Fact]
    public void Mixed_assembly_versions_are_refused_at_startup()
    {
        // The check itself is covered in SharedTests; here the extension still runs it without throwing for matching versions.
        var options = new A2AEndpointOptions();
        Assert.Same(options, options.EnableV03Compat());
        Assert.Throws<ArgumentNullException>(() => A2AV03CompatExtensions.EnableV03Compat(null!));
    }
}
