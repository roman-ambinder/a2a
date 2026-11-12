using System.Net;
using System.Text.Json.Nodes;

namespace A2A.V0_3Compat.Tests;

/// <summary>The v0.3 JSON-RPC methods on the same URL as v1.0.</summary>
public class ServerJsonRpcTests
{
    private static async Task<JsonObject> SendAndGetTaskAsync(V03Host host, string text)
    {
        var (_, body) = await V03Host.RpcAsync(host.Http(), "message/send", V03Host.SendParams(text));
        return (JsonObject)body["result"]!;
    }

    [Fact]
    public async Task Message_send_returns_a_tagged_message()
    {
        await using var host = await V03Host.StartAsync();
        var (response, body) = await V03Host.RpcAsync(host.Http(), "message/send", V03Host.SendParams("hello"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2.0", body["jsonrpc"]!.GetValue<string>());
        Assert.Equal(1, body["id"]!.GetValue<int>());
        var result = body["result"]!;
        Assert.Equal("message", result["kind"]!.GetValue<string>());
        Assert.Equal("agent", result["role"]!.GetValue<string>());
        Assert.Equal("text", result["parts"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal("hi there", result["parts"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Message_send_is_blocking_by_default_and_returns_the_finished_task()
    {
        await using var host = await V03Host.StartAsync();
        var task = await SendAndGetTaskAsync(host, "task");
        Assert.Equal("task", task["kind"]!.GetValue<string>());
        Assert.Equal("completed", task["status"]!["state"]!.GetValue<string>());
        var parts = task["artifacts"]![0]!["parts"]!.AsArray();
        Assert.Equal(["part1", "part2"], parts.Select(p => p!["text"]!.GetValue<string>()));
        Assert.All(parts, p => Assert.Equal("text", p!["kind"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Blocking_false_returns_at_once_and_the_task_can_be_read_and_canceled()
    {
        await using var host = await V03Host.StartAsync();
        var http = host.Http();
        var (_, sent) = await V03Host.RpcAsync(http, "message/send", V03Host.SendParams("slow", blocking: false));
        var task = sent["result"]!;
        Assert.Equal("task", task["kind"]!.GetValue<string>());
        var id = task["id"]!.GetValue<string>();
        Assert.Contains(task["status"]!["state"]!.GetValue<string>(), new[] { "submitted", "working" });

        var (_, got) = await V03Host.RpcAsync(http, "tasks/get", new JsonObject { ["id"] = id, ["historyLength"] = 1 });
        Assert.Equal(id, got["result"]!["id"]!.GetValue<string>());
        Assert.Single(got["result"]!["history"]!.AsArray());

        var (_, canceled) = await V03Host.RpcAsync(http, "tasks/cancel", new JsonObject { ["id"] = id });
        Assert.Equal("canceled", canceled["result"]!["status"]!["state"]!.GetValue<string>());
        var (_, again) = await V03Host.RpcAsync(http, "tasks/get", new JsonObject { ["id"] = id });
        Assert.Equal("canceled", again["result"]!["status"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tasks_get_of_an_unknown_task_is_32001_without_data()
    {
        await using var host = await V03Host.StartAsync();
        var (response, body) = await V03Host.RpcAsync(host.Http(), "tasks/get", new JsonObject { ["id"] = "nope" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(-32001, body["error"]!["code"]!.GetValue<int>());
        Assert.NotNull(body["error"]!["message"]);
        Assert.Null(body["error"]!["data"]);
    }

    [Fact]
    public async Task Message_stream_sends_flat_tagged_events_wrapped_in_json_rpc_and_ends_on_final()
    {
        await using var host = await V03Host.StartAsync();
        using var response = await V03Host.StreamAsync(host.Http(), V03Host.RpcStream("message/stream", V03Host.SendParams("task")));
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var frames = await V03Host.ReadSseAsync(response);

        Assert.All(frames, f =>
        {
            Assert.Equal("2.0", f["jsonrpc"]!.GetValue<string>());
            Assert.Equal("s1", f["id"]!.GetValue<string>());
        });
        var kinds = frames.Select(f => f["result"]!["kind"]!.GetValue<string>()).ToList();
        Assert.Equal("task", kinds[0]);
        Assert.Contains("artifact-update", kinds);
        Assert.Equal("status-update", kinds[^1]);

        var updates = frames.Select(f => f["result"]!).Where(r => r["kind"]!.GetValue<string>() == "status-update").ToList();
        Assert.False(updates[0]["final"]!.GetValue<bool>());
        var last = updates[^1];
        Assert.Equal("completed", last["status"]!["state"]!.GetValue<string>());
        Assert.True(last["final"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_interrupted_task_ends_the_stream_with_final_true()
    {
        await using var host = await V03Host.StartAsync();
        using var response = await V03Host.StreamAsync(host.Http(), V03Host.RpcStream("message/stream", V03Host.SendParams("input")));
        var frames = await V03Host.ReadSseAsync(response); // returns: the core closed the stream
        var last = frames[^1]["result"]!;
        Assert.Equal("status-update", last["kind"]!.GetValue<string>());
        Assert.Equal("input-required", last["status"]!["state"]!.GetValue<string>());
        Assert.True(last["final"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Tasks_resubscribe_starts_with_a_task_snapshot()
    {
        await using var host = await V03Host.StartAsync();
        var http = host.Http();
        var (_, sent) = await V03Host.RpcAsync(http, "message/send", V03Host.SendParams("slow", blocking: false));
        var id = sent["result"]!["id"]!.GetValue<string>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await V03Host.StreamAsync(http, V03Host.RpcStream("tasks/resubscribe", new JsonObject { ["id"] = id }));
        var frames = await V03Host.ReadSseAsync(response, max: 1, cts.Token);
        Assert.Equal("task", frames[0]["result"]!["kind"]!.GetValue<string>());
        Assert.Equal(id, frames[0]["result"]!["id"]!.GetValue<string>());
        await V03Host.RpcAsync(http, "tasks/cancel", new JsonObject { ["id"] = id });
    }

    [Fact]
    public async Task Extended_card_is_a_pure_v03_card_that_says_no_push()
    {
        await using var host = await V03Host.StartAsync();
        var (_, body) = await V03Host.RpcAsync(host.Http("bob"), "agent/getAuthenticatedExtendedCard", null);
        var card = body["result"]!;
        Assert.Equal("0.3.0", card["protocolVersion"]!.GetValue<string>());
        Assert.Equal("Extended card for bob", card["description"]!.GetValue<string>());
        Assert.Equal(V03Host.BaseUrl, card["url"]!.GetValue<string>());
        Assert.False(card["capabilities"]!["pushNotifications"]!.GetValue<bool>());
        Assert.Null(card["supportedInterfaces"]); // pure v0.3: no v1.0 members
        Assert.Equal("http", card["securitySchemes"]!["bearer"]!["type"]!.GetValue<string>());
        Assert.Equal("[{\"bearer\":[]}]", card["security"]!.ToJsonString());
    }

    [Fact]
    public async Task Extended_card_leaves_out_v1_signatures_that_do_not_cover_the_v03_document()
    {
        // A JWS over the v1.0 extended card cannot verify the v0.3 document served, so a verifying v0.3 client would reject it.
        await using var host = await V03Host.StartAsync(configure: o =>
        {
            var inner = o.ExtendedAgentCardProvider!;
            o.ExtendedAgentCardProvider = async (call, card, ct) =>
            {
                var extended = await inner(call, card, ct);
                extended!.Signatures = [new AgentCardSignature { Protected = "eyJhbGciOiJFUzI1NiJ9", Signature = "c2ln" }];
                return extended;
            };
        });
        var (_, body) = await V03Host.RpcAsync(host.Http("bob"), "agent/getAuthenticatedExtendedCard", null);
        var card = body["result"]!;
        Assert.Equal("Extended card for bob", card["description"]!.GetValue<string>());
        Assert.Null(card["signatures"]);
    }

    [Theory]
    [InlineData("tasks/pushNotificationConfig/set")]
    [InlineData("tasks/pushNotificationConfig/get")]
    [InlineData("tasks/pushNotificationConfig/list")]
    [InlineData("tasks/pushNotificationConfig/delete")]
    public async Task Push_config_methods_are_refused_with_32003(string method)
    {
        await using var host = await V03Host.StartAsync();
        var (response, body) = await V03Host.RpcAsync(host.Http(), method, new JsonObject { ["id"] = "t", ["taskId"] = "t" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(-32003, body["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_push_config_in_a_send_is_refused_with_32003_for_send_and_stream()
    {
        await using var host = await V03Host.StartAsync();
        var push = new JsonObject
        {
            ["configuration"] = new JsonObject { ["pushNotificationConfig"] = new JsonObject { ["url"] = "https://hooks.example/x" } },
        };
        var (_, body) = await V03Host.RpcAsync(host.Http(), "message/send", V03Host.SendParams("hello", extra: push));
        Assert.Equal(-32003, body["error"]!["code"]!.GetValue<int>());

        using var response = await V03Host.StreamAsync(host.Http(), V03Host.RpcStream("message/stream", V03Host.SendParams("hello", extra: push)));
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("-32003", text);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType); // refused before the stream started
    }

    [Fact]
    public async Task Tasks_list_does_not_exist_in_v03_and_is_32601()
    {
        await using var host = await V03Host.StartAsync();
        var (_, body) = await V03Host.RpcAsync(host.Http(), "tasks/list", new JsonObject());
        Assert.Equal(-32601, body["error"]!["code"]!.GetValue<int>());
        // Any other method is the core's: with a version it is not found, without one the core refuses the version first.
        var (_, unknown) = await V03Host.RpcAsync(host.Http(), "message/unknown", new JsonObject(), version: "1.0");
        Assert.Equal(-32601, unknown["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Bad_params_are_32602()
    {
        await using var host = await V03Host.StartAsync();
        var (_, missing) = await V03Host.RpcAsync(host.Http(), "message/send", new JsonObject());
        Assert.Equal(-32602, missing["error"]!["code"]!.GetValue<int>());
        var (_, badKind) = await V03Host.RpcAsync(host.Http(), "message/send", new JsonObject
        {
            ["message"] = new JsonObject { ["messageId"] = "m", ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["kind"] = "video" }) },
        });
        Assert.Equal(-32602, badKind["error"]!["code"]!.GetValue<int>());
        var (_, noParams) = await V03Host.RpcAsync(host.Http(), "tasks/get", null);
        Assert.Equal(-32602, noParams["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_request_id_comes_back_as_sent()
    {
        await using var host = await V03Host.StartAsync();
        var (_, body) = await V03Host.RpcAsync(host.Http(), "tasks/get", new JsonObject { ["id"] = "x" }, id: "abc-1");
        Assert.Equal("abc-1", body["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_version_header_of_1_0_vetoes_a_v03_method()
    {
        await using var host = await V03Host.StartAsync();
        var (response, body) = await V03Host.RpcAsync(host.Http(), "message/send", V03Host.SendParams("hello"), version: "1.0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(-32009, body["error"]!["code"]!.GetValue<int>());
        var (_, ok) = await V03Host.RpcAsync(host.Http(), "message/send", V03Host.SendParams("hello"), version: "0.3");
        Assert.NotNull(ok["result"]);
    }

    [Fact]
    public async Task Extensions_header_of_v03_clients_is_honored_and_echoed()
    {
        await using var host = await V03Host.StartAsync();
        using var message = new HttpRequestMessage(HttpMethod.Post, "/a2a")
        {
            Content = V03Host.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "message/send", ["params"] = V03Host.SendParams("hello") }.ToJsonString()),
        };
        message.Headers.Add("X-A2A-Extensions", V03Host.OptionalExtension);
        using var response = await host.Http().SendAsync(message);
        Assert.Equal(V03Host.OptionalExtension, response.Headers.GetValues("X-A2A-Extensions").Single());
        Assert.Equal(V03Host.OptionalExtension, response.Headers.GetValues("A2A-Extensions").Single());
    }

    [Fact]
    public async Task Tenants_come_only_from_the_route()
    {
        await using var host = await V03Host.StartAsync();
        var http = host.Http();
        var (_, sent) = await V03Host.RpcAsync(http, "message/send", V03Host.SendParams("task"), path: "/a2a/acme");
        var id = sent["result"]!["id"]!.GetValue<string>();
        var (_, own) = await V03Host.RpcAsync(http, "tasks/get", new JsonObject { ["id"] = id }, path: "/a2a/acme");
        Assert.Equal(id, own["result"]!["id"]!.GetValue<string>());
        var (_, other) = await V03Host.RpcAsync(http, "tasks/get", new JsonObject { ["id"] = id }, path: "/a2a");
        Assert.Equal(-32001, other["error"]!["code"]!.GetValue<int>());
        var (_, wrongTenant) = await V03Host.RpcAsync(http, "tasks/get", new JsonObject { ["id"] = id, ["tenant"] = "acme" }, path: "/a2a");
        Assert.Equal(-32001, wrongTenant["error"]!["code"]!.GetValue<int>()); // a body "tenant" is never read
    }

    [Fact]
    public async Task Another_user_cannot_read_the_task()
    {
        await using var host = await V03Host.StartAsync();
        var task = await SendAndGetTaskAsync(host, "task");
        var (_, body) = await V03Host.RpcAsync(host.Http("mallory"), "tasks/get", new JsonObject { ["id"] = task["id"]!.GetValue<string>() });
        Assert.Equal(-32001, body["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_v03_surface_counts_requests()
    {
        using var recorder = new MeterRecorder();
        await using var host = await V03Host.StartAsync();
        await V03Host.RpcAsync(host.Http(), "message/send", V03Host.SendParams("hello"));
        Assert.Contains(("JSONRPC", "message/send"), recorder.Requests());
    }
}
