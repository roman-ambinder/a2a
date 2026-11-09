using System.Text.Json;
using A2A.Client;
using A2A.Client.Grpc;
using Grpc.Core;
using A2aPb = Lf.A2a.V1;

namespace A2A.Grpc.Tests;

public class ProtoMapperTests
{
    private static readonly Google.Protobuf.JsonFormatter OldFormatter = new(Google.Protobuf.JsonFormatter.Settings.Default.WithFormatDefaultValues(false));
    private static readonly Google.Protobuf.JsonParser OldParser = new(Google.Protobuf.JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private static T OldToModel<T>(Google.Protobuf.IMessage proto) => A2A.Json.A2AJson.Deserialize<T>(OldFormatter.Format(proto));

    private static TProto OldToProto<TProto>(object model) where TProto : Google.Protobuf.IMessage<TProto>, new() =>
        OldParser.Parse<TProto>(JsonSerializer.Serialize(model, A2A.Json.A2AJson.Options.GetTypeInfo(model.GetType())));

    [Fact]
    public void Round_trip_equals_previous_mapping()
    {
        var task = new AgentTask
        {
            Id = "t\u00e9-\U0001F600",
            ContextId = "c1",
            Status = new AgentTaskStatus { State = TaskState.Working, Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.Zero), Message = Message.FromAgent("h\u00e9llo \u4e16\u754c \U0001F600 \"q\" \n", "c1", "t\u00e9-\U0001F600") },
            Artifacts = [new Artifact { ArtifactId = "a", Parts = [Part.FromText("x"), Part.FromBytes(new byte[] { 1, 2, 3 }, "application/octet-stream"), Part.FromData(JsonSerializer.SerializeToElement(new { n = 1, s = "\u00fc" })), Part.FromUrl(new Uri("https://x/y"))] }],
            Metadata = new() { ["k"] = JsonSerializer.SerializeToElement(new { deep = true }) },
        };
        var oldProto = OldToProto<A2aPb.Task>(task);
        var newProto = ProtoMapper.ToProto<A2aPb.Task, AgentTask>(task);
        Assert.Equal(oldProto, newProto);
        Assert.Equal(
            A2A.Json.A2AJson.Serialize(OldToModel<AgentTask>(oldProto)),
            A2A.Json.A2AJson.Serialize(ProtoMapper.ToModel<AgentTask>(newProto)));

        var card = GrpcHost.Card();
        card.Capabilities.PushNotifications = false;
        var oldCard = OldToProto<A2aPb.AgentCard>(card);
        var newCard = ProtoMapper.ToProto<A2aPb.AgentCard, AgentCard>(card);
        Assert.Equal(oldCard, newCard);
        Assert.Equal(
            A2A.Json.A2AJson.Serialize(OldToModel<AgentCard>(oldCard)),
            A2A.Json.A2AJson.Serialize(ProtoMapper.ToModel<AgentCard>(newCard)));
    }

    [Fact]
    public void V11_fields_and_events_round_trip_through_proto()
    {
        var task = new AgentTask
        {
            Id = "t",
            ContextId = "c",
            Status = new AgentTaskStatus { State = TaskState.InputRequired, Message = Message.FromAgent("more?", "c", "t") },
            Artifacts = [new Artifact { ArtifactId = "a", Parts = [Part.FromText("x")], StartGeneration = 2, EndGeneration = 3, Role = Role.User }],
            Generation = 4,
            Timeline =
            [
                TimelineEntry.FromMessage(Message.FromUser("hi", "c", "t"), 1),
                TimelineEntry.FromStatus(new AgentTaskStatus { State = TaskState.InputRequired, Message = Message.FromAgent("more?", "c", "t") }, 4),
            ],
        };
        var proto = ProtoMapper.ToProto<A2aPb.Task, AgentTask>(task);
        Assert.Equal(4, proto.Generation);
        Assert.Equal(2, proto.Timeline.Count);
        Assert.Equal(A2aPb.TimelineEntry.EntryOneofCase.Message, proto.Timeline[0].EntryCase);
        Assert.Equal(A2aPb.Role.User, proto.Artifacts[0].Role);
        Assert.Equal(A2A.Json.A2AJson.Serialize(task), A2A.Json.A2AJson.Serialize(ProtoMapper.ToModel<AgentTask>(proto)));

        StreamResponse[] events =
        [
            StreamResponse.Of(new TaskMessageUpdateEvent { TaskId = "t", ContextId = "c", Message = Message.FromUser("hi", "c", "t"), Generation = 5 }),
            StreamResponse.Of(new StreamError { TaskId = "t", Code = "UNAVAILABLE", Message = "busy", ReferenceId = "m" }),
            StreamResponse.Of(new TaskStatusUpdateEvent { TaskId = "t", ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working }, Generation = 6 }),
        ];
        foreach (var e in events)
        {
            var p = ProtoMapper.ToProto<A2aPb.StreamResponse, StreamResponse>(e);
            Assert.Equal(A2A.Json.A2AJson.Serialize(e), A2A.Json.A2AJson.Serialize(ProtoMapper.ToModel<StreamResponse>(p)));
        }

        var request = new StreamRequest { Tenant = "x", Subscribe = new SubscribeToTaskRequest { Id = "t", LastSeenGeneration = 7 } };
        var pr = ProtoMapper.ToProto<A2aPb.StreamRequest, StreamRequest>(request);
        Assert.Equal(7, pr.Subscribe.LastSeenGeneration);
        Assert.Equal(A2A.Json.A2AJson.Serialize(request), A2A.Json.A2AJson.Serialize(ProtoMapper.ToModel<StreamRequest>(pr)));
    }

    [Fact]
    public void Task_round_trips_through_proto()
    {
        var task = new AgentTask
        {
            Id = "t1",
            ContextId = "c1",
            Status = new AgentTaskStatus { State = TaskState.InputRequired, Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.Zero), Message = Message.FromAgent("more?", "c1", "t1") },
            Artifacts =
            [
                new Artifact
                {
                    ArtifactId = "a",
                    Parts =
                    [
                        Part.FromText("x"), Part.FromBytes(new byte[] { 1, 2, 3 }, "application/octet-stream"),
                        Part.FromData(JsonSerializer.SerializeToElement(new { n = 1 })), Part.FromUrl(new Uri("https://x/y")),
                    ],
                },
            ],
            Metadata = new() { ["k"] = JsonSerializer.SerializeToElement(new { deep = true }) },
        };
        var proto = ProtoMapper.ToProto<A2aPb.Task, AgentTask>(task);
        Assert.Equal(A2aPb.TaskState.InputRequired, proto.Status.State);
        Assert.Equal(Google.Protobuf.ByteString.CopyFrom(1, 2, 3), proto.Artifacts[0].Parts[1].Raw);

        var back = ProtoMapper.ToModel<AgentTask>(proto);
        Assert.Equal(TaskState.InputRequired, back.Status.State);
        Assert.Equal(task.Status.Timestamp, back.Status.Timestamp);
        Assert.Equal([typeof(TextPart), typeof(RawPart), typeof(DataPart), typeof(UrlPart)], back.Artifacts![0].Parts.Select(p => p.GetType()));
        Assert.True(back.Metadata!["k"].GetProperty("deep").GetBoolean());
        Assert.Equal("more?", back.Status.Message!.GetText());
    }

    [Fact]
    public void Response_unions_keep_their_member()
    {
        var reply = ProtoMapper.ToProto<A2aPb.SendMessageResponse, SendMessageResponse>(new MessageResponse(Message.FromAgent("hi")));
        Assert.Equal(A2aPb.SendMessageResponse.PayloadOneofCase.Message, reply.PayloadCase);
        var back = Assert.IsType<MessageResponse>(ProtoMapper.ToModel<SendMessageResponse>(reply));
        Assert.Equal("hi", back.Message.GetText());

        var evt = ProtoMapper.ToProto<A2aPb.StreamResponse, StreamResponse>(StreamResponse.Of(new TaskStatusUpdateEvent
        {
            TaskId = "t", ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working },
        }));
        Assert.Equal(A2aPb.StreamResponse.PayloadOneofCase.StatusUpdate, evt.PayloadCase);
        Assert.IsType<StatusUpdateEvent>(ProtoMapper.ToModel<StreamResponse>(evt));
    }

    [Fact]
    public void Agent_card_keeps_explicit_optional_false()
    {
        var card = GrpcHost.Card();
        card.Capabilities.PushNotifications = false;
        var proto = ProtoMapper.ToProto<A2aPb.AgentCard, AgentCard>(card);
        Assert.True(proto.Capabilities.HasPushNotifications);
        Assert.False(ProtoMapper.ToModel<AgentCard>(proto).Capabilities.PushNotifications!.Value);
    }

    [Fact]
    public void Error_round_trips_through_rpc_status()
    {
        var rpc = GrpcErrors.ToRpcException(A2AException.TaskNotFound("t9"));
        Assert.Equal(StatusCode.NotFound, rpc.StatusCode);
        var back = GrpcErrors.FromRpcException(rpc);
        Assert.Equal(A2AErrorCode.TaskNotFound, back.Code);
        Assert.Equal("t9", back.Metadata!["taskId"]);
        Assert.False(back.IsRetryable);
    }

    [Fact]
    public void Rate_limit_error_carries_retry_info_and_retryable_metadata()
    {
        var rpc = GrpcErrors.ToRpcException(A2AException.RateLimited(TimeSpan.FromSeconds(7)));
        Assert.Equal(StatusCode.ResourceExhausted, rpc.StatusCode);
        var status = rpc.GetRpcStatus()!;
        var info = status.GetDetail<Google.Rpc.ErrorInfo>()!;
        Assert.Equal("RATE_LIMITED", info.Reason);
        Assert.Equal("true", info.Metadata["retryable"]);
        Assert.Equal("7", info.Metadata["retryAfter"]);
        Assert.Equal(7, status.GetDetail<Google.Rpc.RetryInfo>()!.RetryDelay.Seconds);

        var back = GrpcErrors.FromRpcException(rpc);
        Assert.Equal(A2AErrorCode.RateLimited, back.Code);
        Assert.Equal(TimeSpan.FromSeconds(7), back.RetryAfter);
        Assert.True(back.IsRetryable);
    }

    [Theory]
    [InlineData(A2AErrors.PayloadTooLargeReason)]
    [InlineData(A2AErrors.IdempotencyKeyMismatchReason)]
    public void Special_reasons_survive_the_round_trip(string reason)
    {
        var source = reason == A2AErrors.PayloadTooLargeReason ? A2AException.PayloadTooLarge(10) : A2AException.IdempotencyKeyMismatch();
        var rpc = GrpcErrors.ToRpcException(source);
        Assert.Equal(StatusCode.InvalidArgument, rpc.StatusCode);
        Assert.Equal(reason, rpc.GetRpcStatus()!.GetDetail<Google.Rpc.ErrorInfo>()!.Reason);
        var back = GrpcErrors.FromRpcException(rpc);
        Assert.Equal(A2AErrorCode.InvalidRequest, back.Code);
        Assert.Equal(reason, back.ReasonOverride);
    }

    [Fact]
    public void Bare_resource_exhausted_is_payload_too_large_like_http_413()
    {
        // What the gRPC layer sends for a message over the size limit: no A2A ErrorInfo.
        var rpc = new RpcException(new Status(StatusCode.ResourceExhausted, "Received message exceeds the maximum configured message size."));
        var back = GrpcErrors.FromRpcException(rpc);
        Assert.Equal(A2AErrorCode.InvalidRequest, back.Code);
        Assert.Equal(A2AErrors.PayloadTooLargeReason, back.ReasonOverride);
        Assert.Equal(413, back.HttpStatus);
        Assert.False(back.IsRetryable);

        // An A2A rate limit (RESOURCE_EXHAUSTED with ErrorInfo) keeps its own code.
        Assert.Equal(A2AErrorCode.RateLimited, GrpcErrors.FromRpcException(GrpcErrors.ToRpcException(A2AException.RateLimited(TimeSpan.FromSeconds(1)))).Code);
    }

    [Theory]
    [InlineData("grpc.example.com:50051", "https://grpc.example.com:50051/")]
    [InlineData("http://localhost:5000", "http://localhost:5000/")]
    public void Parses_card_grpc_urls(string url, string expected) => Assert.Equal(expected, GrpcTransport.ParseAddress(url).AbsoluteUri);
}

public class GrpcBindingTests : IAsyncLifetime
{
    private GrpcHost _host = null!;

    public async Task InitializeAsync() => _host = await GrpcHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Direct_reply()
    {
        var r = await _host.Client().SendMessageAsync("hello");
        Assert.Equal("hi there", Assert.IsType<MessageResponse>(r).Message.GetText());
    }

    [Fact]
    public async Task Blocking_send_and_get_with_history_length()
    {
        var client = _host.Client();
        var task = Assert.IsType<TaskResponse>(await client.SendMessageAsync("task")).Task;
        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal(["part1", "part2"], task.Artifacts!.Single().Parts.Select(p => ((TextPart)p).Text));
        Assert.Single((await client.GetTaskAsync(task.Id, 1)).History!);
        Assert.Null((await client.GetTaskAsync(task.Id, 0)).History);
    }

    [Fact]
    public async Task Streaming_order()
    {
        var events = new List<StreamResponse>();
        await foreach (var e in _host.Client().SendStreamingMessageAsync(Message.FromUser("task"))) events.Add(e);
        Assert.IsType<TaskEvent>(events[0]);
        Assert.True(Assert.IsType<ArtifactUpdateEvent>(events[3]).ArtifactUpdate.Append);
        Assert.Equal(TaskState.Completed, Assert.IsType<StatusUpdateEvent>(events[^1]).StatusUpdate.Status.State);
    }

    [Fact]
    public async Task Multi_turn_and_terminal_rejection()
    {
        var client = _host.Client();
        var first = Assert.IsType<TaskResponse>(await client.SendMessageAsync("input")).Task;
        Assert.Equal(TaskState.InputRequired, first.Status.State);
        var second = Assert.IsType<TaskResponse>(await client.SendMessageAsync("more", first.ContextId, first.Id)).Task;
        Assert.Equal("got: more", second.Status.Message!.GetText());
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("again", null, first.Id));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
    }

    [Fact]
    public async Task Subscribe_then_cancel()
    {
        var client = _host.Client();
        var task = Assert.IsType<TaskResponse>(await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).Task;
        var first = new TaskCompletionSource<StreamResponse>();
        var states = new List<TaskState>();
        var reader = Task.Run(async () =>
        {
            await foreach (var e in client.SubscribeToTaskAsync(task.Id))
            {
                first.TrySetResult(e);
                if (e is StatusUpdateEvent su) states.Add(su.StatusUpdate.Status.State);
            }
        });
        Assert.Equal(task.Id, Assert.IsType<TaskEvent>(await first.Task.WaitAsync(TimeSpan.FromSeconds(10))).Task.Id);
        Assert.Equal(TaskState.Canceled, (await client.CancelTaskAsync(task.Id)).Status.State);
        await reader.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(TaskState.Canceled, states);
        Assert.Equal(TaskState.Canceled, (await client.CancelTaskAsync(task.Id)).Status.State); // the client turns the repeat cancel into the canceled task
    }

    [Fact]
    public async Task Owner_scoping_and_listing()
    {
        var alice = _host.Client("alice");
        var bob = _host.Client("bob");
        var task = Assert.IsType<TaskResponse>(await alice.SendMessageAsync("task")).Task;
        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() => bob.GetTaskAsync(task.Id))).Code);
        Assert.Empty((await bob.ListTasksAsync(new ListTasksRequest())).Tasks);
        Assert.Equal(task.Id, Assert.Single((await alice.ListTasksAsync(new ListTasksRequest())).Tasks).Id);
    }

    [Fact]
    public async Task Push_config_crud()
    {
        var client = _host.Client("carol");
        var task = Assert.IsType<TaskResponse>(await client.SendMessageAsync("input")).Task;
        var created = await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = task.Id, Url = "https://hooks.example/cb" });
        Assert.Single((await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = task.Id })).Configs);
        await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = task.Id, Id = created.Id! });
        Assert.Empty((await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = task.Id })).Configs);
    }

    [Fact]
    public async Task Extended_card_requires_authentication()
    {
        var anon = await Assert.ThrowsAsync<A2AException>(() => _host.Client(user: null).GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest()));
        Assert.Equal(A2AErrorCode.Unauthenticated, anon.Code);
        Assert.Equal("Extended for dave", (await _host.Client("dave").GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest())).Description);
    }

    [Fact]
    public async Task Missing_version_metadata_is_rejected_and_extensions_are_echoed()
    {
        var raw = _host.Raw();
        var noVersion = await Assert.ThrowsAsync<RpcException>(() => raw.GetTaskAsync(new A2aPb.GetTaskRequest { Id = "x" }).ResponseAsync);
        Assert.Equal(A2AErrorCode.VersionNotSupported, GrpcErrors.FromRpcException(noVersion).Code);

        var headers = new Metadata { { "a2a-version", "1.0" }, { "a2a-extensions", $"{GrpcHost.Extension},urn:other" } };
        var msg = new A2aPb.SendMessageRequest { Message = new A2aPb.Message { MessageId = "m", Role = A2aPb.Role.User, Parts = { new A2aPb.Part { Text = "hello" } } } };
        using var call = raw.SendMessageAsync(msg, headers);
        await call.ResponseAsync;
        Assert.Equal(GrpcHost.Extension, (await call.ResponseHeadersAsync).GetValue("a2a-extensions"));
    }

    [Fact]
    public async Task Repeated_extension_metadata_entries_are_read_like_a_comma_list()
    {
        var headers = new Metadata { { "a2a-version", "1.0" }, { "a2a-extensions", GrpcHost.Extension }, { "a2a-extensions", "urn:other" } };
        var msg = new A2aPb.SendMessageRequest { Message = new A2aPb.Message { MessageId = "m", Role = A2aPb.Role.User, Parts = { new A2aPb.Part { Text = "hello" } } } };
        using var call = _host.Raw().SendMessageAsync(msg, headers);
        await call.ResponseAsync;
        Assert.Equal(GrpcHost.Extension, (await call.ResponseHeadersAsync).GetValue("a2a-extensions"));
    }

    [Fact]
    public async Task Streaming_call_echoes_extensions_in_initial_metadata()
    {
        var headers = new Metadata { { "a2a-version", "1.0" }, { "a2a-extensions", $"{GrpcHost.Extension},urn:other" } };
        var msg = new A2aPb.SendMessageRequest { Message = new A2aPb.Message { MessageId = "m", Role = A2aPb.Role.User, Parts = { new A2aPb.Part { Text = "hello" } } } };
        using var call = _host.Raw().SendStreamingMessage(msg, headers);
        Assert.Equal(GrpcHost.Extension, (await call.ResponseHeadersAsync).GetValue("a2a-extensions"));
        Assert.True(await call.ResponseStream.MoveNext(default));
        while (await call.ResponseStream.MoveNext(default)) { }
        Assert.Null(call.GetTrailers().GetValue("a2a-extensions"));
    }

    [Fact]
    public async Task Failed_call_echoes_extensions_in_trailers()
    {
        var headers = new Metadata { { "a2a-version", "1.0" }, { "a2a-extensions", $"{GrpcHost.Extension},urn:other" } };
        using var call = _host.Raw().GetTaskAsync(new A2aPb.GetTaskRequest { Id = "does-not-exist" }, headers);
        var ex = await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync);
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
        Assert.Equal(GrpcHost.Extension, call.GetTrailers().GetValue("a2a-extensions"));
        Assert.Equal(GrpcHost.Extension, ex.Trailers.GetValue("a2a-extensions"));
    }

    [Fact]
    public async Task Failed_call_without_extensions_has_no_echo()
    {
        using var call = _host.Raw().GetTaskAsync(new A2aPb.GetTaskRequest { Id = "does-not-exist" }, new Metadata { { "a2a-version", "1.0" } });
        await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync);
        Assert.Null(call.GetTrailers().GetValue("a2a-extensions"));
    }

    [Fact]
    public async Task Invalid_request_maps_to_invalid_argument()
    {
        var bad = new A2aPb.SendMessageRequest { Message = new A2aPb.Message { MessageId = "m", Role = A2aPb.Role.User } };
        var ex = await Assert.ThrowsAsync<RpcException>(() => _host.Raw().SendMessageAsync(bad, new Metadata { { "a2a-version", "1.0" } }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }
}

/// <summary>Selecting gRPC from the Agent Card through <see cref="A2AClientFactory"/> and <see cref="A2AGrpcClientFactory"/>.</summary>
public class GrpcClientFactoryTests
{
    [Fact]
    public void Factory_selects_grpc_only_when_enabled()
    {
        var card = GrpcHost.Card();
        card.SupportedInterfaces =
        [
            new AgentInterface { Url = "grpc.example.com:50051", ProtocolBinding = ProtocolBindings.Grpc },
            new AgentInterface { Url = "https://agent.example.com/a2a", ProtocolBinding = ProtocolBindings.JsonRpc },
        ];

        Assert.Equal(ProtocolBindings.JsonRpc, A2AClientFactory.SelectInterface(card, new A2AClientOptions())!.ProtocolBinding);
        var withGrpc = new A2AClientOptions().UseGrpc();
        Assert.Equal(ProtocolBindings.Grpc, A2AClientFactory.SelectInterface(card, withGrpc)!.ProtocolBinding);

        using var http = new HttpClient();
        Assert.NotNull(A2AClientFactory.Create(card, http, withGrpc));
    }

    [Fact]
    public void Grpc_only_factory_rejects_cards_without_grpc()
    {
        var card = GrpcHost.Card();
        card.SupportedInterfaces = [new AgentInterface { Url = "https://agent.example.com/a2a", ProtocolBinding = ProtocolBindings.JsonRpc }];
        Assert.Equal(A2AErrorCode.UnsupportedOperation, Assert.Throws<A2AException>(() => A2AGrpcClientFactory.Create(card)).Code);
    }

    [Fact]
    public async Task Idempotency_key_option_deduplicates_end_to_end()
    {
        await using var host = await GrpcHost.StartAsync();
        var client = host.Client();
        var options = new A2ARequestOptions { IdempotencyKey = "key-1" };
        var message = Message.FromUser("task");
        var first = Assert.IsType<TaskResponse>(await client.SendMessageAsync(message, options: options)).Task;
        var again = Assert.IsType<TaskResponse>(await client.SendMessageAsync(message, options: options)).Task;
        Assert.Equal(first.Id, again.Id);
    }
}
