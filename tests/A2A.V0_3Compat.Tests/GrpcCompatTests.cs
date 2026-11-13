using A2A.Server;
using System.Text.Json.Nodes;
using A2A.Client;
using A2A.Client.Grpc.V0_3Compat;
using A2A.Client.V0_3Compat;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.TestHost;
using P = A2a.V1;

namespace A2A.V0_3Compat.Tests;

/// <summary>
/// v0.3 over gRPC: the service <c>a2a.v1.A2AService</c> of the published v0.3 proto. The server is called with the
/// client stub generated from that proto here (an independent v0.3 peer), and with this SDK's v0.3 gRPC transport.
/// </summary>
public class GrpcCompatTests
{
    private static readonly JsonParser Strict = new(JsonParser.Settings.Default); // unknown fields are an error

    private static Task<V03Host> StartAsync(Action<AgentCard>? card = null) =>
        V03Host.StartAsync(grpc: true, configure: o =>
        {
            o.PushNotifications.AllowInsecureUrls = true;
            o.PushNotifications.AllowPrivateNetworks = true;
            o.AgentCard!.SupportedInterfaces.Add(new AgentInterface { Url = "http://localhost/", ProtocolBinding = ProtocolBindings.Grpc });
            card?.Invoke(o.AgentCard);
            var extended = o.ExtendedAgentCardProvider!;
            o.ExtendedAgentCardProvider = async (call, c, ct) =>
            {
                var e = await extended(call, c, ct);
                e!.SupportedInterfaces.Add(new AgentInterface { Url = "http://localhost/", ProtocolBinding = ProtocolBindings.Grpc });
                return e;
            };
        });

    private static P.A2AService.A2AServiceClient Stub(V03Host host, string? user = "alice") => new(host.GrpcChannel(user));

    // proto3 has no "unset" bool: like the v0.3 Python server, a send without blocking: true does not wait.
    private static P.SendMessageRequest Send(string text, string? taskId = null) => new()
    {
        Configuration = new P.SendMessageConfiguration { Blocking = true },
        Request = new P.Message
        {
            MessageId = Guid.NewGuid().ToString(), Role = P.Role.User, TaskId = taskId ?? "",
            Content = { new P.Part { Text = text } },
        },
    };

    // ---- the server, called with the published proto's stub ----

    [Fact]
    public async Task SendMessage_answers_a_message_or_a_task()
    {
        await using var host = await StartAsync();
        var stub = Stub(host);

        var hello = await stub.SendMessageAsync(Send("hello"));
        Assert.Equal(P.SendMessageResponse.PayloadOneofCase.Msg, hello.PayloadCase);
        Assert.Equal("hi there", hello.Msg.Content[0].Text);

        var task = await stub.SendMessageAsync(Send("task"));
        Assert.Equal(P.TaskState.Completed, task.Task.Status.State);
        Assert.Equal("part1part2", string.Concat(task.Task.Artifacts[0].Parts.Select(p => p.Text)));

        var got = await stub.GetTaskAsync(new P.GetTaskRequest { Name = $"tasks/{task.Task.Id}", HistoryLength = 1 });
        Assert.Equal(task.Task.Id, got.Id);
        Assert.Single(got.History);
    }

    [Fact]
    public async Task Streams_end_with_a_final_status_and_subscribe_follows_a_task()
    {
        await using var host = await StartAsync();
        var stub = Stub(host);

        using var stream = stub.SendStreamingMessage(Send("task"));
        var events = new List<P.StreamResponse>();
        await foreach (var e in stream.ResponseStream.ReadAllAsync()) events.Add(e);
        Assert.Equal(P.StreamResponse.PayloadOneofCase.Task, events[0].PayloadCase);
        Assert.Contains(events, e => e.PayloadCase == P.StreamResponse.PayloadOneofCase.ArtifactUpdate);
        var last = events[^1].StatusUpdate;
        Assert.Equal(P.TaskState.Completed, last.Status.State);
        Assert.True(last.Final);

        var input = (await stub.SendMessageAsync(Send("input"))).Task;
        using var follow = stub.TaskSubscription(new P.TaskSubscriptionRequest { Name = $"tasks/{input.Id}" });
        Assert.True(await follow.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(input.Id, follow.ResponseStream.Current.Task.Id);
        await stub.CancelTaskAsync(new P.CancelTaskRequest { Name = $"tasks/{input.Id}" });
    }

    [Fact]
    public async Task Errors_are_the_v03_python_status_and_name()
    {
        await using var host = await StartAsync();
        var stub = Stub(host);

        var missing = await Assert.ThrowsAsync<RpcException>(() => stub.GetTaskAsync(new P.GetTaskRequest { Name = "tasks/nope" }).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, missing.StatusCode);
        Assert.StartsWith("TaskNotFoundError: ", missing.Status.Detail, StringComparison.Ordinal);

        var anonymous = await Assert.ThrowsAsync<RpcException>(() => Stub(host, user: null).GetTaskAsync(new P.GetTaskRequest { Name = "tasks/nope" }).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, anonymous.StatusCode);

        var v1 = await Assert.ThrowsAsync<RpcException>(() => stub.GetTaskAsync(new P.GetTaskRequest { Name = "tasks/nope" },
            new Metadata { { "a2a-version", "1.0" } }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, v1.StatusCode);
        Assert.StartsWith("VersionNotSupportedError: ", v1.Status.Detail, StringComparison.Ordinal);

        var badName = await Assert.ThrowsAsync<RpcException>(() => stub.GetTaskAsync(new P.GetTaskRequest { Name = "nope" }).ResponseAsync);
        Assert.StartsWith("InvalidParamsError: ", badName.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_configs_are_created_read_listed_and_deleted()
    {
        await using var host = await StartAsync();
        var stub = Stub(host);
        var task = (await stub.SendMessageAsync(Send("input"))).Task;

        var created = await stub.CreateTaskPushNotificationConfigAsync(new P.CreateTaskPushNotificationConfigRequest
        {
            Parent = $"tasks/{task.Id}", ConfigId = "g1",
            Config = new P.TaskPushNotificationConfig { PushNotificationConfig = new P.PushNotificationConfig { Url = "http://hooks.test/x" } },
        });
        Assert.Equal($"tasks/{task.Id}/pushNotificationConfigs/g1", created.Name);

        var got = await stub.GetTaskPushNotificationConfigAsync(new P.GetTaskPushNotificationConfigRequest { Name = created.Name });
        Assert.Equal("http://hooks.test/x", got.PushNotificationConfig.Url);
        var list = await stub.ListTaskPushNotificationConfigAsync(new P.ListTaskPushNotificationConfigRequest { Parent = $"tasks/{task.Id}" });
        Assert.Equal(created.Name, Assert.Single(list.Configs).Name);
        await stub.DeleteTaskPushNotificationConfigAsync(new P.DeleteTaskPushNotificationConfigRequest { Name = created.Name });
        Assert.Empty((await stub.ListTaskPushNotificationConfigAsync(new P.ListTaskPushNotificationConfigRequest { Parent = $"tasks/{task.Id}" })).Configs);
    }

    [Fact]
    public async Task GetAgentCard_is_the_v03_proto_card()
    {
        await using var host = await StartAsync();
        var card = await Stub(host, "bob").GetAgentCardAsync(new P.GetAgentCardRequest());

        Assert.Equal("0.3.0", card.ProtocolVersion);
        Assert.Equal("Extended card for bob", card.Description); // the extended card: the agent offers one
        Assert.Equal("Bearer", card.SecuritySchemes["bearer"].HttpAuthSecurityScheme.Scheme);
        Assert.Equal("bearer", Assert.Single(Assert.Single(card.Security).Schemes).Key);
        Assert.Contains(card.AdditionalInterfaces, i => i.Transport == "GRPC" && i.Url == "http://localhost/");
        // The proto JSON of the card parses strictly: every member is in the v0.3 proto.
        Strict.Parse<P.AgentCard>(JsonFormatter.Default.Format(card));
    }

    [Fact]
    public async Task The_blended_card_lists_v03_grpc_only_when_it_is_served()
    {
        await using var served = await StartAsync();
        var withGrpc = JsonNode.Parse(await served.Http(null).GetStringAsync("/.well-known/agent-card.json"))!;
        Assert.Contains(withGrpc["additionalInterfaces"]!.AsArray(), i => (string?)i!["transport"] == "GRPC");

        await using var plain = await V03Host.StartAsync(configure: o =>
            o.AgentCard!.SupportedInterfaces.Add(new AgentInterface { Url = "http://localhost/", ProtocolBinding = ProtocolBindings.Grpc }));
        var withoutGrpc = JsonNode.Parse(await plain.Http(null).GetStringAsync("/.well-known/agent-card.json"))!;
        Assert.DoesNotContain(withoutGrpc["additionalInterfaces"]!.AsArray(), i => (string?)i!["transport"] == "GRPC");
    }

    // ---- this SDK's v0.3 gRPC transport ----

    private static A2AClient Client(V03Host host)
    {
        var card = V03Host.DefaultCard();
        card.SupportedInterfaces = [new AgentInterface { Url = "http://localhost/", ProtocolBinding = ProtocolBindings.Grpc, ProtocolVersion = "0.3" }];
        var options = new A2AClientOptions { SupportedBindings = [ProtocolBindings.Grpc], MaxRetries = 0 }
            .UseGrpcV03(new global::Grpc.Net.Client.GrpcChannelOptions
            {
                HttpHandler = new UserHandler("alice") { InnerHandler = host.App.GetTestServer().CreateHandler() },
            });
        return V03CompatClientFactory.Create(card, new HttpClient(), options);
    }

    private sealed class UserHandler(string user) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("X-Test-User", user);
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task The_v03_grpc_client_sends_streams_and_manages_push_configs()
    {
        await using var host = await StartAsync();
        var client = Client(host);

        Assert.IsType<MessageResponse>(await client.SendMessageAsync("hello"));
        var task = Assert.IsType<TaskResponse>(await client.SendMessageAsync("task")).Task;
        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal(task.Id, (await client.GetTaskAsync(task.Id)).Id);

        var events = new List<StreamResponse>();
        await foreach (var e in client.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("task") })) events.Add(e);
        Assert.Equal(TaskState.Completed, Assert.IsType<StatusUpdateEvent>(events[^1]).StatusUpdate.Status.State);

        var input = Assert.IsType<TaskResponse>(await client.SendMessageAsync("input")).Task;
        await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = input.Id, Id = "k", Url = "http://hooks.test/k" });
        Assert.Equal("http://hooks.test/k", (await client.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = input.Id, Id = "k" })).Url);
        Assert.Single((await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = input.Id })).Configs);
        await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = input.Id, Id = "k" });

        var extended = await client.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest());
        Assert.Equal("Extended card for alice", extended.Description);
        Assert.Contains(extended.SupportedInterfaces, i => i.ProtocolBinding == ProtocolBindings.Grpc && i.ProtocolVersion == "0.3");

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync("nope"));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
        var list = await Assert.ThrowsAsync<A2AException>(() => client.ListTasksAsync(new ListTasksRequest()));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, list.Code);
    }

    [Fact]
    public async Task HistoryLength_0_returns_no_history_over_v03_grpc()
    {
        // proto3 int32 has no presence: 0 reaches the server as "not set", so the client applies it.
        await using var host = await StartAsync();
        var client = Client(host);
        var task = Assert.IsType<TaskResponse>(await client.SendMessageAsync("task")).Task;

        Assert.Null((await client.GetTaskAsync(new GetTaskRequest { Id = task.Id, HistoryLength = 0 })).History);
        Assert.Single((await client.GetTaskAsync(new GetTaskRequest { Id = task.Id, HistoryLength = 1 })).History!);
        Assert.True((await client.GetTaskAsync(new GetTaskRequest { Id = task.Id })).History!.Count >= 1);
    }

    [Fact]
    public async Task GetAgentCard_gives_an_anonymous_caller_the_public_card()
    {
        // The extended card requires authentication: a caller without credentials still discovers the agent.
        await using var host = await V03Host.StartAsync(grpc: true, requireAuth: false, configure: o => o.AllowAnonymous = true);
        var anonymous = await Stub(host, user: null).GetAgentCardAsync(new P.GetAgentCardRequest());
        Assert.Equal("Test agent", anonymous.Description);

        var bob = await Stub(host, "bob").GetAgentCardAsync(new P.GetAgentCardRequest());
        Assert.Equal("Extended card for bob", bob.Description);
    }

    [Fact]
    public async Task The_public_v03_card_is_the_card_of_the_calls_tenant()
    {
        await using var host = await StartAsync();
        var services = host.App.Services;
        var handler = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<A2A.Server.IA2ARequestHandler>(services);
        var cards = new TenantCards();
        var call = new A2A.Server.ServerCallContext { Tenant = "acme", Principal = "alice" };

        var card = await A2A.Server.V0_3Compat.V03Surface.V03CardAsync(handler, cards, call, extended: false, includeGrpc: false, CancellationToken.None);
        Assert.Equal("Card of acme", card["name"]!.GetValue<string>());
        Assert.Equal("http://localhost/acme", card["url"]!.GetValue<string>());

        var unknown = new A2A.Server.ServerCallContext { Tenant = "nobody", Principal = "alice" };
        var ex = await Assert.ThrowsAsync<A2AException>(() => A2A.Server.V0_3Compat.V03Surface.V03CardAsync(handler, cards, unknown, false, false, CancellationToken.None));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
    }

    /// <summary>A card per tenant; none for the tenant "nobody".</summary>
    private sealed class TenantCards : A2A.Server.IAgentCardProvider
    {
        public AgentCard Get() => throw new InvalidOperationException("one card per tenant");

        public ValueTask<AgentCard?> GetAsync(A2A.Server.Agents.AgentRequestScope scope, CancellationToken cancellationToken)
        {
            if (scope.Tenant == "nobody") return ValueTask.FromResult<AgentCard?>(null);
            var card = V03Host.DefaultCard();
            card.Name = $"Card of {scope.Tenant}";
            card.SupportedInterfaces = [new AgentInterface { Url = $"http://localhost/{scope.Tenant}", ProtocolBinding = ProtocolBindings.JsonRpc }];
            return ValueTask.FromResult<AgentCard?>(card);
        }
    }

    [Fact]
    public void Without_UseGrpcV03_a_v03_grpc_interface_is_not_selected()
    {
        var card = V03Host.DefaultCard();
        card.SupportedInterfaces = [new AgentInterface { Url = "http://localhost/", ProtocolBinding = ProtocolBindings.Grpc, ProtocolVersion = "0.3" }];
        var ex = Assert.Throws<A2AException>(() => V03CompatClientFactory.Create(card, new HttpClient(),
            new A2AClientOptions { SupportedBindings = [ProtocolBindings.Grpc] }));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
    }

    // UseGrpcV03 registered after UseGrpc used to hide the v1.0 gRPC factory: the last factory for GRPC was asked alone,
    // and the v0.3 one refuses a v1.0 interface.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UseGrpc_and_UseGrpcV03_in_either_order_select_each_versions_grpc_interface(bool v1First)
    {
        A2AClientOptions Options()
        {
            var o = new A2AClientOptions { SupportedBindings = [ProtocolBindings.Grpc] };
            return v1First
                ? A2A.Client.Grpc.A2AGrpcClientOptionsExtensions.UseGrpc(o).UseGrpcV03()
                : A2A.Client.Grpc.A2AGrpcClientOptionsExtensions.UseGrpc(o.UseGrpcV03());
        }

        var v1 = V03Host.DefaultCard();
        v1.SupportedInterfaces = [new AgentInterface { Url = "http://localhost:1/", ProtocolBinding = ProtocolBindings.Grpc, ProtocolVersion = "1.0" }];
        Assert.Equal("http://localhost:1/", A2AClientFactory.SelectInterface(v1, Options())?.Url);
        Assert.NotNull(V03CompatClientFactory.Create(v1, new HttpClient(), Options()));

        var v03 = V03Host.DefaultCard();
        v03.SupportedInterfaces = [new AgentInterface { Url = "http://localhost:2/", ProtocolBinding = ProtocolBindings.Grpc, ProtocolVersion = "0.3" }];
        Assert.NotNull(V03CompatClientFactory.Create(v03, new HttpClient(), Options()));
    }

    // ---- shared pieces ----

    [Fact]
    public void The_shipped_v03_proto_is_the_published_one()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "A2A.slnx"))) root = Path.GetDirectoryName(root)!;
        Assert.Equal(File.ReadAllText(Path.Combine(root, "tests/A2A.V0_3Compat.Tests/Golden/proto/a2a_v03.proto")),
            File.ReadAllText(Path.Combine(root, "src/Shared/Protos/a2a_v03.proto")));
    }

    [Fact]
    public void The_proto_card_round_trips_and_parses_strictly()
    {
        using var meters = new MeterRecorder();
        var v03 = Golden.Object("agent-card.json");
        var proto = V03CardCodec.ToProtoCard(v03);
        Strict.Parse<P.AgentCard>(proto.ToJsonString()); // every member is in the v0.3 proto
        var back = V03CardCodec.FromProtoCard(proto);

        Assert.True(JsonNode.DeepEquals(v03["security"], back["security"]));
        Assert.True(JsonNode.DeepEquals(v03["skills"], back["skills"]));
        foreach (var (name, scheme) in v03["securitySchemes"]!.AsObject())
        {
            if (name == "oauth") continue;
            Assert.True(JsonNode.DeepEquals(scheme, back["securitySchemes"]![name]), name);
        }

        // The proto's OAuthFlows is a oneof: the first flow is kept, the drop counted.
        var flows = back["securitySchemes"]!["oauth"]!["flows"]!.AsObject();
        Assert.Equal("authorizationCode", Assert.Single(flows).Key);
        Assert.True(JsonNode.DeepEquals(v03["securitySchemes"]!["oauth"]!["flows"]!["authorizationCode"], flows["authorizationCode"]));
        Assert.Contains("oauth.flows", meters.DroppedFields());
    }

    [Theory]
    [InlineData(A2AErrorCode.TaskNotFound, 5)]
    [InlineData(A2AErrorCode.TaskNotCancelable, 12)]
    [InlineData(A2AErrorCode.InvalidParams, 3)]
    [InlineData(A2AErrorCode.PushNotificationNotSupported, 12)]
    [InlineData(A2AErrorCode.Internal, 13)]
    public void Errors_round_trip_through_their_name(A2AErrorCode code, int status)
    {
        var (s, message) = V03GrpcErrors.ToStatus(new A2AException(code, "why"));
        Assert.Equal(status, s);
        var back = V03GrpcErrors.FromStatus(s, message);
        Assert.Equal(code, back.Code);
        Assert.Equal("why", back.Message);
        Assert.Equal(A2AErrorCode.TaskNotFound, V03GrpcErrors.FromStatus(5, "no prefix").Code); // a peer without the prefix
    }

    [Fact]
    public void An_unavailable_status_is_a_connection_failure_the_stream_retries()
    {
        // UNAVAILABLE (14) is how a dropped connection or a restarting server reaches a gRPC client. As on the v1 path
        // (GrpcErrors), it carries HTTP 503, which ResumableStream treats as a connection failure and reconnects after.
        var dropped = V03GrpcErrors.FromStatus(14, "Error starting gRPC call. HttpRequestException: connection refused");
        Assert.Equal(503, dropped.HttpStatus);

        // A named error keeps its own meaning, whatever the status.
        var named = V03GrpcErrors.FromStatus(14, "TaskNotFoundError: gone");
        Assert.Equal(A2AErrorCode.TaskNotFound, named.Code);
        Assert.Null(named.HttpStatus);
    }

    [Fact]
    public async Task An_unauthenticated_call_gets_a_www_authenticate_challenge()
    {
        // As the v1 gRPC binding and the v0.3 HTTP bindings do (spec 3.3.2, 7.4): the challenge names the scheme.
        // Started with anonymous calls allowed (the startup check needs a policy otherwise), then refused: the call then
        // reaches the service, which answers UNAUTHENTICATED itself.
        A2AServerOptions? options = null;
        await using var host = await V03Host.StartAsync(grpc: true, requireAuth: false, configure: o =>
        {
            o.AllowAnonymous = true;
            options = o;
        });
        options!.AllowAnonymous = false;
        var stub = Stub(host, user: null);

        var ex = await Assert.ThrowsAsync<RpcException>(async () => await stub.GetTaskAsync(new P.GetTaskRequest { Name = "tasks/x" }));

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        Assert.False(string.IsNullOrEmpty(ex.Trailers.GetValue("www-authenticate")));
    }

    [Fact]
    public void The_retry_delay_travels_as_a_retry_after_trailer_and_back()
    {
        Assert.Equal("3", V03GrpcErrors.RetryAfterValue(TimeSpan.FromSeconds(2.2)));
        Assert.Equal(TimeSpan.FromSeconds(3), V03GrpcErrors.ParseRetryAfter("3"));
        Assert.Null(V03GrpcErrors.ParseRetryAfter("soon"));
        Assert.Null(V03GrpcErrors.ParseRetryAfter(null));

        var limited = V03GrpcErrors.FromStatus(8, "RateLimitedError: slow down", TimeSpan.FromSeconds(3));
        Assert.Equal(A2AErrorCode.RateLimited, limited.Code);
        Assert.Equal(TimeSpan.FromSeconds(3), limited.RetryAfter);
    }
}
