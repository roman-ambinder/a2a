using System.Net;
using System.Text;
using System.Text.Json;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Json;
using A2A.Net;

namespace A2A.Tests;

/// <summary>Regressions for problems reported against other A2A SDKs (see the issue links on each test).</summary>
public class InteropHardeningTests
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static HttpRequestMessage Post(string url, string body, string user = "alice")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = Json(body) };
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        req.Headers.Add("X-Test-User", user);
        return req;
    }

    // ---- ProtoJSON proto field names (A2A #2087, a2a-tck #242) ----

    [Fact]
    public void Normalizer_renames_proto_names_but_not_free_form_keys()
    {
        using var doc = JsonDocument.Parse("""
            {"message":{"message_id":"m","context_id":"c","metadata":{"my_key":{"inner_key":1}},
              "parts":[{"data":{"snake_case":true}},{"text":"t","media_type":"text/plain"}]},
             "configuration":{"history_length":2,"return_immediately":true}}
            """);
        var n = ProtoJsonFieldNames.Normalize(doc.RootElement);
        var m = n.GetProperty("message");
        Assert.Equal("m", m.GetProperty("messageId").GetString());
        Assert.Equal("c", m.GetProperty("contextId").GetString());
        Assert.Equal(1, m.GetProperty("metadata").GetProperty("my_key").GetProperty("inner_key").GetInt32());
        Assert.True(m.GetProperty("parts")[0].GetProperty("data").GetProperty("snake_case").GetBoolean());
        Assert.Equal("text/plain", m.GetProperty("parts")[1].GetProperty("mediaType").GetString());
        Assert.Equal(2, n.GetProperty("configuration").GetProperty("historyLength").GetInt32());
    }

    [Fact]
    public void Normalizer_returns_input_unchanged_without_proto_names()
    {
        using var doc = JsonDocument.Parse("""{"message":{"messageId":"m","metadata":{"a_b":1}}}""");
        Assert.Equal(doc.RootElement.GetRawText(), ProtoJsonFieldNames.Normalize(doc.RootElement).GetRawText());
    }

    [Fact]
    public void Normalizer_rejects_both_spellings_of_one_field()
    {
        using var doc = JsonDocument.Parse("""{"historyLength":1,"history_length":2}""");
        Assert.Throws<JsonException>(() => ProtoJsonFieldNames.Normalize(doc.RootElement));
    }

    [Theory]
    [InlineData("history_length", "historyLength")]
    [InlineData("push_notification_config", "pushNotificationConfig")]
    [InlineData("id", "id")]
    public void Proto_name_to_json_name(string proto, string json) => Assert.Equal(json, ProtoJsonFieldNames.ToJsonName(proto));

    [Fact]
    public async Task Json_rpc_accepts_proto_field_names()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var http = host.Http();
        var resp = await http.SendAsync(Post(TestAgentHost.BaseUrl, """
            {"jsonrpc":"2.0","id":1,"method":"SendMessage","params":
              {"message":{"message_id":"snake-1","role":"ROLE_USER","parts":[{"text":"task"}]},"configuration":{"history_length":0}}}
            """));
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var task = doc.RootElement.GetProperty("result").GetProperty("task");
        Assert.Equal("TASK_STATE_COMPLETED", task.GetProperty("status").GetProperty("state").GetString());
        Assert.False(task.TryGetProperty("history", out _)); // history_length = 0 was honored
    }

    [Fact]
    public async Task Json_rpc_rejects_both_spellings()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var http = host.Http();
        var resp = await http.SendAsync(Post(TestAgentHost.BaseUrl, """
            {"jsonrpc":"2.0","id":1,"method":"GetTask","params":{"id":"x","historyLength":1,"history_length":2}}
            """));
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(-32602, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Rest_accepts_proto_names_in_body_and_query()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var http = host.Http();
        var send = await http.SendAsync(Post("/a2a/message:send",
            """{"message":{"message_id":"snake-2","role":"ROLE_USER","parts":[{"text":"task"}]}}"""));
        send.EnsureSuccessStatusCode();
        using var sent = JsonDocument.Parse(await send.Content.ReadAsStringAsync());
        var id = sent.RootElement.GetProperty("task").GetProperty("id").GetString();

        var get = new HttpRequestMessage(HttpMethod.Get, $"/a2a/tasks/{id}?history_length=0");
        get.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        get.Headers.Add("X-Test-User", "alice");
        var got = await http.SendAsync(get);
        got.EnsureSuccessStatusCode();
        using var task = JsonDocument.Parse(await got.Content.ReadAsStringAsync());
        Assert.False(task.RootElement.TryGetProperty("history", out _));
    }

    // ---- Errors inside REST streams (A2A #1262) ----

    private static async IAsyncEnumerable<A2A.Server.Streaming.SequencedEvent> OneEventThenFail()
    {
        await Task.Yield();
        yield return new(1, StreamResponse.Of(new AgentTask { Id = "t", ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working } }));
        throw new A2AException(A2AErrorCode.Internal);
    }

    private static async Task<string> PumpWithError(string? errorEventType)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var body = new MemoryStream();
        http.Response.Body = body;
        var primed = await A2A.Server.AspNetCore.HttpPlumbing.PrimeAsync(OneEventThenFail(), TimeSpan.Zero, null, CancellationToken.None);
        await A2A.Server.AspNetCore.HttpPlumbing.PumpAsync(http, primed!, A2AJson.SerializeToUtf8Bytes,
            _ => Encoding.UTF8.GetBytes("""{"error":{"code":13}}"""), TimeSpan.Zero,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, errorEventType);
        return Encoding.UTF8.GetString(body.ToArray());
    }

    [Fact]
    public async Task Stream_error_frame_is_named_when_asked() =>
        Assert.EndsWith("event: error\ndata: {\"error\":{\"code\":13}}\n\n", await PumpWithError("error"));

    [Fact]
    public async Task Stream_error_frame_is_unnamed_by_default() =>
        Assert.DoesNotContain("event:", await PumpWithError(null));

    // ---- Client stream robustness (A2A #2269, #1262) ----

    private sealed class SseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, A2AProtocol.EventStreamMediaType),
            });
    }

    private const string Status = """{"taskId":"t","contextId":"c","status":{"state":"TASK_STATE_COMPLETED"}}""";

    [Fact]
    public async Task Rest_client_skips_unknown_event_types()
    {
        var sse = "data: {\"futureEvent\":{\"x\":1}}\n\n" + $"data: {{\"statusUpdate\":{Status}}}\n\n";
        var transport = new HttpJsonTransport(new HttpClient(new SseHandler(sse)), new Uri("http://agent.test/"));
        var events = new List<StreamResponse>();
        await foreach (var e in transport.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = "t" })) events.Add(e);
        Assert.IsType<StatusUpdateEvent>(Assert.Single(events));
    }

    [Fact]
    public async Task Json_rpc_client_skips_unknown_event_types()
    {
        var sse = "data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"futureEvent\":{}}}\n\n"
                  + $"data: {{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{{\"statusUpdate\":{Status}}}}}\n\n";
        var transport = new JsonRpcTransport(new HttpClient(new SseHandler(sse)), new Uri("http://agent.test/"));
        var events = new List<StreamResponse>();
        await foreach (var e in transport.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = "t" })) events.Add(e);
        Assert.IsType<StatusUpdateEvent>(Assert.Single(events));
    }

    [Fact]
    public async Task Rest_client_reads_named_error_event_with_bare_status()
    {
        var sse = """
            event: error
            data: {"code":5,"message":"gone","details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"TASK_NOT_FOUND","domain":"a2a-protocol.org"}]}


            """.Replace("\r\n", "\n");
        var transport = new HttpJsonTransport(new HttpClient(new SseHandler(sse)), new Uri("http://agent.test/"));
        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var _ in transport.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = "t" })) { }
        });
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
    }

    // ---- Redirects (a2a-java CVE-2026-111763) ----

    private sealed class RedirectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // What an auto-redirecting handler reports: the request now points at the redirect target.
            request.RequestUri = new Uri("https://evil.test/collect");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("""{"id":"t","contextId":"c","status":{"state":"TASK_STATE_COMPLETED"}}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task Client_refuses_redirected_responses()
    {
        var transport = new HttpJsonTransport(new HttpClient(new RedirectingHandler()), new Uri("https://agent.test/a2a"));
        var ex = await Assert.ThrowsAsync<A2AException>(() => transport.GetTaskAsync(new GetTaskRequest { Id = "t" }, new A2ARequestOptions { MaxRetries = 0 }));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
        Assert.Contains("https://evil.test", ex.Message);
    }

    // ---- SSRF-safe fetch of peer-chosen URLs (spec §14.1.1) ----

    [Theory]
    [InlineData("http://files.example/a")]          // http not allowed by default
    [InlineData("https://127.0.0.1/a")]
    [InlineData("https://169.254.169.254/latest")]
    [InlineData("https://[::1]/a")]
    [InlineData("https://2130706433/a")]           // decimal 127.0.0.1
    [InlineData("https://user:pw@files.example/a")]
    public void Safe_http_refuses_unsafe_urls(string url) =>
        Assert.Throws<HttpRequestException>(() => SafeHttp.EnsureAllowed(new Uri(url)));

    [Fact]
    public void Safe_http_allows_public_https() => SafeHttp.EnsureAllowed(new Uri("https://files.example/a"));

    [Fact]
    public async Task Safe_http_blocks_names_that_resolve_to_private_addresses()
    {
        using var http = SafeHttp.CreateClient();
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => SafeHttp.DownloadAsync(http, new Uri("https://localhost:1/x"), 1024));
    }
}
