using System.Net;
using System.Text.Json.Nodes;
using A2A.Client;
using A2A.Client.V0_3Compat;
using Google.Protobuf;
using P = A2a.V1;
using static A2A.V0_3Compat.Tests.ClientData;

namespace A2A.V0_3Compat.Tests;

/// <summary>The v0.3 HTTP+JSON transport against a fake v0.3 agent. Every operation, the stream rules, and error mapping.</summary>
public class ClientHttpJsonTests
{
    private static readonly JsonParser Strict = new(JsonParser.Settings.Default); // unknown fields are an error

    private static readonly Uri Base = new("http://agent.test/api");

    private static (V03HttpJsonTransport Transport, FakeV03Server Server) Make(Func<SeenRequest, HttpResponseMessage> respond, Action<A2AClientOptions>? configure = null)
    {
        var server = new FakeV03Server(respond);
        var options = new A2AClientOptions { MaxRetries = 0 };
        configure?.Invoke(options);
        return (new V03HttpJsonTransport(server.Client(), Base, options), server);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    public async Task A_dot_segment_id_or_tenant_is_refused_before_sending(string dots)
    {
        var (transport, server) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new A2AClient(transport);

        var byId = await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync(dots));
        Assert.Equal(A2AErrorCode.InvalidParams, byId.Code);
        var byTenant = await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync(new GetTaskRequest { Id = "t1", Tenant = dots }));
        Assert.Equal(A2AErrorCode.InvalidParams, byTenant.Code);
        Assert.Empty(server.Requests);
    }

    private static async Task<List<StreamResponse>> ToListAsync(IAsyncEnumerable<StreamResponse> source)
    {
        var list = new List<StreamResponse>();
        await foreach (var item in source) list.Add(item);
        return list;
    }

    // ---- send ----

    [Fact]
    public async Task Send_writes_a_proto_json_request_and_reads_a_wrapped_task()
    {
        var (transport, server) = Make(_ => FakeV03Server.Json(new JsonObject { ["task"] = JsonNode.Parse(RestTaskJson("TASK_STATE_CANCELLED")) }));
        var response = await transport.SendMessageAsync(Send("hi"));

        Assert.Equal(TaskState.Canceled, Assert.IsType<TaskResponse>(response).Task.Status.State);
        var request = server.Last;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/message:send", request.PathAndQuery);
        var message = request.Json!["message"]!;
        Assert.Equal("ROLE_USER", (string?)message["role"]);
        Assert.Equal("hi", (string?)message["content"]![0]!["text"]);
        Assert.Null(message["kind"]);
        Assert.Null(message["parts"]);
        Assert.Equal("0.3", request.Header("A2A-Version"));
    }

    [Fact]
    public async Task Send_reads_a_wrapped_message()
    {
        var reply = new JsonObject
        {
            ["message"] = new JsonObject { ["messageId"] = "r1", ["role"] = "ROLE_AGENT", ["content"] = new JsonArray(new JsonObject { ["text"] = "hello" }) },
        };
        var (transport, _) = Make(_ => FakeV03Server.Json(reply));
        var response = await transport.SendMessageAsync(Send("hi"));
        Assert.Equal("hello", Assert.IsType<TextPart>(Assert.IsType<MessageResponse>(response).Message.Parts[0]).Text);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Blocking_is_always_written_and_return_immediately_means_false(bool? returnImmediately, bool expectedBlocking)
    {
        var (transport, server) = Make(_ => FakeV03Server.Json(new JsonObject { ["task"] = JsonNode.Parse(RestTaskJson()) }));
        await transport.SendMessageAsync(Send("hi", returnImmediately));
        Assert.Equal(expectedBlocking, (bool?)server.Last.Json!["configuration"]!["blocking"]);
    }

    [Fact]
    public async Task Extensions_use_the_v03_header()
    {
        var (transport, server) = Make(_ => FakeV03Server.Json(new JsonObject { ["task"] = JsonNode.Parse(RestTaskJson()) }), o => o.Extensions.Add("urn:a"));
        await transport.SendMessageAsync(Send("hi"), new A2ARequestOptions { Extensions = ["urn:b"] });
        Assert.Equal("urn:a,urn:b", server.Last.Header("X-A2A-Extensions"));
        Assert.Null(server.Last.Header("A2A-Extensions"));
    }

    [Fact]
    public async Task The_tenant_goes_into_the_path()
    {
        var (transport, server) = Make(r => FakeV03Server.Json(r.PathAndQuery.Contains("message:send", StringComparison.Ordinal)
            ? new JsonObject { ["task"] = JsonNode.Parse(RestTaskJson()) }.ToJsonString() : RestTaskJson()), o => o.Tenant = "acme");
        await transport.SendMessageAsync(Send("hi"));
        Assert.Equal("/api/acme/v1/message:send", server.Last.PathAndQuery);
        await transport.GetTaskAsync(new GetTaskRequest { Id = "t1", Tenant = "other" });
        Assert.StartsWith("/api/other/v1/tasks/t1", server.Last.PathAndQuery);
        Assert.Null(server.Last.Json); // a v0.3 body has no tenant
    }

    // ---- streams ----

    [Fact]
    public async Task Stream_reads_events_and_stops_on_final_even_if_the_connection_stays_open()
    {
        var (transport, server) = Make(_ => FakeV03Server.SseThenHang(
            new JsonObject { ["task"] = JsonNode.Parse(RestTaskJson()) }.ToJsonString(),
            RestStatusUpdateJson("TASK_STATE_WORKING", final: false),
            RestStatusUpdateJson("TASK_STATE_COMPLETED", final: true)));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = await ToListAsync(transport.SendStreamingMessageAsync(Send("hi"), cancellationToken: cts.Token));

        Assert.Equal(3, events.Count);
        Assert.IsType<TaskEvent>(events[0]);
        Assert.Equal(TaskState.Completed, Assert.IsType<StatusUpdateEvent>(events[2]).StatusUpdate.Status.State);
        Assert.Equal(HttpMethod.Post, server.Last.Method);
        Assert.Equal("/api/v1/message:stream", server.Last.PathAndQuery);
    }

    [Fact]
    public async Task A_stream_without_final_ends_when_the_connection_ends()
    {
        var (transport, _) = Make(_ => FakeV03Server.Sse(RestStatusUpdateJson("TASK_STATE_WORKING", false)));
        Assert.Single(await ToListAsync(transport.SendStreamingMessageAsync(Send("hi"))));
    }

    [Fact]
    public async Task Stream_reads_message_and_artifact_events_and_skips_unknown_ones()
    {
        var artifact = new JsonObject
        {
            ["artifactUpdate"] = new JsonObject
            {
                ["taskId"] = "t1", ["contextId"] = "c1", ["lastChunk"] = true,
                ["artifact"] = new JsonObject { ["artifactId"] = "a1", ["parts"] = new JsonArray(new JsonObject { ["text"] = "x" }) },
            },
        };
        var message = new JsonObject { ["message"] = new JsonObject { ["messageId"] = "r1", ["role"] = "ROLE_AGENT", ["content"] = new JsonArray(new JsonObject { ["text"] = "y" }) } };
        var (transport, _) = Make(_ => FakeV03Server.Sse(artifact.ToJsonString(), """{"futureEvent":{}}""", message.ToJsonString()));
        var events = await ToListAsync(transport.SendStreamingMessageAsync(Send("hi")));
        Assert.Equal(2, events.Count);
        Assert.IsType<ArtifactUpdateEvent>(events[0]);
        Assert.IsType<MessageEvent>(events[1]);
    }

    [Fact]
    public async Task Subscribe_uses_get_even_when_the_options_ask_for_post()
    {
        var (transport, server) = Make(_ => FakeV03Server.Sse(RestStatusUpdateJson("TASK_STATE_COMPLETED", true)), o => o.RestSubscribeMethod = HttpMethod.Post);
        Assert.Single(await ToListAsync(transport.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = "t 1" })));
        Assert.Equal(HttpMethod.Get, server.Last.Method);
        Assert.Equal("/api/v1/tasks/t%201:subscribe", server.Last.PathAndQuery);
        Assert.Equal("", server.Last.Body);

        var resumable = (A2A.Client.Streaming.IResumableTransport)transport;
        await foreach (var _ in resumable.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = "t1" }, null, "t1:2", default)) { }
        Assert.Equal("t1:2", server.Last.Header("Last-Event-ID"));
    }

    [Fact]
    public async Task An_error_before_the_stream_is_thrown_with_the_body_code()
    {
        var (transport, _) = Make(_ => FakeV03Server.Json("""{"code":-32001,"message":"gone"}""", HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<A2AException>(() => ToListAsync(transport.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = "t1" })));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
    }

    [Fact]
    public async Task An_error_member_inside_the_stream_is_thrown()
    {
        var (transport, _) = Make(_ => FakeV03Server.Sse("""{"error":{"code":-32004,"message":"nope"}}"""));
        var ex = await Assert.ThrowsAsync<A2AException>(() => ToListAsync(transport.SendStreamingMessageAsync(Send("hi"))));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
    }

    [Fact]
    public async Task A_stream_response_that_is_not_sse_is_an_invalid_response()
    {
        var (transport, _) = Make(_ => FakeV03Server.Json("{}"));
        var ex = await Assert.ThrowsAsync<A2AException>(() => ToListAsync(transport.SendStreamingMessageAsync(Send("hi"))));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    // ---- tasks ----

    [Fact]
    public async Task Get_task_uses_get_with_history_length()
    {
        var (transport, server) = Make(_ => FakeV03Server.Json(RestTaskJson("TASK_STATE_INPUT_REQUIRED")));
        var task = await transport.GetTaskAsync(new GetTaskRequest { Id = "t1", HistoryLength = 3 });
        Assert.Equal(TaskState.InputRequired, task.Status.State);
        Assert.Equal(HttpMethod.Get, server.Last.Method);
        Assert.Equal("/api/v1/tasks/t1?historyLength=3", server.Last.PathAndQuery);

        await transport.GetTaskAsync(new GetTaskRequest { Id = "t1" });
        Assert.Equal("/api/v1/tasks/t1", server.Last.PathAndQuery);
    }

    [Fact]
    public async Task Cancel_task_posts_to_the_cancel_route()
    {
        var (transport, server) = Make(_ => FakeV03Server.Json(RestTaskJson("TASK_STATE_CANCELLED")));
        var task = await transport.CancelTaskAsync(new CancelTaskRequest { Id = "t1" });
        Assert.Equal(TaskState.Canceled, task.Status.State);
        Assert.Equal(HttpMethod.Post, server.Last.Method);
        Assert.Equal("/api/v1/tasks/t1:cancel", server.Last.PathAndQuery);
    }

    [Fact]
    public async Task Cancel_metadata_cannot_be_sent_and_its_drop_is_counted()
    {
        using var meters = new MeterRecorder();
        var (transport, server) = Make(_ => FakeV03Server.Json(RestTaskJson("TASK_STATE_CANCELLED")));
        var metadata = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["reason"] = System.Text.Json.JsonDocument.Parse("1").RootElement.Clone(),
        };
        await transport.CancelTaskAsync(new CancelTaskRequest { Id = "t1", Metadata = metadata });

        Assert.Null(server.Last.Json!["metadata"]); // the proto CancelTaskRequest has only the name
        Assert.Contains("tasks/cancel.metadata", meters.DroppedFields());
    }

    private static string RestTask(string id, string context, string state, string timestamp, int historyCount) =>
        new JsonObject
        {
            ["id"] = id, ["contextId"] = context,
            ["status"] = new JsonObject { ["state"] = state, ["timestamp"] = timestamp },
            ["history"] = new JsonArray(Enumerable.Range(0, historyCount).Select(i => (JsonNode)new JsonObject
            {
                ["messageId"] = $"m{i}", ["role"] = "ROLE_USER", ["content"] = new JsonArray(new JsonObject { ["text"] = $"h{i}" }),
            }).ToArray()),
            ["artifacts"] = new JsonArray(new JsonObject { ["artifactId"] = "a", ["parts"] = new JsonArray(new JsonObject { ["text"] = "x" }) }),
        }.ToJsonString();

    [Fact]
    public async Task List_tasks_gets_the_v03_array_and_applies_the_filters_here()
    {
        var array = $"[{RestTask("t1", "c1", "TASK_STATE_COMPLETED", "2026-01-02T00:00:00Z", 3)},"
                    + $"{RestTask("t2", "c2", "TASK_STATE_COMPLETED", "2026-01-03T00:00:00Z", 1)},"
                    + $"{RestTask("t3", "c1", "TASK_STATE_WORKING", "2026-01-04T00:00:00Z", 2)}]";
        var (transport, server) = Make(_ => FakeV03Server.Json(array));

        var all = await transport.ListTasksAsync(new ListTasksRequest());
        Assert.Equal(HttpMethod.Get, server.Last.Method);
        Assert.Equal("/api/v1/tasks", server.Last.PathAndQuery); // v0.3 defines no query parameters
        Assert.Equal(["t1", "t2", "t3"], all.Tasks.Select(t => t.Id));
        Assert.True(string.IsNullOrEmpty(all.NextPageToken));

        var filtered = await transport.ListTasksAsync(new ListTasksRequest
        {
            ContextId = "c1", Status = TaskState.Completed, HistoryLength = 1, IncludeArtifacts = false,
        });
        var only = Assert.Single(filtered.Tasks);
        Assert.Equal("t1", only.Id);
        Assert.Equal("m2", Assert.Single(only.History!).MessageId); // the newest one
        Assert.Null(only.Artifacts);

        var after = await transport.ListTasksAsync(new ListTasksRequest { StatusTimestampAfter = DateTimeOffset.Parse("2026-01-02T12:00:00Z"), PageSize = 1 });
        Assert.Equal("t2", Assert.Single(after.Tasks).Id);
        Assert.False(string.IsNullOrEmpty(after.NextPageToken)); // t3 is after it too

        // pageSize pages the filtered array: paging on reaches every task.
        var seen = new List<string>();
        string? token = null;
        do
        {
            var page = await transport.ListTasksAsync(new ListTasksRequest { PageSize = 2, PageToken = token });
            Assert.True(page.Tasks.Count <= 2);
            seen.AddRange(page.Tasks.Select(t => t.Id));
            token = page.NextPageToken;
        } while (!string.IsNullOrEmpty(token));
        Assert.Equal(["t1", "t2", "t3"], seen);

        var bad = await Assert.ThrowsAsync<A2AException>(() => transport.ListTasksAsync(new ListTasksRequest { PageToken = "elsewhere" }));
        Assert.Equal(A2AErrorCode.InvalidParams, bad.Code);
    }

    // ---- push config ----

    private static JsonObject PushConfigJson() => new()
    {
        ["name"] = "tasks/t1/pushNotificationConfigs/c1",
        ["pushNotificationConfig"] = new JsonObject
        {
            ["id"] = "c1", ["url"] = "https://hook.example/x", ["token"] = "tok",
            ["authentication"] = new JsonObject { ["schemes"] = new JsonArray("Bearer"), ["credentials"] = "secret" },
        },
    };

    [Fact]
    public async Task Push_config_create_get_list_delete()
    {
        var (transport, server) = Make(r =>
        {
            if (r.Method == HttpMethod.Delete) return FakeV03Server.Json("{}");
            if (r.Method == HttpMethod.Get && r.PathAndQuery.Contains("pushNotificationConfigs?", StringComparison.Ordinal))
                return FakeV03Server.Json(new JsonObject { ["configs"] = new JsonArray(PushConfigJson()), ["nextPageToken"] = "n2" });
            return FakeV03Server.Json(PushConfigJson());
        });

        var created = await transport.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig
        {
            TaskId = "t1", Id = "c1", Url = "https://hook.example/x", Token = "tok",
            Authentication = new AuthenticationInfo { Scheme = "Bearer", Credentials = "secret" },
        });
        Assert.Equal("c1", created.Id);
        Assert.Equal("t1", created.TaskId);
        Assert.Equal(HttpMethod.Post, server.Last.Method);
        Assert.Equal("/api/v1/tasks/t1/pushNotificationConfigs?configId=c1", server.Last.PathAndQuery);
        // The whole CreateTaskPushNotificationConfigRequest, as the a2a-sdk 0.3 Python client sends it and its server reads it.
        Strict.Parse<P.CreateTaskPushNotificationConfigRequest>(server.Last.Body);
        Assert.Equal("tasks/t1", (string?)server.Last.Json!["parent"]);
        Assert.Equal("c1", (string?)server.Last.Json!["configId"]);
        Assert.Equal("tasks/t1/pushNotificationConfigs/c1", (string?)server.Last.Json!["config"]!["name"]);
        Assert.Equal("https://hook.example/x", (string?)server.Last.Json!["config"]!["pushNotificationConfig"]!["url"]);

        var got = await transport.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = "t1", Id = "c1" });
        Assert.Equal("tok", got.Token);
        Assert.Equal("/api/v1/tasks/t1/pushNotificationConfigs/c1", server.Last.PathAndQuery);

        var list = await transport.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = "t1", PageSize = 5, PageToken = "p" });
        Assert.Single(list.Configs);
        Assert.Equal("n2", list.NextPageToken);
        Assert.Equal("/api/v1/tasks/t1/pushNotificationConfigs?pageSize=5&pageToken=p", server.Last.PathAndQuery);

        await transport.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = "t1", Id = "c1" });
        Assert.Equal(HttpMethod.Delete, server.Last.Method);
        Assert.Equal("/api/v1/tasks/t1/pushNotificationConfigs/c1", server.Last.PathAndQuery);
    }

    [Fact]
    public async Task Delete_accepts_an_empty_body()
    {
        var (transport, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });
        await transport.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = "t1", Id = "c1" });
    }

    // ---- extended card ----

    [Fact]
    public async Task Extended_card_is_get_v1_card_in_the_json_rpc_card_dialect()
    {
        var (transport, server) = Make(_ => FakeV03Server.Json(Golden.Text("agent-card.json")));
        var card = await transport.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest());
        Assert.Equal("Golden Agent", card.Name);
        Assert.Null(card.Signatures);
        Assert.Equal(HttpMethod.Get, server.Last.Method);
        Assert.Equal("/api/v1/card", server.Last.PathAndQuery);
    }

    // ---- errors ----

    [Theory]
    [InlineData("""{"code":-32001,"message":"no task"}""", HttpStatusCode.NotFound, A2AErrorCode.TaskNotFound)]
    [InlineData("""{"code":-32002,"message":"x"}""", HttpStatusCode.Conflict, A2AErrorCode.TaskNotCancelable)]
    [InlineData("""{"code":-32003,"message":"x"}""", HttpStatusCode.BadRequest, A2AErrorCode.PushNotificationNotSupported)]
    [InlineData("""{"code":-32602,"message":"x"}""", HttpStatusCode.BadRequest, A2AErrorCode.InvalidParams)]
    [InlineData("""{"code":401,"message":"x"}""", HttpStatusCode.Unauthorized, A2AErrorCode.Unauthenticated)]
    [InlineData("""{"code":403,"message":"x"}""", HttpStatusCode.Forbidden, A2AErrorCode.PermissionDenied)]
    [InlineData("""{"message":"bad"}""", HttpStatusCode.BadRequest, A2AErrorCode.InvalidParams)]
    [InlineData("""{"message":"missing"}""", HttpStatusCode.NotFound, A2AErrorCode.TaskNotFound)]
    [InlineData("""{"message":"broken"}""", HttpStatusCode.InternalServerError, A2AErrorCode.Internal)]
    [InlineData("""{"error":{"code":-32004,"message":"nested"}}""", HttpStatusCode.BadRequest, A2AErrorCode.UnsupportedOperation)]
    public async Task Rest_errors_read_the_code_from_the_body_else_the_status(string body, HttpStatusCode status, A2AErrorCode expected)
    {
        var (transport, _) = Make(_ => FakeV03Server.Json(body, status));
        var ex = await Assert.ThrowsAsync<A2AException>(() => transport.GetTaskAsync(new GetTaskRequest { Id = "t1" }));
        Assert.Equal(expected, ex.Code);
        Assert.Equal((int)status, ex.HttpStatus);
    }

    [Fact]
    public async Task The_error_message_comes_from_the_body()
    {
        var (transport, _) = Make(_ => FakeV03Server.Json("""{"code":-32001,"message":"no such task"}""", HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<A2AException>(() => transport.GetTaskAsync(new GetTaskRequest { Id = "t1" }));
        Assert.Equal("no such task", ex.Message);
    }

    [Fact]
    public async Task A_non_json_error_is_a_plain_http_error()
    {
        var (transport, _) = Make(_ => FakeV03Server.Json("<html>bad gateway</html>", HttpStatusCode.BadGateway, "text/html"));
        var ex = await Assert.ThrowsAsync<A2AException>(() => transport.GetTaskAsync(new GetTaskRequest { Id = "t1" }));
        Assert.Equal(A2AErrorCode.Internal, ex.Code);
        Assert.Equal(502, ex.HttpStatus);
    }

    [Fact]
    public async Task A_body_that_does_not_parse_is_an_invalid_response()
    {
        var (transport, _) = Make(_ => FakeV03Server.Json("{\"id\":\"t1\"}"));
        var ex = await Assert.ThrowsAsync<A2AException>(() => transport.GetTaskAsync(new GetTaskRequest { Id = "t1" }));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    [Fact]
    public async Task A_server_error_is_retried_for_idempotent_calls()
    {
        var calls = 0;
        var server = new FakeV03Server(_ => ++calls == 1 ? FakeV03Server.Json("""{"message":"x"}""", HttpStatusCode.ServiceUnavailable) : FakeV03Server.Json(RestTaskJson()));
        var transport = new V03HttpJsonTransport(server.Client(), Base, new A2AClientOptions { MaxRetries = 1, TimeProvider = new InstantTime() });
        Assert.Equal("t1", (await transport.GetTaskAsync(new GetTaskRequest { Id = "t1" })).Id);
        Assert.Equal(2, server.Requests.Count);
    }

    private sealed class InstantTime : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }
}
