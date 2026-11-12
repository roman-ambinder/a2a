using System.Net;
using System.Text.Json.Nodes;
using A2A.Client;
using A2A.Client.V0_3Compat;
using static A2A.V0_3Compat.Tests.ClientData;

namespace A2A.V0_3Compat.Tests;

/// <summary>The v0.3 JSON-RPC transport against a fake v0.3 agent. Every operation, the stream rules, and error mapping.</summary>
public class ClientJsonRpcTests
{
    private static readonly Uri Endpoint = new("http://agent.test/rpc");

    private static (V03JsonRpcTransport Transport, FakeV03Server Server) Make(Func<SeenRequest, HttpResponseMessage> respond, Action<A2AClientOptions>? configure = null)
    {
        var server = new FakeV03Server(respond);
        var options = new A2AClientOptions { MaxRetries = 0 };
        configure?.Invoke(options);
        return (new V03JsonRpcTransport(server.Client(), Endpoint, options), server);
    }

    private static async Task<List<StreamResponse>> ToListAsync(IAsyncEnumerable<StreamResponse> source)
    {
        var list = new List<StreamResponse>();
        await foreach (var item in source) list.Add(item);
        return list;
    }

    // ---- send ----

    [Fact]
    public async Task Send_writes_a_v03_request_and_reads_the_task()
    {
        var (transport, server) = Make(_ => FakeV03Server.Rpc(JsonNode.Parse(Golden.Text("task.json"))));
        var response = await transport.SendMessageAsync(Send("hi"));

        var task = Assert.IsType<TaskResponse>(response).Task;
        Assert.Equal("task-1", task.Id);
        Assert.Equal(TaskState.InputRequired, task.Status.State);
        Assert.Equal("result", task.Artifacts![0].Name);

        var request = server.Last;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/rpc", request.PathAndQuery);
        var json = request.Json!;
        Assert.Equal("2.0", (string?)json["jsonrpc"]);
        Assert.Equal("message/send", (string?)json["method"]);
        var message = json["params"]!["message"]!;
        Assert.Equal("message", (string?)message["kind"]);
        Assert.Equal("user", (string?)message["role"]);
        Assert.Equal("text", (string?)message["parts"]![0]!["kind"]);
        Assert.Equal("hi", (string?)message["parts"]![0]!["text"]);
        Assert.Equal("application/json", request.Header("content:Content-Type")!.Split(';')[0]);
    }

    [Fact]
    public async Task Send_reads_a_message_result()
    {
        var reply = new JsonObject
        {
            ["kind"] = "message", ["messageId"] = "r1", ["role"] = "agent",
            ["parts"] = new JsonArray(new JsonObject { ["kind"] = "text", ["text"] = "hello" }),
        };
        var (transport, _) = Make(_ => FakeV03Server.Rpc(reply));
        var response = await transport.SendMessageAsync(Send("hi"));
        Assert.Equal("hello", Assert.IsType<TextPart>(Assert.IsType<MessageResponse>(response).Message.Parts[0]).Text);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Blocking_is_always_written_and_return_immediately_means_false(bool? returnImmediately, bool expectedBlocking)
    {
        var (transport, server) = Make(_ => FakeV03Server.Rpc(JsonNode.Parse(TaskJson())));
        await transport.SendMessageAsync(Send("hi", returnImmediately));
        var configuration = server.Last.Json!["params"]!["configuration"]!;
        Assert.Equal(expectedBlocking, (bool?)configuration["blocking"]);
    }

    [Fact]
    public async Task Headers_say_v03_and_extensions_use_the_v03_name()
    {
        var (transport, server) = Make(_ => FakeV03Server.Rpc(JsonNode.Parse(TaskJson())), o => o.Extensions.Add("urn:a"));
        await transport.SendMessageAsync(Send("hi"), new A2ARequestOptions { Extensions = ["urn:b"] });
        var request = server.Last;
        Assert.Equal("0.3", request.Header("A2A-Version"));
        Assert.Equal("urn:a,urn:b", request.Header("X-A2A-Extensions"));
        Assert.Null(request.Header("A2A-Extensions"));
        Assert.NotNull(request.Header("Idempotency-Key"));
    }

    [Fact]
    public async Task No_extensions_header_without_extensions()
    {
        var (transport, server) = Make(_ => FakeV03Server.Rpc(JsonNode.Parse(TaskJson())));
        await transport.SendMessageAsync(Send("hi"));
        Assert.Null(server.Last.Header("X-A2A-Extensions"));
    }

    [Fact]
    public async Task The_caller_can_override_the_version_header_per_call()
    {
        var (transport, server) = Make(_ => FakeV03Server.Rpc(JsonNode.Parse(TaskJson())));
        await transport.SendMessageAsync(Send("hi"), new A2ARequestOptions { Headers = { ["A2A-Version"] = "0.3.1" } });
        Assert.Equal("0.3.1", server.Last.Header("A2A-Version"));
    }

    [Fact]
    public async Task The_transport_speaks_v03_even_when_given_v1_options()
    {
        var options = new A2AClientOptions { ProtocolVersion = "1.0" };
        var server = new FakeV03Server(_ => FakeV03Server.Rpc(JsonNode.Parse(TaskJson())));
        var transport = new V03JsonRpcTransport(server.Client(), Endpoint, options);
        await transport.GetTaskAsync(new GetTaskRequest { Id = "t1" });
        Assert.Equal("0.3", server.Last.Header("A2A-Version"));
        Assert.Equal("1.0", options.ProtocolVersion); // the caller's object is untouched
    }

    // ---- streams ----

    [Fact]
    public async Task Stream_reads_the_events_and_stops_on_final_even_if_the_connection_stays_open()
    {
        var (transport, server) = Make(_ => FakeV03Server.SseThenHang(
            FakeV03Server.RpcEvent(JsonNode.Parse(TaskJson())!).ToJsonString(),
            FakeV03Server.RpcEvent(JsonNode.Parse(StatusUpdateJson("working", final: false))!).ToJsonString(),
            FakeV03Server.RpcEvent(JsonNode.Parse(StatusUpdateJson("completed", final: true))!).ToJsonString()));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = await ToListAsync(transport.SendStreamingMessageAsync(Send("hi"), cancellationToken: cts.Token));

        Assert.Equal(3, events.Count);
        Assert.IsType<TaskEvent>(events[0]);
        Assert.Equal(TaskState.Working, Assert.IsType<StatusUpdateEvent>(events[1]).StatusUpdate.Status.State);
        Assert.Equal(TaskState.Completed, Assert.IsType<StatusUpdateEvent>(events[2]).StatusUpdate.Status.State);
        Assert.Equal("message/stream", (string?)server.Last.Json!["method"]);
        Assert.Equal("text/event-stream", server.Last.Header("Accept")!.Split(',')[0]);
    }

    [Fact]
    public async Task Events_after_final_are_not_read()
    {
        var (transport, _) = Make(_ => FakeV03Server.Sse(
            FakeV03Server.RpcEvent(JsonNode.Parse(StatusUpdateJson("completed", final: true))!).ToJsonString(),
            "this is not json"));
        var events = await ToListAsync(transport.SendStreamingMessageAsync(Send("hi")));
        Assert.Single(events);
    }

    [Fact]
    public async Task A_stream_without_final_ends_when_the_connection_ends()
    {
        var (transport, _) = Make(_ => FakeV03Server.Sse(
            FakeV03Server.RpcEvent(JsonNode.Parse(TaskJson())!).ToJsonString(),
            FakeV03Server.RpcEvent(JsonNode.Parse(StatusUpdateJson("working", final: false))!).ToJsonString()));
        var events = await ToListAsync(transport.SendStreamingMessageAsync(Send("hi")));
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task Stream_reads_message_and_artifact_events()
    {
        var artifact = new JsonObject
        {
            ["kind"] = "artifact-update", ["taskId"] = "t1", ["contextId"] = "c1", ["lastChunk"] = true,
            ["artifact"] = JsonNode.Parse(Golden.Text("artifact-update.json"))!["artifact"]!.DeepClone(),
        };
        var message = new JsonObject
        {
            ["kind"] = "message", ["messageId"] = "r1", ["role"] = "agent",
            ["parts"] = new JsonArray(new JsonObject { ["kind"] = "text", ["text"] = "x" }),
        };
        var (transport, _) = Make(_ => FakeV03Server.Sse(FakeV03Server.RpcEvent(artifact).ToJsonString(), FakeV03Server.RpcEvent(message).ToJsonString()));
        var events = await ToListAsync(transport.SendStreamingMessageAsync(Send("hi")));
        Assert.IsType<ArtifactUpdateEvent>(events[0]);
        Assert.IsType<MessageEvent>(events[1]);
    }

    [Fact]
    public async Task Subscribe_sends_tasks_resubscribe_and_sends_last_event_id_on_resume()
    {
        var (transport, server) = Make(_ => FakeV03Server.Sse(FakeV03Server.RpcEvent(JsonNode.Parse(StatusUpdateJson("completed", true))!).ToJsonString()));
        var events = await ToListAsync(transport.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = "t1" }));
        Assert.Single(events);
        Assert.Equal("tasks/resubscribe", (string?)server.Last.Json!["method"]);
        Assert.Equal("t1", (string?)server.Last.Json!["params"]!["id"]);

        var resumable = (A2A.Client.Streaming.IResumableTransport)transport;
        await foreach (var _ in resumable.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = "t1" }, null, "t1:4", default)) { }
        Assert.Equal("t1:4", server.Last.Header("Last-Event-ID"));
    }

    [Fact]
    public async Task A_json_error_instead_of_a_stream_is_thrown_as_that_error()
    {
        var (transport, _) = Make(_ => FakeV03Server.RpcError(-32001, "no such task"));
        var ex = await Assert.ThrowsAsync<A2AException>(() => ToListAsync(transport.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = "t1" })));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
    }

    [Fact]
    public async Task An_error_inside_the_stream_is_thrown()
    {
        var error = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "s1", ["error"] = new JsonObject { ["code"] = -32004, ["message"] = "nope" } };
        var (transport, _) = Make(_ => FakeV03Server.Sse(error.ToJsonString()));
        var ex = await Assert.ThrowsAsync<A2AException>(() => ToListAsync(transport.SendStreamingMessageAsync(Send("hi"))));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
    }

    [Fact]
    public async Task A_malformed_event_is_an_invalid_response()
    {
        var (transport, _) = Make(_ => FakeV03Server.Sse(FakeV03Server.RpcEvent(new JsonObject { ["kind"] = "bogus" }).ToJsonString()));
        var ex = await Assert.ThrowsAsync<A2AException>(() => ToListAsync(transport.SendStreamingMessageAsync(Send("hi"))));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    // ---- tasks ----

    [Fact]
    public async Task A_response_without_result_is_a_null_result()
    {
        // a2a-sdk 0.3 (Python) drops a null result (exclude_none): the delete answer is {"jsonrpc":"2.0","id":...}.
        var (transport, server) = Make(_ => FakeV03Server.Json("""{"jsonrpc":"2.0","id":"1"}"""));
        await transport.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = "t1", Id = "c1" });
        Assert.Equal("tasks/pushNotificationConfig/delete", (string?)server.Last.Json!["method"]);

        var (bad, _) = Make(_ => FakeV03Server.Json("""{"jsonrpc":"2.0"}"""));
        var ex = await Assert.ThrowsAsync<A2AException>(() => bad.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = "t1", Id = "c1" }));
        Assert.Contains("not a JSON-RPC response", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_and_cancel_task_use_v03_params()
    {
        var (transport, server) = Make(_ => FakeV03Server.Rpc(JsonNode.Parse(TaskJson("canceled"))));
        var task = await transport.GetTaskAsync(new GetTaskRequest { Id = "t1", HistoryLength = 3 });
        Assert.Equal(TaskState.Canceled, task.Status.State);
        Assert.Equal("tasks/get", (string?)server.Last.Json!["method"]);
        Assert.Equal("t1", (string?)server.Last.Json!["params"]!["id"]);
        Assert.Equal(3, (int?)server.Last.Json!["params"]!["historyLength"]);

        await transport.CancelTaskAsync(new CancelTaskRequest { Id = "t1" });
        Assert.Equal("tasks/cancel", (string?)server.Last.Json!["method"]);
        Assert.Equal("t1", (string?)server.Last.Json!["params"]!["id"]);
        Assert.Null(server.Last.Json!["params"]!["metadata"]);
    }

    [Fact]
    public async Task Cancel_sends_its_metadata_in_the_v03_TaskIdParams()
    {
        using var meters = new MeterRecorder();
        var (transport, server) = Make(_ => FakeV03Server.Rpc(JsonNode.Parse(TaskJson("canceled"))));
        var metadata = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["reason"] = System.Text.Json.JsonDocument.Parse("\"user asked\"").RootElement.Clone(),
        };
        await transport.CancelTaskAsync(new CancelTaskRequest { Id = "t1", Metadata = metadata });

        Assert.Equal("user asked", (string?)server.Last.Json!["params"]!["metadata"]!["reason"]);
        Assert.DoesNotContain("tasks/cancel.metadata", meters.DroppedFields());
    }

    [Fact]
    public async Task List_tasks_is_unsupported_and_sends_nothing()
    {
        var (transport, server) = Make(_ => FakeV03Server.Rpc(null));
        var ex = await Assert.ThrowsAsync<A2AException>(() => transport.ListTasksAsync(new ListTasksRequest()));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
        Assert.Empty(server.Requests);
    }

    // ---- push config ----

    private static JsonObject PushConfigJson() => new()
    {
        ["taskId"] = "t1",
        ["pushNotificationConfig"] = new JsonObject
        {
            ["id"] = "c1", ["url"] = "https://hook.example/x", ["token"] = "tok",
            ["authentication"] = new JsonObject { ["schemes"] = new JsonArray("Bearer", "Basic"), ["credentials"] = "secret" },
        },
    };

    [Fact]
    public async Task Push_config_create_get_list_delete()
    {
        var (transport, server) = Make(r => (string?)r.Json!["method"] switch
        {
            "tasks/pushNotificationConfig/list" => FakeV03Server.Rpc(new JsonArray(PushConfigJson())),
            "tasks/pushNotificationConfig/delete" => FakeV03Server.Rpc(null),
            _ => FakeV03Server.Rpc(PushConfigJson()),
        });

        var created = await transport.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig
        {
            TaskId = "t1", Id = "c1", Url = "https://hook.example/x", Token = "tok",
            Authentication = new AuthenticationInfo { Scheme = "Bearer", Credentials = "secret" },
        });
        Assert.Equal("c1", created.Id);
        Assert.Equal("t1", created.TaskId);
        Assert.Equal("Bearer", created.Authentication!.Scheme); // schemes[0]
        var set = server.Last.Json!;
        Assert.Equal("tasks/pushNotificationConfig/set", (string?)set["method"]);
        Assert.Equal("t1", (string?)set["params"]!["taskId"]);
        Assert.Equal("https://hook.example/x", (string?)set["params"]!["pushNotificationConfig"]!["url"]);
        Assert.Equal("Bearer", (string?)set["params"]!["pushNotificationConfig"]!["authentication"]!["schemes"]![0]);

        var got = await transport.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = "t1", Id = "c1" });
        Assert.Equal("tok", got.Token);
        var get = server.Last.Json!;
        Assert.Equal("tasks/pushNotificationConfig/get", (string?)get["method"]);
        Assert.Equal("t1", (string?)get["params"]!["id"]);
        Assert.Equal("c1", (string?)get["params"]!["pushNotificationConfigId"]);

        var list = await transport.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = "t1" });
        Assert.Single(list.Configs);
        Assert.Equal("tasks/pushNotificationConfig/list", (string?)server.Last.Json!["method"]);
        Assert.Equal("t1", (string?)server.Last.Json!["params"]!["id"]);

        await transport.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = "t1", Id = "c1" });
        var delete = server.Last.Json!;
        Assert.Equal("tasks/pushNotificationConfig/delete", (string?)delete["method"]);
        Assert.Equal("c1", (string?)delete["params"]!["pushNotificationConfigId"]);
    }

    // ---- extended card ----

    [Fact]
    public async Task Extended_card_is_read_in_the_v03_card_dialect_and_is_unsigned()
    {
        var (transport, server) = Make(_ => FakeV03Server.Rpc(JsonNode.Parse(Golden.Text("agent-card.json"))));
        var card = await transport.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest());
        Assert.Equal("Golden Agent", card.Name);
        Assert.Null(card.Signatures);
        Assert.Equal("agent/getAuthenticatedExtendedCard", (string?)server.Last.Json!["method"]);
        Assert.Null(server.Last.Json!["params"]);
    }

    // ---- errors ----

    [Theory]
    [InlineData(-32001, A2AErrorCode.TaskNotFound)]
    [InlineData(-32002, A2AErrorCode.TaskNotCancelable)]
    [InlineData(-32003, A2AErrorCode.PushNotificationNotSupported)]
    [InlineData(-32004, A2AErrorCode.UnsupportedOperation)]
    [InlineData(-32602, A2AErrorCode.InvalidParams)]
    [InlineData(-32601, A2AErrorCode.MethodNotFound)]
    [InlineData(-32050, A2AErrorCode.Internal)]
    [InlineData(7, A2AErrorCode.Internal)]
    public async Task Rpc_error_codes_map_to_a2a_codes(int code, A2AErrorCode expected)
    {
        var (transport, _) = Make(_ => FakeV03Server.RpcError(code, "boom"));
        var ex = await Assert.ThrowsAsync<A2AException>(() => transport.GetTaskAsync(new GetTaskRequest { Id = "t1" }));
        Assert.Equal(expected, ex.Code);
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Http_errors_without_a_rpc_body_become_http_errors()
    {
        var (transport, _) = Make(_ => FakeV03Server.Json("denied", HttpStatusCode.Unauthorized, "text/plain"));
        var ex = await Assert.ThrowsAsync<A2AException>(() => transport.GetTaskAsync(new GetTaskRequest { Id = "t1" }));
        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        Assert.Equal(401, ex.HttpStatus);
    }

    [Fact]
    public async Task A_result_that_does_not_parse_is_an_invalid_response()
    {
        var (transport, _) = Make(_ => FakeV03Server.Rpc(new JsonObject { ["kind"] = "task" }));
        var ex = await Assert.ThrowsAsync<A2AException>(() => transport.GetTaskAsync(new GetTaskRequest { Id = "t1" }));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    [Fact]
    public async Task A_response_with_no_result_is_an_invalid_response()
    {
        var (transport, _) = Make(_ => FakeV03Server.Rpc(null));
        var ex = await Assert.ThrowsAsync<A2AException>(() => transport.GetTaskAsync(new GetTaskRequest { Id = "t1" }));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    [Fact]
    public async Task A_server_error_is_retried_like_the_v1_transports_do()
    {
        var calls = 0;
        var server = new FakeV03Server(_ => ++calls == 1
            ? FakeV03Server.Json("oops", HttpStatusCode.ServiceUnavailable, "text/plain")
            : FakeV03Server.Rpc(JsonNode.Parse(TaskJson())));
        var options = new A2AClientOptions { MaxRetries = 1, TimeProvider = new InstantTime() };
        var transport = new V03JsonRpcTransport(server.Client(), Endpoint, options);
        var task = await transport.GetTaskAsync(new GetTaskRequest { Id = "t1" });
        Assert.Equal("t1", task.Id);
        Assert.Equal(2, server.Requests.Count);
    }

    /// <summary>A clock whose timers fire at once, so retry delays do not slow the test.</summary>
    private sealed class InstantTime : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public async Task Redirects_are_refused()
    {
        var server = new FakeV03Server(_ => FakeV03Server.Rpc(JsonNode.Parse(TaskJson())));
        // The handler below makes the response look as if it came from another URL.
        var redirecting = new RedirectingHandler(server);
        var transport = new V03JsonRpcTransport(new HttpClient(redirecting), Endpoint, new A2AClientOptions { MaxRetries = 0 });
        var ex = await Assert.ThrowsAsync<A2AException>(() => transport.GetTaskAsync(new GetTaskRequest { Id = "t1" }));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    private sealed class RedirectingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            response.RequestMessage = new HttpRequestMessage(request.Method, "http://other.test/rpc");
            return response;
        }
    }
}
