using System.Net;
using System.Text.Json.Nodes;
using Google.Protobuf;
using P = A2a.V1;

namespace A2A.V0_3Compat.Tests;

/// <summary>The v0.3 HTTP+JSON routes under <c>{pattern}/v1</c>. Bodies are checked with the generated classes of the v0.3 proto.</summary>
public class ServerRestTests
{
    private static readonly JsonParser Strict = new(JsonParser.Settings.Default);

    private static async Task<(HttpResponseMessage Response, string Text)> SendAsync(HttpClient http, HttpMethod method, string path, string? body = null, string? version = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = V03Host.Json(body);
        if (version is not null) request.Headers.Add("A2A-Version", version);
        var response = await http.SendAsync(request);
        return (response, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> TaskIdAsync(HttpClient http, string text, string prefix = "/a2a")
    {
        var (_, body) = await SendAsync(http, HttpMethod.Post, prefix + "/v1/message:send", V03Host.RestSendBody(text, blocking: false));
        return Strict.Parse<P.SendMessageResponse>(body).Task.Id;
    }

    [Fact]
    public async Task Message_send_returns_a_wrapped_message_that_parses_with_the_v03_proto()
    {
        await using var host = await V03Host.StartAsync();
        var (response, text) = await SendAsync(host.Http(), HttpMethod.Post, "/a2a/v1/message:send", V03Host.RestSendBody("hello"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var proto = Strict.Parse<P.SendMessageResponse>(text);
        Assert.Equal(P.SendMessageResponse.PayloadOneofCase.Msg, proto.PayloadCase);
        Assert.Equal(P.Role.Agent, proto.Msg.Role);
        Assert.Equal("hi there", proto.Msg.Content[0].Text);
        Assert.Contains("ROLE_AGENT", text);
        Assert.DoesNotContain("\"kind\"", text);
    }

    [Fact]
    public async Task Message_send_returns_the_finished_task_when_blocking()
    {
        await using var host = await V03Host.StartAsync();
        var (_, text) = await SendAsync(host.Http(), HttpMethod.Post, "/a2a/v1/message:send", V03Host.RestSendBody("task"));
        var task = Strict.Parse<P.SendMessageResponse>(text).Task;
        Assert.Equal(P.TaskState.Completed, task.Status.State);
        Assert.Equal(["part1", "part2"], task.Artifacts[0].Parts.Select(p => p.Text));
    }

    [Fact]
    public async Task Absent_blocking_means_blocking_and_false_returns_at_once()
    {
        await using var host = await V03Host.StartAsync();
        var http = host.Http();
        var (_, blocking) = await SendAsync(http, HttpMethod.Post, "/a2a/v1/message:send", V03Host.RestSendBody("task", blocking: null));
        Assert.Equal(P.TaskState.Completed, Strict.Parse<P.SendMessageResponse>(blocking).Task.Status.State);
        var (_, immediate) = await SendAsync(http, HttpMethod.Post, "/a2a/v1/message:send", V03Host.RestSendBody("slow", blocking: false));
        Assert.NotEqual(P.TaskState.Completed, Strict.Parse<P.SendMessageResponse>(immediate).Task.Status.State);
    }

    [Fact]
    public async Task Proto_field_names_in_the_request_are_accepted()
    {
        await using var host = await V03Host.StartAsync();
        var body = """{"message":{"message_id":"m1","role":"ROLE_USER","content":[{"text":"hello"}]},"configuration":{"history_length":1,"blocking":true}}""";
        var (response, text) = await SendAsync(host.Http(), HttpMethod.Post, "/a2a/v1/message:send", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hi there", Strict.Parse<P.SendMessageResponse>(text).Msg.Content[0].Text);
    }

    [Fact]
    public async Task Get_cancel_and_subscribe_use_the_task_id_of_the_route()
    {
        await using var host = await V03Host.StartAsync();
        var http = host.Http();
        var id = await TaskIdAsync(http, "slow");

        var (getResponse, getText) = await SendAsync(http, HttpMethod.Get, $"/a2a/v1/tasks/{id}?historyLength=1");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var task = Strict.Parse<P.Task>(getText);
        Assert.Equal(id, task.Id);
        Assert.Single(task.History);
        var (_, snake) = await SendAsync(http, HttpMethod.Get, $"/a2a/v1/tasks/{id}?history_length=1");
        Assert.Single(Strict.Parse<P.Task>(snake).History);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var subscribe = await V03Host.StreamAsync(http, new HttpRequestMessage(HttpMethod.Get, $"/a2a/v1/tasks/{id}:subscribe"));
        var first = (await V03Host.ReadSseAsync(subscribe, max: 1, cts.Token))[0];
        Assert.Equal(id, Strict.Parse<P.StreamResponse>(first.ToJsonString()).Task.Id);

        var (cancelResponse, cancelText) = await SendAsync(http, HttpMethod.Post, $"/a2a/v1/tasks/{id}:cancel", "{}");
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);
        Assert.Equal(P.TaskState.Cancelled, Strict.Parse<P.Task>(cancelText).Status.State);
    }

    [Fact]
    public async Task Subscribe_also_works_with_post()
    {
        await using var host = await V03Host.StartAsync();
        var http = host.Http();
        var id = await TaskIdAsync(http, "slow");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var subscribe = await V03Host.StreamAsync(http, new HttpRequestMessage(HttpMethod.Post, $"/a2a/v1/tasks/{id}:subscribe"));
        Assert.Equal(P.StreamResponse.PayloadOneofCase.Task, Strict.Parse<P.StreamResponse>((await V03Host.ReadSseAsync(subscribe, 1, cts.Token))[0].ToJsonString()).PayloadCase);
        await SendAsync(http, HttpMethod.Post, $"/a2a/v1/tasks/{id}:cancel", "{}");
    }

    [Fact]
    public async Task Message_stream_sends_wrapped_events_that_parse_as_proto_stream_responses()
    {
        await using var host = await V03Host.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/a2a/v1/message:stream") { Content = V03Host.Json(V03Host.RestSendBody("task")) };
        using var response = await V03Host.StreamAsync(host.Http(), request);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var frames = await V03Host.ReadSseAsync(response);
        var events = frames.Select(f => Strict.Parse<P.StreamResponse>(f.ToJsonString())).ToList();

        Assert.Equal(P.StreamResponse.PayloadOneofCase.Task, events[0].PayloadCase);
        Assert.Contains(events, e => e.PayloadCase == P.StreamResponse.PayloadOneofCase.ArtifactUpdate);
        var last = events[^1];
        Assert.Equal(P.StreamResponse.PayloadOneofCase.StatusUpdate, last.PayloadCase);
        Assert.Equal(P.TaskState.Completed, last.StatusUpdate.Status.State);
        Assert.True(last.StatusUpdate.Final);
        Assert.All(frames, f => Assert.Null(f["jsonrpc"])); // no JSON-RPC envelope
    }

    [Fact]
    public async Task An_interrupted_task_ends_the_rest_stream_with_final_true()
    {
        await using var host = await V03Host.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/a2a/v1/message:stream") { Content = V03Host.Json(V03Host.RestSendBody("input")) };
        using var response = await V03Host.StreamAsync(host.Http(), request);
        var last = Strict.Parse<P.StreamResponse>((await V03Host.ReadSseAsync(response))[^1].ToJsonString());
        Assert.Equal(P.TaskState.InputRequired, last.StatusUpdate.Status.State);
        Assert.True(last.StatusUpdate.Final);
    }

    [Fact]
    public async Task Card_route_returns_the_pure_v03_extended_card()
    {
        await using var host = await V03Host.StartAsync();
        var (response, text) = await SendAsync(host.Http("bob"), HttpMethod.Get, "/a2a/v1/card");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var card = JsonNode.Parse(text)!;
        // The JSON-RPC dialect (a2a-python #1418): camelCase members and flat schemes, not ProtoJSON.
        Assert.Equal("0.3.0", card["protocolVersion"]!.GetValue<string>());
        Assert.Equal("Extended card for bob", card["description"]!.GetValue<string>());
        Assert.Equal("http", card["securitySchemes"]!["bearer"]!["type"]!.GetValue<string>());
        Assert.False(card["capabilities"]!["pushNotifications"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("POST", "/a2a/v1/tasks/t1/pushNotificationConfigs")]
    [InlineData("GET", "/a2a/v1/tasks/t1/pushNotificationConfigs")]
    [InlineData("GET", "/a2a/v1/tasks/t1/pushNotificationConfigs/c1")]
    [InlineData("DELETE", "/a2a/v1/tasks/t1/pushNotificationConfigs/c1")]
    public async Task Push_config_routes_are_refused_with_32003(string method, string path)
    {
        await using var host = await V03Host.StartAsync();
        var (response, text) = await SendAsync(host.Http(), new HttpMethod(method), path, method == "POST" ? "{}" : null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(-32003, JsonNode.Parse(text)!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_push_config_in_a_send_is_refused()
    {
        await using var host = await V03Host.StartAsync();
        var body = """{"message":{"messageId":"m","role":"ROLE_USER","content":[{"text":"hello"}]},"configuration":{"pushNotification":{"url":"https://hooks.example/x"}}}""";
        var (response, text) = await SendAsync(host.Http(), HttpMethod.Post, "/a2a/v1/message:send", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(-32003, JsonNode.Parse(text)!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Errors_have_the_core_status_and_a_code_and_message_body()
    {
        await using var host = await V03Host.StartAsync();
        var (response, text) = await SendAsync(host.Http(), HttpMethod.Get, "/a2a/v1/tasks/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(-32001, body["code"]!.GetValue<int>());
        Assert.NotNull(body["message"]);
        Assert.Equal(2, body.Count); // no google.rpc status, no details

        var (badResponse, badText) = await SendAsync(host.Http(), HttpMethod.Post, "/a2a/v1/message:send", "{\"message\":{}}");
        Assert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode);
        Assert.Equal(-32602, JsonNode.Parse(badText)!["code"]!.GetValue<int>());
        var (emptyResponse, _) = await SendAsync(host.Http(), HttpMethod.Post, "/a2a/v1/message:send", "");
        Assert.Equal(HttpStatusCode.BadRequest, emptyResponse.StatusCode);
    }

    [Fact]
    public async Task A_version_header_of_1_0_vetoes_the_v03_routes_with_400()
    {
        await using var host = await V03Host.StartAsync();
        var (response, text) = await SendAsync(host.Http(), HttpMethod.Post, "/a2a/v1/message:send", V03Host.RestSendBody("hello"), version: "1.0");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(-32009, JsonNode.Parse(text)!["code"]!.GetValue<int>());
        var (ok, _) = await SendAsync(host.Http(), HttpMethod.Post, "/a2a/v1/message:send", V03Host.RestSendBody("hello"), version: "0.3");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Tenant_routes_scope_tasks_to_the_tenant()
    {
        await using var host = await V03Host.StartAsync();
        var http = host.Http();
        var id = await TaskIdAsync(http, "slow", "/a2a/acme");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(http, HttpMethod.Get, $"/a2a/acme/v1/tasks/{id}")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(http, HttpMethod.Get, $"/a2a/v1/tasks/{id}")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(http, HttpMethod.Get, $"/a2a/other/v1/tasks/{id}")).Response.StatusCode);
        await SendAsync(http, HttpMethod.Post, $"/a2a/acme/v1/tasks/{id}:cancel", "{}");
    }

    [Fact]
    public async Task Path_tenant_patterns_map_the_routes_below_the_tenant_segment()
    {
        await using var host = await V03Host.StartAsync(pattern: "/{tenant}/a2a");
        var (response, text) = await SendAsync(host.Http(), HttpMethod.Post, "/acme/a2a/v1/message:send", V03Host.RestSendBody("hello"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("hi there", text);
    }

    [Fact]
    public async Task The_v03_surface_counts_rest_requests()
    {
        using var recorder = new MeterRecorder();
        await using var host = await V03Host.StartAsync();
        await SendAsync(host.Http(), HttpMethod.Post, "/a2a/v1/message:send", V03Host.RestSendBody("hello"));
        Assert.Contains(("HTTP+JSON", "message:send"), recorder.Requests());
    }
}
