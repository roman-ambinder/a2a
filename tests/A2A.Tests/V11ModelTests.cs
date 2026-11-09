using System.Text.Json;
using A2A.Json;
using A2A.Server.AspNetCore;

namespace A2A.Tests;

/// <summary>A2A 1.1 (release candidate) data model: wire shape, forward compatibility, copies and the new error code.</summary>
public sealed class V11ModelTests
{
    private static JsonElement J(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Message Msg(string id, Role role = Role.User) => new() { MessageId = id, Role = role, Parts = [Part.FromText(id)] };

    private static AgentTask FullTask() => new()
    {
        Id = "t",
        ContextId = "c",
        Status = new() { State = TaskState.Working, Message = Msg("s", Role.Agent), Timestamp = DateTimeOffset.UnixEpoch },
        History = [Msg("h1")],
        Artifacts =
        [
            new Artifact { ArtifactId = "a", Parts = [Part.FromText("x")], StartGeneration = 2, EndGeneration = 3, Role = Role.User },
        ],
        Generation = 4,
        Timeline =
        [
            TimelineEntry.FromMessage(Msg("h1"), 1),
            TimelineEntry.FromStatus(new AgentTaskStatus { State = TaskState.Working, Message = Msg("s", Role.Agent) }, 4),
        ],
    };

    [Fact]
    public void Generation_is_written_as_a_string_and_read_from_a_string_or_a_number()
    {
        var json = A2AJson.Serialize(new AgentTask { Id = "t", Status = new() { State = TaskState.Working }, Generation = 9007199254740993 });
        Assert.Contains("\"generation\":\"9007199254740993\"", json, StringComparison.Ordinal);

        Assert.Equal(7, A2AJson.Deserialize<AgentTask>("""{"id":"t","status":{"state":"TASK_STATE_WORKING"},"generation":7}""").Generation);
        Assert.Equal(7, A2AJson.Deserialize<AgentTask>("""{"id":"t","status":{"state":"TASK_STATE_WORKING"},"generation":"7"}""").Generation);
        Assert.Null(A2AJson.Deserialize<AgentTask>("""{"id":"t","status":{"state":"TASK_STATE_WORKING"}}""").Generation);
    }

    [Theory]
    [InlineData("\"x\"")]
    [InlineData("1.5")]
    [InlineData("\"99999999999999999999\"")]
    [InlineData("true")]
    public void Generation_that_is_not_a_64_bit_integer_is_refused(string value)
    {
        Assert.ThrowsAny<JsonException>(() =>
            A2AJson.Deserialize<AgentTask>($$"""{"id":"t","status":{"state":"TASK_STATE_WORKING"},"generation":{{value}}}"""));
    }

    [Fact]
    public void A_task_without_1_1_fields_has_the_1_0_shape()
    {
        var json = A2AJson.Serialize(new AgentTask
        {
            Id = "t",
            Status = new() { State = TaskState.Completed },
            Artifacts = [new Artifact { ArtifactId = "a", Parts = [Part.FromText("x")] }],
        });
        Assert.DoesNotContain("generation", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timeline", json, StringComparison.Ordinal);
        Assert.DoesNotContain("role", json, StringComparison.Ordinal);
        Assert.Equal(
            """{"id":"t","contextId":"","status":{"state":"TASK_STATE_COMPLETED"},"artifacts":[{"artifactId":"a","parts":[{"text":"x"}]}]}""",
            json);
    }

    [Fact]
    public void Task_with_timeline_and_artifact_placement_round_trips()
    {
        var task = FullTask();
        var json = A2AJson.Serialize(task);
        Assert.Contains("\"timeline\":[{\"message\":", json, StringComparison.Ordinal);
        Assert.Contains("\"startGeneration\":\"2\",\"endGeneration\":\"3\",\"role\":\"ROLE_USER\"", json, StringComparison.Ordinal);
        Assert.Equal(json, A2AJson.Serialize(A2AJson.Deserialize<AgentTask>(json)));
    }

    [Fact]
    public void Timeline_entry_of_an_unknown_kind_is_kept_and_marked()
    {
        var task = A2AJson.Deserialize<AgentTask>(
            """{"id":"t","status":{"state":"TASK_STATE_WORKING"},"timeline":[{"elicitation":{"x":1},"generation":"3"},{"message":{"messageId":"m","role":"ROLE_USER","parts":[{"text":"hi"}]},"generation":"4"}]}""");
        Assert.Equal(2, task.Timeline!.Count);
        Assert.True(task.Timeline[0].IsUnknownKind);
        Assert.Equal(3, task.Timeline[0].Generation);
        Assert.False(task.Timeline[1].IsUnknownKind);
        Assert.Equal("m", task.Timeline[1].Message!.MessageId);
    }

    [Fact]
    public void Message_update_and_stream_error_events_round_trip()
    {
        StreamResponse update = StreamResponse.Of(new TaskMessageUpdateEvent { TaskId = "t", ContextId = "c", Message = Msg("m"), Generation = 5 });
        var json = A2AJson.Serialize(update);
        Assert.Equal(
            """{"messageUpdate":{"taskId":"t","contextId":"c","message":{"messageId":"m","role":"ROLE_USER","parts":[{"text":"m"}]},"generation":"5"}}""",
            json);
        var read = Assert.IsType<MessageUpdateEvent>(A2AJson.Deserialize<StreamResponse>(json));
        Assert.Equal(5, read.MessageUpdate.Generation);
        Assert.Equal("t", read.TaskId);

        StreamResponse error = StreamResponse.Of(new StreamError { TaskId = "t", Code = "UNAVAILABLE", Message = "busy", ReferenceId = "m" });
        json = A2AJson.Serialize(error);
        Assert.Equal("""{"error":{"taskId":"t","code":"UNAVAILABLE","message":"busy","referenceId":"m"}}""", json);
        var err = Assert.IsType<StreamErrorEvent>(A2AJson.Deserialize<StreamResponse>(json));
        Assert.Equal("UNAVAILABLE", err.Error.Code);
        Assert.Equal("t", err.TaskId);
    }

    [Fact]
    public void Status_and_artifact_events_carry_generation()
    {
        var status = A2AJson.Deserialize<StreamResponse>(
            """{"statusUpdate":{"taskId":"t","contextId":"c","status":{"state":"TASK_STATE_WORKING"},"generation":"2"}}""");
        Assert.Equal(2, Assert.IsType<StatusUpdateEvent>(status).StatusUpdate.Generation);
        var artifact = A2AJson.Deserialize<StreamResponse>(
            """{"artifactUpdate":{"taskId":"t","contextId":"c","artifact":{"artifactId":"a","parts":[{"text":"x"}]},"generation":3}}""");
        Assert.Equal(3, Assert.IsType<ArtifactUpdateEvent>(artifact).ArtifactUpdate.Generation);
    }

    [Fact]
    public void Stream_request_reads_both_kinds_and_marks_an_unknown_one()
    {
        var send = A2AJson.Deserialize<StreamRequest>(
            """{"tenant":"x","sendMessage":{"message":{"messageId":"m","role":"ROLE_USER","parts":[{"text":"hi"}]},"configuration":{"ifGenerationMatch":"4","timelineLength":2}}}""");
        Assert.Equal("x", send.Tenant);
        Assert.Equal(4, send.SendMessage!.Configuration!.IfGenerationMatch);
        Assert.Equal(2, send.SendMessage.Configuration.TimelineLength);
        Assert.False(send.IsUnknownKind);

        var subscribe = A2AJson.Deserialize<StreamRequest>("""{"subscribe":{"id":"t","lastSeenGeneration":"9"}}""");
        Assert.Equal(9, subscribe.Subscribe!.LastSeenGeneration);

        Assert.True(A2AJson.Deserialize<StreamRequest>("""{"tenant":"x","cancel":{"id":"t"}}""").IsUnknownKind);
    }

    [Fact]
    public void Request_fields_use_the_proto_json_names()
    {
        Assert.Equal("""{"id":"t","timelineLength":3,"currentGeneration":"5"}""",
            A2AJson.Serialize(new GetTaskRequest { Id = "t", TimelineLength = 3, CurrentGeneration = 5 }));
        Assert.Equal("""{"timelineLength":1}""", A2AJson.Serialize(new ListTasksRequest { TimelineLength = 1 }));
        Assert.Equal("""{"streaming":true,"userArtifacts":true}""", A2AJson.Serialize(new AgentCapabilities { Streaming = true, UserArtifacts = true }));
        Assert.Equal("""{"url":"https://x","protocolVersion":"1.1"}""", A2AJson.Serialize(new TaskPushNotificationConfig { Url = "https://x", ProtocolVersion = "1.1" }));

        var request = new SendMessageRequest
        {
            Message = Msg("m"),
            ArtifactUpdates = [new TaskArtifactUpdateEvent { TaskId = "t", Artifact = new Artifact { ArtifactId = "a", Parts = [Part.FromText("x")] } }],
        };
        Assert.Contains("\"artifactUpdates\":[{\"taskId\":\"t\",\"contextId\":\"\",\"artifact\":", A2AJson.Serialize(request), StringComparison.Ordinal);
    }

    [Fact]
    public void Copies_keep_every_1_1_field()
    {
        var task = FullTask();
        Assert.Equal(A2AJson.Serialize(task), A2AJson.Serialize(task.DeepCopy()));
        Assert.Equal(A2AJson.Serialize(task), A2AJson.Serialize(task.CopyForUpdate()));
        var copy = task.CopyForUpdate();
        Assert.NotSame(task.Timeline, copy.Timeline);
        Assert.Same(task.Timeline![0], copy.Timeline![0]);
        var deep = task.DeepCopy();
        Assert.NotSame(task.Timeline[0], deep.Timeline![0]);
        Assert.NotSame(task.Timeline[0].Message, deep.Timeline[0].Message);

        var artifact = task.Artifacts![0];
        var grown = artifact.CopyWithParts([Part.FromText("y")]);
        Assert.Equal((2L, 3L, Role.User), (grown.StartGeneration!.Value, grown.EndGeneration!.Value, grown.Role!.Value));

        var config = new TaskPushNotificationConfig { Url = "https://x", ProtocolVersion = "1.1" };
        Assert.Equal("1.1", config.DeepCopy().ProtocolVersion);

        StreamResponse[] events =
        [
            StreamResponse.Of(new TaskMessageUpdateEvent { TaskId = "t", ContextId = "c", Message = Msg("m"), Metadata = new() { ["k"] = J("1") }, Generation = 3 }),
            StreamResponse.Of(new StreamError { TaskId = "t", Code = "INVALID_ARGUMENT", ReferenceId = "m", Metadata = new() { ["k"] = J("1") } }),
            StreamResponse.Of(new TaskStatusUpdateEvent { TaskId = "t", Status = new() { State = TaskState.Working }, Generation = 4 }),
            StreamResponse.Of(new TaskArtifactUpdateEvent { TaskId = "t", Artifact = artifact, Generation = 5 }),
        ];
        foreach (var e in events)
        {
            var c = e.DeepCopy();
            Assert.NotSame(e, c);
            Assert.Equal(A2AJson.Serialize(e), A2AJson.Serialize(c));
        }
    }

    [Fact]
    public void Generation_mismatch_maps_to_409_aborted()
    {
        Assert.Equal(-32010, (int)A2AErrorCode.TaskGenerationMismatch);
        Assert.Equal(409, A2AErrors.HttpStatus(A2AErrorCode.TaskGenerationMismatch));
        Assert.Equal("ABORTED", A2AErrors.GrpcStatus(A2AErrorCode.TaskGenerationMismatch));
        Assert.Equal("TASK_GENERATION_MISMATCH", A2AErrors.Reason(A2AErrorCode.TaskGenerationMismatch));
        Assert.True(A2AErrors.TryParseReason("TASK_GENERATION_MISMATCH", out var code));
        Assert.Equal(A2AErrorCode.TaskGenerationMismatch, code);
        Assert.False(A2AErrors.DefaultRetryable(A2AErrorCode.TaskGenerationMismatch));
    }

    [Theory]
    [InlineData("https://agent.example.com", "agent.example.com:443")]
    [InlineData("https://agent.example.com:8443", "agent.example.com:8443")]
    [InlineData("https://[::1]:5001", "[::1]:5001")]
    [InlineData("https://[::1]", "[::1]:443")]
    [InlineData("http://localhost:5000", "http://localhost:5000")]
    [InlineData("https://agent.example.com/api", "https://agent.example.com/api")]
    public void Grpc_interface_address_is_host_and_port_for_tls(string url, string expected) =>
        Assert.Equal(expected, AgentCardCompleter.GrpcAddress(url));

    [Theory]
    [InlineData(null, A2AProtocol.JsonMediaType)]
    [InlineData("application/json", A2AProtocol.JsonMediaType)]
    [InlineData("*/*", A2AProtocol.JsonMediaType)]
    [InlineData("application/a2a-agent-card+json", A2AProtocol.AgentCardMediaType)]
    [InlineData("application/a2a-agent-card+json, application/json;q=0.9", A2AProtocol.AgentCardMediaType)]
    [InlineData("application/a2a-agent-card+json;q=0, application/json", A2AProtocol.JsonMediaType)]
    public async Task Agent_card_media_type_follows_the_accept_header(string? accept, string expected)
    {
        await using var host = await TestAgentHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, A2AProtocol.WellKnownAgentCardPath);
        if (accept is not null) request.Headers.TryAddWithoutValidation("Accept", accept);
        using var response = await host.Http().SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.Equal(expected, response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Accept", response.Headers.Vary);
        A2AJson.Deserialize<AgentCard>(await response.Content.ReadAsStringAsync());
    }
}
