using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using P = A2a.V1;

namespace A2A.V0_3Compat.Tests;

/// <summary>
/// The v0.3 HTTP+JSON dialect (V03ProtoJsonCodec), checked with the generated classes of the real v0.3 proto
/// (tag v0.3.0): what the codec writes must parse with the protobuf JSON parser, and what the protobuf JSON formatter writes
/// must read back.
/// </summary>
public class ProtoJsonCodecTests
{
    private static readonly V03ProtoJsonCodec C = V03ProtoJsonCodec.Instance;
    private static readonly JsonParser Strict = new(JsonParser.Settings.Default); // unknown fields are an error

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static AgentTask SampleTask() => new()
    {
        Id = "task-1",
        ContextId = "ctx-1",
        Status = new AgentTaskStatus
        {
            State = TaskState.Canceled,
            Timestamp = new DateTimeOffset(2025, 10, 27, 10, 0, 0, TimeSpan.Zero),
            Message = Message.FromAgent("stopped", "ctx-1", "task-1"),
        },
        Artifacts =
        [
            new Artifact
            {
                ArtifactId = "a1",
                Name = "result",
                Parts =
                [
                    Part.FromText("42"),
                    Part.FromData(El("""{"answer":42}""")),
                    Part.FromBytes(System.Text.Encoding.UTF8.GetBytes("hello"), "text/plain"),
                    Part.FromUrl(new Uri("https://example.com/f.png"), "image/png"),
                ],
                Metadata = new() { ["k"] = El("\"v\"") },
                Extensions = ["urn:ext"],
            },
        ],
        History = [new Message { MessageId = "m1", Role = Role.User, Parts = [Part.FromText("hi")], ContextId = "ctx-1", TaskId = "task-1", Extensions = ["urn:ext"] }],
        Metadata = new() { ["source"] = El("\"test\"") },
    };

    [Fact]
    public void A_task_the_codec_writes_parses_with_the_v03_proto()
    {
        var json = C.WriteTask(SampleTask()).ToJsonString();
        var proto = Strict.Parse<P.Task>(json);

        Assert.Equal("task-1", proto.Id);
        Assert.Equal("ctx-1", proto.ContextId);
        Assert.Equal(P.TaskState.Cancelled, proto.Status.State);
        Assert.Equal("stopped", proto.Status.Update.Content[0].Text);
        Assert.Equal(P.Role.Agent, proto.Status.Update.Role);
        Assert.Equal(new DateTimeOffset(2025, 10, 27, 10, 0, 0, TimeSpan.Zero), proto.Status.Timestamp.ToDateTimeOffset());
        var parts = proto.Artifacts[0].Parts;
        Assert.Equal("42", parts[0].Text);
        Assert.Equal(42, parts[1].Data.Data.Fields["answer"].NumberValue);
        Assert.Equal("hello", parts[2].File.FileWithBytes.ToStringUtf8());
        Assert.Equal("text/plain", parts[2].File.MimeType);
        Assert.Equal("https://example.com/f.png", parts[3].File.FileWithUri);
        Assert.Equal("image/png", parts[3].File.MimeType);
        Assert.Equal("v", proto.Artifacts[0].Metadata.Fields["k"].StringValue);
        Assert.Equal("m1", proto.History[0].MessageId);
        Assert.Equal("hi", proto.History[0].Content[0].Text);
        Assert.Equal("test", proto.Metadata.Fields["source"].StringValue);
    }

    [Fact]
    public void A_task_the_proto_formatter_writes_reads_back()
    {
        var proto = new P.Task
        {
            Id = "task-1",
            ContextId = "ctx-1",
            Status = new P.TaskStatus
            {
                State = P.TaskState.InputRequired,
                Update = new P.Message { MessageId = "m2", Role = P.Role.Agent, Content = { new P.Part { Text = "Which city?" } } },
            },
            Artifacts =
            {
                new P.Artifact
                {
                    ArtifactId = "a1",
                    Parts =
                    {
                        new P.Part { Text = "x" },
                        new P.Part { File = new P.FilePart { FileWithUri = "https://example.com/f", MimeType = "text/plain" } },
                        new P.Part { File = new P.FilePart { FileWithBytes = ByteString.CopyFromUtf8("bytes"), MimeType = "application/octet-stream" } },
                        new P.Part { Data = new P.DataPart { Data = Google.Protobuf.WellKnownTypes.Struct.Parser.ParseJson("""{"n":1}""") } },
                    },
                },
            },
        };
        var task = C.ReadTask(JsonNode.Parse(JsonFormatter.Default.Format(proto)));

        Assert.Equal(TaskState.InputRequired, task.Status.State);
        Assert.Equal("Which city?", task.Status.Message!.GetText());
        Assert.Equal(Role.Agent, task.Status.Message.Role);
        var parts = task.Artifacts![0].Parts;
        Assert.Equal("x", Assert.IsType<TextPart>(parts[0]).Text);
        Assert.Equal("https://example.com/f", Assert.IsType<UrlPart>(parts[1]).Url.OriginalString);
        Assert.Equal("bytes", System.Text.Encoding.UTF8.GetString(Assert.IsType<RawPart>(parts[2]).Raw.Span));
        Assert.Equal("""{"n":1}""", Assert.IsType<DataPart>(parts[3]).Data.GetRawText().Replace(" ", ""));
    }

    [Fact]
    public void Wire_shapes_follow_the_proto_json_rules()
    {
        var node = C.WriteTask(SampleTask());
        Assert.Null(node["kind"]);
        Assert.Equal("TASK_STATE_CANCELLED", node["status"]!["state"]!.GetValue<string>()); // two Ls
        Assert.Equal("ROLE_AGENT", node["status"]!["message"]!["role"]!.GetValue<string>());
        Assert.NotNull(node["status"]!["message"]!["content"]); // the proto calls a message's parts "content"
        var parts = node["artifacts"]![0]!["parts"]!.AsArray();
        Assert.True(Nodes.Same(Nodes.Parse("""{"text":"42"}"""), parts[0]));
        Assert.True(Nodes.Same(Nodes.Parse("""{"data":{"data":{"answer":42}}}"""), parts[1]));
        Assert.Equal("aGVsbG8=", parts[2]!["file"]!["fileWithBytes"]!.GetValue<string>());
        Assert.Equal("https://example.com/f.png", parts[3]!["file"]!["fileWithUri"]!.GetValue<string>());
    }

    [Fact]
    public void Both_spellings_of_canceled_are_read_and_content_or_parts_is_accepted()
    {
        Assert.Equal(TaskState.Canceled, C.ReadStatus(Nodes.Parse("""{"state":"TASK_STATE_CANCELLED"}""")).State);
        Assert.Equal(TaskState.Canceled, C.ReadStatus(Nodes.Parse("""{"state":"TASK_STATE_CANCELED"}""")).State);
        Assert.Equal("a", C.ReadMessage(Nodes.Parse("""{"messageId":"m","role":"ROLE_USER","parts":[{"text":"a"}]}""")).GetText());
        Assert.Equal("a", C.ReadMessage(Nodes.Parse("""{"messageId":"m","role":"ROLE_USER","content":[{"text":"a"}]}""")).GetText());
        Assert.Throws<JsonException>(() => C.ReadStatus(Nodes.Parse("""{"state":"working"}""")));
    }

    [Fact]
    public void Dropped_fields_are_reported_and_non_object_data_is_wrapped()
    {
        using var recorder = new MeterRecorder();
        var file = C.WritePartNode(Part.FromBytes(new byte[] { 1 }, "application/octet-stream", "name.bin"));
        Assert.Null(file["file"]!["name"]);
        var text = C.WritePartNode(Part.FromText("a") with { Metadata = new() { ["k"] = El("1") } });
        Assert.Null(text["metadata"]);
        var data = C.WritePartNode(Part.FromData(El("[1]")));
        Assert.True(Nodes.Same(Nodes.Parse("""{"data":{"data":{"value":[1]}}}"""), data));
        var message = C.WriteMessage(new Message { MessageId = "m", Role = Role.User, Parts = [Part.FromText("a")], ReferenceTaskIds = ["t"] });
        Assert.Null(message["referenceTaskIds"]);

        var fields = recorder.DroppedFields().ToList();
        Assert.Contains("part.filename", fields);
        Assert.Contains("part.metadata", fields);
        Assert.Contains("message.referenceTaskIds", fields);
    }

    [Fact]
    public void A_part_must_set_exactly_one_member()
    {
        Assert.Throws<JsonException>(() => C.ReadPartNode(Nodes.Parse("{}")));
        Assert.Throws<JsonException>(() => C.ReadPartNode(Nodes.Parse("""{"text":"a","data":{"data":{}}}""")));
        Assert.Throws<JsonException>(() => C.ReadPartNode(Nodes.Parse("""{"file":{"fileWithUri":"https://x","fileWithBytes":"YQ=="}}""")));
    }

    [Fact]
    public void Stream_events_are_wrapped_and_parse_as_the_proto_stream_response()
    {
        var status = StreamResponse.Of(new TaskStatusUpdateEvent
        {
            TaskId = "t", ContextId = "c", Status = new AgentTaskStatus { State = TaskState.InputRequired },
        });
        var proto = Strict.Parse<P.StreamResponse>(C.WriteStreamEvent(status).ToJsonString());
        Assert.Equal(P.StreamResponse.PayloadOneofCase.StatusUpdate, proto.PayloadCase);
        Assert.True(proto.StatusUpdate.Final); // interrupted states close the stream
        Assert.Equal(P.TaskState.InputRequired, proto.StatusUpdate.Status.State);

        var working = Strict.Parse<P.StreamResponse>(C.WriteStreamEvent(StreamResponse.Of(new TaskStatusUpdateEvent
        {
            TaskId = "t", ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working },
        })).ToJsonString());
        Assert.False(working.StatusUpdate.Final);

        var artifact = StreamResponse.Of(new TaskArtifactUpdateEvent
        {
            TaskId = "t", ContextId = "c", Artifact = new Artifact { ArtifactId = "a", Parts = [Part.FromText("x")] }, Append = true, LastChunk = true,
        });
        var artifactProto = Strict.Parse<P.StreamResponse>(C.WriteStreamEvent(artifact).ToJsonString());
        Assert.Equal(P.StreamResponse.PayloadOneofCase.ArtifactUpdate, artifactProto.PayloadCase);
        Assert.True(artifactProto.ArtifactUpdate.Append);
        Assert.True(artifactProto.ArtifactUpdate.LastChunk);

        Assert.Equal(P.StreamResponse.PayloadOneofCase.Task, Strict.Parse<P.StreamResponse>(C.WriteStreamEvent(StreamResponse.Of(SampleTask())).ToJsonString()).PayloadCase);
        Assert.Equal(P.StreamResponse.PayloadOneofCase.Msg, Strict.Parse<P.StreamResponse>(C.WriteStreamEvent(StreamResponse.Of(Message.FromAgent("hi"))).ToJsonString()).PayloadCase);
    }

    [Fact]
    public void Events_the_proto_formatter_writes_read_back_with_final()
    {
        var proto = new P.StreamResponse
        {
            StatusUpdate = new P.TaskStatusUpdateEvent { TaskId = "t", ContextId = "c", Final = true, Status = new P.TaskStatus { State = P.TaskState.Completed } },
        };
        var evt = C.ReadStreamEvent(JsonNode.Parse(JsonFormatter.Default.Format(proto))!, out var final);
        Assert.True(final);
        Assert.Equal(TaskState.Completed, Assert.IsType<StatusUpdateEvent>(evt).StatusUpdate.Status.State);

        var message = new P.StreamResponse { Msg = new P.Message { MessageId = "m", Role = P.Role.Agent, Content = { new P.Part { Text = "hi" } } } };
        Assert.IsType<MessageEvent>(C.ReadStreamEvent(JsonNode.Parse(JsonFormatter.Default.Format(message))!, out _));
    }

    [Fact]
    public void Send_result_is_wrapped_and_parses_as_the_proto_send_message_response()
    {
        var task = Strict.Parse<P.SendMessageResponse>(C.WriteSendResult(new TaskResponse(SampleTask())).ToJsonString());
        Assert.Equal(P.SendMessageResponse.PayloadOneofCase.Task, task.PayloadCase);
        var message = Strict.Parse<P.SendMessageResponse>(C.WriteSendResult(new MessageResponse(Message.FromAgent("hi"))).ToJsonString());
        Assert.Equal("hi", message.Msg.Content[0].Text);

        Assert.IsType<MessageResponse>(C.ReadSendResult(JsonNode.Parse(JsonFormatter.Default.Format(message))!));
    }

    [Fact]
    public void Send_request_parses_as_the_proto_and_the_proto_request_reads_back()
    {
        var request = new SendMessageRequest
        {
            Message = Message.FromUser("hello", "ctx", null),
            Configuration = new SendMessageConfiguration
            {
                AcceptedOutputModes = ["text/plain"],
                HistoryLength = 4,
                ReturnImmediately = true,
                TaskPushNotificationConfig = new TaskPushNotificationConfig
                {
                    Id = "c1", Url = "https://hooks.example/x", Token = "tok", Authentication = new AuthenticationInfo { Scheme = "Bearer", Credentials = "cred" },
                },
            },
            Metadata = new() { ["m"] = El("1") },
        };
        var proto = Strict.Parse<P.SendMessageRequest>(C.WriteSendRequest(request).ToJsonString());
        Assert.Equal("hello", proto.Request.Content[0].Text);
        Assert.Equal(P.Role.User, proto.Request.Role);
        Assert.False(proto.Configuration.Blocking); // ReturnImmediately: not blocking
        Assert.Equal(4, proto.Configuration.HistoryLength);
        Assert.Equal("https://hooks.example/x", proto.Configuration.PushNotification.Url);
        Assert.Equal("Bearer", proto.Configuration.PushNotification.Authentication.Schemes[0]);

        // ProtoJSON omits false, so a plain formatter drops "blocking": false. On HTTP+JSON absent means not blocking, so it still
        // reads as ReturnImmediately, like the 0.3 Python client sends it.
        var explicitFalse = new JsonFormatter(JsonFormatter.Settings.Default.WithFormatDefaultValues(true));
        var back = C.ReadSendRequest(JsonNode.Parse(explicitFalse.Format(proto)));
        Assert.True(back.Configuration!.ReturnImmediately);
        Assert.True(C.ReadSendRequest(JsonNode.Parse(JsonFormatter.Default.Format(proto))).Configuration!.ReturnImmediately);
        proto.Configuration.Blocking = true;
        Assert.False(C.ReadSendRequest(JsonNode.Parse(JsonFormatter.Default.Format(proto))).Configuration!.ReturnImmediately);
        Assert.Equal("c1", back.Configuration.TaskPushNotificationConfig!.Id);
        Assert.Equal("hello", back.Message!.GetText());
    }

    [Fact]
    public void Push_config_name_carries_the_task_and_config_id()
    {
        var config = new TaskPushNotificationConfig { TaskId = "task-1", Id = "cfg-1", Url = "https://hooks.example/x" };
        var node = C.WritePushConfig(config);
        Assert.Equal("tasks/task-1/pushNotificationConfigs/cfg-1", node["name"]!.GetValue<string>());
        var proto = Strict.Parse<P.TaskPushNotificationConfig>(node.ToJsonString());
        Assert.Equal("cfg-1", proto.PushNotificationConfig.Id);

        var back = C.ReadPushConfig(JsonNode.Parse(JsonFormatter.Default.Format(proto))!);
        Assert.Equal("task-1", back.TaskId);
        Assert.Equal("cfg-1", back.Id);
        Assert.Throws<JsonException>(() => C.ReadPushConfig(Nodes.Parse("""{"name":"nope","pushNotificationConfig":{"url":"https://x"}}""")));
    }

    [Fact]
    public void Error_body_has_a_numeric_code_and_the_message()
    {
        var body = V03ProtoJsonCodec.WriteError(A2AException.TaskNotFound("t"), 404);
        Assert.Equal(-32001, body["code"]!.GetValue<int>());
        Assert.NotNull(body["message"]);
        Assert.Equal(401, V03ProtoJsonCodec.WriteError(new A2AException(A2AErrorCode.Unauthenticated), 401)["code"]!.GetValue<int>());
    }
}
