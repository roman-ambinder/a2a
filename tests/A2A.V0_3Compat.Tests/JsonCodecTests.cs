using System.Text.Json;
using System.Text.Json.Nodes;

namespace A2A.V0_3Compat.Tests;

/// <summary>The v0.3 JSON-RPC dialect (V03JsonCodec), checked against documents that validate against the v0.3 JSON schema.</summary>
public class JsonCodecTests
{
    private static readonly V03JsonCodec C = V03JsonCodec.Instance;

    [Fact]
    public void Golden_task_reads_and_writes_back_the_same_json()
    {
        var golden = Golden.Read("task.json");
        var task = C.ReadTask(golden);

        Assert.Equal("task-1", task.Id);
        Assert.Equal(TaskState.InputRequired, task.Status.State);
        Assert.Equal(new DateTimeOffset(2025, 10, 27, 10, 0, 0, TimeSpan.Zero), task.Status.Timestamp);
        Assert.Equal("Which city?", task.Status.Message!.GetText());
        var parts = task.Artifacts![0].Parts;
        Assert.IsType<TextPart>(parts[0]);
        Assert.IsType<DataPart>(parts[1]);
        var raw = Assert.IsType<RawPart>(parts[2]);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(raw.Raw.Span));
        Assert.Equal("text/plain", raw.MediaType);
        Assert.Equal("hello.txt", raw.Filename);
        var url = Assert.IsType<UrlPart>(parts[3]);
        Assert.Equal("https://example.com/f.png", url.Url.OriginalString);
        Assert.Equal(["task-0"], task.History![0].ReferenceTaskIds);

        var written = C.WriteTask(task);
        Assert.True(Nodes.Same(golden, written), Nodes.Show(written));
    }

    [Fact]
    public void Golden_status_update_round_trips_and_carries_final()
    {
        var golden = Golden.Read("status-update.json");
        var evt = C.ReadStreamEvent(golden, out var final);
        Assert.True(final);
        var update = Assert.IsType<StatusUpdateEvent>(evt).StatusUpdate;
        Assert.Equal(TaskState.Completed, update.Status.State);
        Assert.True(Nodes.Same(golden, C.WriteStreamEvent(evt)));
    }

    [Fact]
    public void Golden_artifact_update_round_trips()
    {
        var golden = Golden.Read("artifact-update.json");
        var evt = Assert.IsType<ArtifactUpdateEvent>(C.ReadStreamEvent(golden, out var final));
        Assert.False(final);
        Assert.True(evt.ArtifactUpdate.Append);
        Assert.True(evt.ArtifactUpdate.LastChunk);
        Assert.True(Nodes.Same(golden, C.WriteStreamEvent(evt)));
    }

    [Fact]
    public void Golden_send_params_round_trip_with_blocking_and_push_config()
    {
        var golden = Golden.Read("send-params.json");
        var request = C.ReadSendRequest(golden);
        Assert.True(request.Configuration!.ReturnImmediately); // blocking: false
        Assert.Equal(5, request.Configuration.HistoryLength);
        var push = request.Configuration.TaskPushNotificationConfig!;
        Assert.Equal("cfg-1", push.Id);
        Assert.Equal("Bearer", push.Authentication!.Scheme);
        Assert.Equal("secret", push.Authentication.Credentials);
        Assert.True(Nodes.Same(golden, C.WriteSendRequest(request)), Nodes.Show(C.WriteSendRequest(request)));
    }

    [Fact]
    public void Golden_push_config_round_trips_and_keeps_only_the_first_scheme()
    {
        var golden = Golden.Read("push-config.json");
        var config = C.ReadPushConfig(golden);
        Assert.Equal("task-1", config.TaskId);
        Assert.Equal("t0k3n", config.Token);
        Assert.True(Nodes.Same(golden, C.WritePushConfig(config)));

        using var recorder = new MeterRecorder();
        var many = Golden.Object("push-config.json");
        many["pushNotificationConfig"]!["authentication"]!["schemes"] = new JsonArray("Basic", "Bearer");
        Assert.Equal("Basic", C.ReadPushConfig(many).Authentication!.Scheme);
        Assert.Contains("pushNotificationConfig.authentication.schemes", recorder.DroppedFields());
    }

    [Fact]
    public void Absent_blocking_means_blocking_and_it_is_always_written()
    {
        var request = C.ReadSendRequest(Nodes.Parse("""{"message":{"messageId":"m","role":"user","parts":[{"kind":"text","text":"x"}]}}"""));
        Assert.Null(request.Configuration);
        var written = C.WriteSendRequest(request);
        Assert.True(written["configuration"]!["blocking"]!.GetValue<bool>());
    }

    [Fact]
    public void Tenant_is_not_sent_and_is_reported()
    {
        using var recorder = new MeterRecorder();
        var request = new SendMessageRequest { Message = Message.FromUser("hi"), Tenant = "acme" };
        Assert.Null(C.WriteSendRequest(request)["tenant"]);
        Assert.Contains("request.tenant", recorder.DroppedFields());
    }

    // ---- parts ----

    [Fact]
    public void Part_without_kind_is_inferred_from_its_members()
    {
        Assert.IsType<TextPart>(C.ReadPartNode(Nodes.Parse("""{"text":"a"}""")));
        Assert.IsType<DataPart>(C.ReadPartNode(Nodes.Parse("""{"data":{"a":1}}""")));
        Assert.IsType<UrlPart>(C.ReadPartNode(Nodes.Parse("""{"file":{"uri":"https://x/y"}}""")));
        Assert.Throws<JsonException>(() => C.ReadPartNode(Nodes.Parse("""{"other":1}""")));
    }

    [Fact]
    public void Unknown_part_kind_is_an_error()
    {
        var ex = Assert.Throws<JsonException>(() => C.ReadPartNode(Nodes.Parse("""{"kind":"video","text":"a"}""")));
        Assert.Contains("video", ex.Message);
    }

    [Fact]
    public void File_part_needs_exactly_one_of_bytes_and_uri()
    {
        Assert.Throws<JsonException>(() => C.ReadPartNode(Nodes.Parse("""{"kind":"file","file":{"name":"x"}}""")));
        Assert.Throws<JsonException>(() => C.ReadPartNode(Nodes.Parse("""{"kind":"file","file":{"bytes":"YQ==","uri":"https://x"}}""")));
        Assert.Throws<JsonException>(() => C.ReadPartNode(Nodes.Parse("""{"kind":"file","file":{"bytes":"***"}}""")));
    }

    [Fact]
    public void Non_object_data_is_wrapped_marked_and_unwrapped()
    {
        var part = Part.FromData(JsonDocument.Parse("[1,2,3]").RootElement.Clone());
        var written = C.WritePartNode(part);
        Assert.True(Nodes.Same(Nodes.Parse("""{"kind":"data","data":{"value":[1,2,3]},"metadata":{"data_part_compat":true}}"""), written), Nodes.Show(written));

        var back = Assert.IsType<DataPart>(C.ReadPartNode(written));
        Assert.Equal("[1,2,3]", back.Data.GetRawText());
        Assert.Null(back.Metadata); // the marker is removed
    }

    [Fact]
    public void Wrapped_data_keeps_the_other_metadata()
    {
        var part = Part.FromData(JsonDocument.Parse("\"text\"").RootElement.Clone()) with
        {
            Metadata = new() { ["k"] = JsonDocument.Parse("1").RootElement.Clone() },
        };
        var back = Assert.IsType<DataPart>(C.ReadPartNode(C.WritePartNode(part)));
        Assert.Equal("\"text\"", back.Data.GetRawText());
        Assert.Equal(["k"], back.Metadata!.Keys);
    }

    [Fact]
    public void A_data_part_with_an_object_is_not_wrapped_even_with_a_value_member()
    {
        var node = Nodes.Parse("""{"kind":"data","data":{"value":5}}""");
        var part = Assert.IsType<DataPart>(C.ReadPartNode(node));
        Assert.Equal("""{"value":5}""", part.Data.GetRawText().Replace(" ", ""));
    }

    [Fact]
    public void Filename_and_media_type_on_text_and_data_parts_are_dropped_and_reported()
    {
        using var recorder = new MeterRecorder();
        var text = C.WritePartNode(Part.FromText("a") with { Filename = "a.txt", MediaType = "text/markdown" });
        Assert.True(Nodes.Same(Nodes.Parse("""{"kind":"text","text":"a"}"""), text));
        var data = C.WritePartNode(Part.FromData(JsonDocument.Parse("{}").RootElement.Clone(), "application/x-custom"));
        Assert.Null(data["mediaType"]);
        var fields = recorder.DroppedFields().ToList();
        Assert.Contains("part.filename", fields);
        Assert.Contains("part.mediaType", fields);
    }

    // ---- states and roles ----

    public static TheoryData<TaskState, string> StateNames => new()
    {
        { TaskState.Submitted, "submitted" },
        { TaskState.Working, "working" },
        { TaskState.InputRequired, "input-required" },
        { TaskState.Completed, "completed" },
        { TaskState.Canceled, "canceled" },
        { TaskState.Failed, "failed" },
        { TaskState.Rejected, "rejected" },
        { TaskState.AuthRequired, "auth-required" },
        { TaskState.Unspecified, "unknown" },
    };

    [Theory]
    [MemberData(nameof(StateNames))]
    public void Every_state_maps_both_ways(TaskState state, string name)
    {
        var written = C.WriteStatus(new AgentTaskStatus { State = state });
        Assert.Equal(name, written["state"]!.GetValue<string>());
        Assert.Equal(state, C.ReadStatus(written).State);
    }

    [Fact]
    public void An_unrecognized_state_is_an_error()
    {
        Assert.Throws<JsonException>(() => C.ReadStatus(Nodes.Parse("""{"state":"TASK_STATE_WORKING"}""")));
        Assert.Throws<JsonException>(() => C.ReadStatus(Nodes.Parse("""{"state":"paused"}""")));
    }

    [Fact]
    public void Roles_map_both_ways_and_an_unknown_role_is_an_error()
    {
        Assert.Equal("user", C.WriteMessage(Message.FromUser("a"))["role"]!.GetValue<string>());
        Assert.Equal("agent", C.WriteMessage(Message.FromAgent("a"))["role"]!.GetValue<string>());
        Assert.Throws<JsonException>(() => C.ReadMessage(Nodes.Parse("""{"messageId":"m","role":"ROLE_USER","parts":[]}""")));
        Assert.Throws<JsonException>(() => C.ReadMessage(Nodes.Parse("""{"messageId":"m","parts":[]}""")));
    }

    // ---- events ----

    [Theory]
    [InlineData(TaskState.Working, false)]
    [InlineData(TaskState.Submitted, false)]
    [InlineData(TaskState.Completed, true)]
    [InlineData(TaskState.Failed, true)]
    [InlineData(TaskState.Canceled, true)]
    [InlineData(TaskState.Rejected, true)]
    [InlineData(TaskState.InputRequired, true)]
    [InlineData(TaskState.AuthRequired, true)]
    public void Final_is_true_for_terminal_and_interrupted_states(TaskState state, bool expected)
    {
        var evt = StreamResponse.Of(new TaskStatusUpdateEvent { TaskId = "t", ContextId = "c", Status = new AgentTaskStatus { State = state } });
        var node = C.WriteStreamEvent(evt);
        Assert.Equal("status-update", node["kind"]!.GetValue<string>());
        Assert.Equal(expected, node["final"]!.GetValue<bool>());
    }

    [Fact]
    public void Task_message_and_unknown_events_are_read_by_kind()
    {
        Assert.IsType<TaskEvent>(C.ReadStreamEvent(Golden.Read("task.json"), out _));
        Assert.IsType<MessageEvent>(C.ReadStreamEvent(Nodes.Parse("""{"kind":"message","messageId":"m","role":"agent","parts":[]}"""), out _));
        Assert.Throws<JsonException>(() => C.ReadStreamEvent(Nodes.Parse("""{"kind":"mystery"}"""), out _));
    }

    [Fact]
    public void Send_result_is_the_tagged_task_or_message()
    {
        Assert.IsType<TaskResponse>(C.ReadSendResult(Golden.Read("task.json")));
        var message = C.WriteSendResult(new MessageResponse(Message.FromAgent("hi")));
        Assert.Equal("message", message["kind"]!.GetValue<string>());
        Assert.IsType<MessageResponse>(C.ReadSendResult(message));
    }

    [Fact]
    public void Request_params_have_the_v03_shape()
    {
        Assert.True(Nodes.Same(Nodes.Parse("""{"id":"t","historyLength":3}"""), V03JsonCodec.WriteGetTaskParams(new GetTaskRequest { Id = "t", HistoryLength = 3 })));
        Assert.True(Nodes.Same(Nodes.Parse("""{"id":"t"}"""), V03JsonCodec.WriteTaskIdParams("t")));
        Assert.True(Nodes.Same(Nodes.Parse("""{"id":"t","pushNotificationConfigId":"c"}"""), V03JsonCodec.WritePushConfigIdParams("t", "c")));
        Assert.Equal(("t", 3), (V03JsonCodec.ReadGetTaskParams(Nodes.Parse("""{"id":"t","historyLength":3,"metadata":{}}""")) is { } g ? (g.Id, g.HistoryLength!.Value) : default));
        Assert.Throws<JsonException>(() => V03JsonCodec.ReadGetTaskParams(Nodes.Parse("{}")));
    }

    [Fact]
    public void Get_task_and_resubscribe_metadata_is_dropped_and_reported()
    {
        using var recorder = new MeterRecorder();
        V03JsonCodec.ReadGetTaskParams(Nodes.Parse("""{"id":"t","metadata":{"a":1}}"""));
        V03JsonCodec.ReadResubscribeParams(Nodes.Parse("""{"id":"t","metadata":{"a":1}}"""));
        var fields = recorder.DroppedFields().ToList();
        Assert.Contains("tasks/get.metadata", fields);
        Assert.Contains("tasks/resubscribe.metadata", fields);
        Assert.NotNull(V03JsonCodec.ReadCancelParams(Nodes.Parse("""{"id":"t","metadata":{"a":1}}""")).Metadata);
    }

    [Fact]
    public void Error_response_has_the_code_and_message_and_no_data()
    {
        var error = V03JsonCodec.WriteError(JsonDocument.Parse("7").RootElement, A2AException.TaskNotFound("t"));
        Assert.True(Nodes.Same(
            Nodes.Parse("""{"jsonrpc":"2.0","id":7,"error":{"code":-32001,"message":"The specified task ID does not exist or is not accessible"}}"""), error),
            Nodes.Show(error));
        // The auth codes have no JSON-RPC number.
        Assert.Equal(-32603, V03JsonCodec.WriteError(null, new A2AException(A2AErrorCode.Unauthenticated))["error"]!["code"]!.GetValue<int>());
        // A v1.0-only code is sent as it is.
        Assert.Equal(-32009, V03JsonCodec.WriteError(null, new A2AException(A2AErrorCode.VersionNotSupported))["error"]!["code"]!.GetValue<int>());
        Assert.Null(V03JsonCodec.WriteError(null, A2AException.TaskNotFound("t"))["id"]);
    }

    [Fact]
    public void Parse_refuses_duplicate_members_and_excess_depth()
    {
        Assert.ThrowsAny<JsonException>(() => V03Json.Parse("""{"a":1,"a":2}"""u8));
        Assert.ThrowsAny<JsonException>(() => V03Json.Parse(System.Text.Encoding.UTF8.GetBytes(new string('[', 100) + new string(']', 100))));
    }
}
