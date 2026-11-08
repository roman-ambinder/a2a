using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using A2A.Client;
using A2A.Client.Grpc;
using A2A.Grpc;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pb = Lf.A2a.V1;

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
        var oldProto = OldToProto<Pb.Task>(task);
        var newProto = ProtoMapper.ToProto<Pb.Task>(task);
        Assert.Equal(oldProto, newProto);
        Assert.Equal(
            A2A.Json.A2AJson.Serialize(OldToModel<AgentTask>(oldProto)),
            A2A.Json.A2AJson.Serialize(ProtoMapper.ToModel<AgentTask>(newProto)));

        var card = GrpcHost.Card();
        card.Capabilities.PushNotifications = false;
        var oldCard = OldToProto<Pb.AgentCard>(card);
        var newCard = ProtoMapper.ToProto<Pb.AgentCard>(card);
        Assert.Equal(oldCard, newCard);
        Assert.Equal(
            A2A.Json.A2AJson.Serialize(OldToModel<AgentCard>(oldCard)),
            A2A.Json.A2AJson.Serialize(ProtoMapper.ToModel<AgentCard>(newCard)));
    }

    [Fact]
    public void Task_round_trips_through_proto()
    {
        var task = new AgentTask
        {
            Id = "t1",
            ContextId = "c1",
            Status = new AgentTaskStatus { State = TaskState.InputRequired, Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.Zero), Message = Message.FromAgent("more?", "c1", "t1") },
            Artifacts = [new Artifact { ArtifactId = "a", Parts = [Part.FromText("x"), Part.FromBytes(new byte[] { 1, 2, 3 }, "application/octet-stream"), Part.FromData(JsonSerializer.SerializeToElement(new { n = 1 })), Part.FromUrl(new Uri("https://x/y"))] }],
            Metadata = new() { ["k"] = JsonSerializer.SerializeToElement(new { deep = true }) },
        };
        var proto = ProtoMapper.ToProto<Pb.Task>(task);
        Assert.Equal(Pb.TaskState.InputRequired, proto.Status.State);
        Assert.Equal(Google.Protobuf.ByteString.CopyFrom(1, 2, 3), proto.Artifacts[0].Parts[1].Raw);

        var back = ProtoMapper.ToModel<AgentTask>(proto);
        Assert.Equal(TaskState.InputRequired, back.Status.State);
        Assert.Equal(task.Status.Timestamp, back.Status.Timestamp);
        Assert.Equal([typeof(TextPart), typeof(RawPart), typeof(DataPart), typeof(UrlPart)], back.Artifacts![0].Parts.Select(p => p.GetType()));
        Assert.True(back.Metadata!["k"].GetProperty("deep").GetBoolean());
        Assert.Equal("more?", back.Status.Message!.GetText());
    }

    [Fact]
    public void Agent_card_keeps_explicit_optional_false()
    {
        var card = GrpcHost.Card();
        card.Capabilities.PushNotifications = false;
        var proto = ProtoMapper.ToProto<Pb.AgentCard>(card);
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
        var task = TaskOf(await client.SendMessageAsync("task"));
        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal(["part1", "part2"], task.Artifacts!.Single().Parts.Cast<TextPart>().Select(p => p.Text));
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
        var first = TaskOf(await client.SendMessageAsync("input"));
        Assert.Equal(TaskState.InputRequired, first.Status.State);
        var second = TaskOf(await client.SendMessageAsync("more", first.ContextId, first.Id));
        Assert.Equal("got: more", second.Status.Message!.GetText());
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("again", null, first.Id));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
    }

    [Fact]
    public async Task Subscribe_then_cancel()
    {
        var client = _host.Client();
        var task = TaskOf(await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true }));
        var first = new TaskCompletionSource<StreamResponse>();
        var states = new List<TaskState>();
        var reader = Task.Run(async () =>
        {
            await foreach (var e in client.SubscribeToTaskAsync(task.Id))
            {
                first.TrySetResult(e);
                if (e is StatusUpdateEvent { StatusUpdate: var su }) states.Add(su.Status.State);
            }
        });
        Assert.Equal(task.Id, (await first.Task.WaitAsync(TimeSpan.FromSeconds(10))).TaskId);
        Assert.Equal(TaskState.Canceled, (await client.CancelTaskAsync(task.Id)).Status.State);
        await reader.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(TaskState.Canceled, states);
        Assert.Equal(A2AErrorCode.TaskNotCancelable, (await Assert.ThrowsAsync<A2AException>(() => client.CancelTaskAsync(task.Id))).Code);
    }

    [Fact]
    public async Task Owner_scoping_and_listing()
    {
        var alice = _host.Client("alice");
        var bob = _host.Client("bob");
        var task = TaskOf(await alice.SendMessageAsync("task"));
        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() => bob.GetTaskAsync(task.Id))).Code);
        Assert.Empty((await bob.ListTasksAsync(new ListTasksRequest())).Tasks);
        Assert.Equal(task.Id, Assert.Single((await alice.ListTasksAsync(new ListTasksRequest())).Tasks).Id);
    }

    [Fact]
    public async Task Push_config_crud()
    {
        var client = _host.Client("carol");
        var task = TaskOf(await client.SendMessageAsync("input"));
        var created = await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = task.Id, Url = "http://127.0.0.1:9/cb" });
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
        var raw = new Pb.A2AService.A2AServiceClient(_host.Channel("alice"));
        var noVersion = await Assert.ThrowsAsync<RpcException>(() => raw.GetTaskAsync(new Pb.GetTaskRequest { Id = "x" }).ResponseAsync);
        Assert.Equal(A2AErrorCode.VersionNotSupported, GrpcErrors.FromRpcException(noVersion).Code);

        var headers = new Metadata { { "a2a-version", "1.0" }, { "a2a-extensions", $"{GrpcHost.Extension},urn:other" } };
        var msg = new Pb.SendMessageRequest { Message = new Pb.Message { MessageId = "m", Role = Pb.Role.User, Parts = { new Pb.Part { Text = "hello" } } } };
        using var call = raw.SendMessageAsync(msg, headers);
        await call.ResponseAsync;
        Assert.Equal(GrpcHost.Extension, (await call.ResponseHeadersAsync).GetValue("a2a-extensions"));
    }

    private static AgentTask TaskOf(SendMessageResponse r) => Assert.IsType<TaskResponse>(r).Task;

    [Fact]
    public async Task Invalid_request_maps_to_invalid_argument()
    {
        var raw = new Pb.A2AService.A2AServiceClient(_host.Channel("alice"));
        var bad = new Pb.SendMessageRequest { Message = new Pb.Message { MessageId = "m", Role = Pb.Role.User } };
        var ex = await Assert.ThrowsAsync<RpcException>(() => raw.SendMessageAsync(bad, new Metadata { { "a2a-version", "1.0" } }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }
}

public class GrpcSecurityAndFactoryTests
{
    [Fact]
    public async Task Startup_fails_without_an_authorization_policy()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => GrpcHost.StartAsync(requireAuth: false));
        Assert.Contains("authorization policy", ex.Message);
    }

    [Fact]
    public async Task Allow_anonymous_opts_out_of_the_startup_check()
    {
        await using var host = await GrpcHost.StartAsync(requireAuth: false, configure: o => o.AllowAnonymous = true);
        Assert.IsType<MessageResponse>(await host.Client(user: null).SendMessageAsync("hello"));
    }

    [Fact]
    public async Task Retry_with_same_idempotency_key_returns_the_same_task()
    {
        await using var host = await GrpcHost.StartAsync();
        var client = host.Client();
        var options = new A2ARequestOptions { IdempotencyKey = "key-1" };
        var message = Message.FromUser("task");
        var first = Assert.IsType<TaskResponse>(await client.SendMessageAsync(message, options: options)).Task;
        var again = Assert.IsType<TaskResponse>(await client.SendMessageAsync(message, options: options)).Task;
        Assert.Equal(first.Id, again.Id);
    }

    [Fact]
    public void Factory_selects_grpc_only_when_enabled()
    {
        var card = GrpcHost.Card();
        card.SupportedInterfaces = [
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
}

// ---------------- host ----------------

internal sealed class ScenarioAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
    {
        if (context.Task?.Status.State == TaskState.InputRequired)
        {
            await updater.CompleteAsync($"got: {context.UserText}", ct);
            return;
        }
        switch (context.UserText)
        {
            case "hello": await updater.ReplyAsync("hi there", ct); break;
            case "task":
                await updater.StartWorkAsync("working", ct);
                await updater.AddArtifactAsync([Part.FromText("part1")], artifactId: "a1", name: "result", cancellationToken: ct);
                await updater.AddArtifactAsync([Part.FromText("part2")], artifactId: "a1", append: true, lastChunk: true, cancellationToken: ct);
                await updater.CompleteAsync("done", ct);
                break;
            case "input": await updater.RequireInputAsync("need more", ct); break;
            case "slow":
                await updater.StartWorkAsync(cancellationToken: ct);
                await Task.Delay(Timeout.Infinite, ct);
                break;
            default: await updater.CompleteAsync(context.UserText, ct); break;
        }
    }
}

internal sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["x-test-user"].ToString();
        if (string.IsNullOrEmpty(user)) return Task.FromResult(AuthenticateResult.NoResult());
        var id = new ClaimsIdentity([new Claim("sub", user)], "Test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), "Test")));
    }
}

internal sealed class GrpcHost : IAsyncDisposable
{
    public const string Extension = "urn:test:ext";
    private readonly WebApplication _app;

    private GrpcHost(WebApplication app) => _app = app;

    public static AgentCard Card() => new()
    {
        Name = "gRPC Scenario Agent", Description = "test", Version = "1.0.0",
        SupportedInterfaces = [new AgentInterface { Url = "http://localhost", ProtocolBinding = ProtocolBindings.Grpc }],
        Capabilities = new AgentCapabilities { Streaming = true, PushNotifications = true, ExtendedAgentCard = true, Extensions = [new AgentExtension { Uri = Extension }] },
        DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "s", Name = "S", Description = "s", Tags = ["t"] }],
    };

    public static async Task<GrpcHost> StartAsync(bool requireAuth = true, Action<A2AServerOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", null);
        builder.Services.AddAuthorization();
        builder.Services.AddA2AServer(o =>
        {
            o.AgentCard = Card();
            o.CancelGracePeriod = TimeSpan.FromSeconds(2);
            o.PushNotifications.AllowInsecureUrls = true;
            o.PushNotifications.AllowPrivateNetworks = true;
            o.ExtendedAgentCardProvider = (call, _, _) =>
            {
                var c = Card();
                c.Description = $"Extended for {call.Principal}";
                return ValueTask.FromResult<AgentCard?>(c);
            };
            configure?.Invoke(o);
        }).AddAgentExecutor<ScenarioAgent>();
        builder.Services.AddA2AGrpc();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseA2A();
        app.UseAuthorization();
        app.MapAgentCard();
        var grpc = app.MapA2AGrpc();
        if (requireAuth) grpc.RequireAuthorization();
        await app.StartAsync();
        return new GrpcHost(app);
    }

    public GrpcChannel Channel(string? user = null)
    {
        HttpMessageHandler handler = _app.GetTestServer().CreateHandler();
        if (user is not null) handler = new UserHandler(user) { InnerHandler = handler };
        return GrpcChannel.ForAddress(_app.GetTestServer().BaseAddress, new GrpcChannelOptions { HttpHandler = handler });
    }

    public HttpMessageHandler Handler(string? user = null)
    {
        HttpMessageHandler handler = _app.GetTestServer().CreateHandler();
        return user is null ? handler : new UserHandler(user) { InnerHandler = handler };
    }

    public Uri BaseAddress => _app.GetTestServer().BaseAddress;

    public A2AClient Client(string? user = "alice", A2AClientOptions? options = null)
    {
        options ??= new A2AClientOptions();
        return new A2AClient(new GrpcTransport(Channel(user).CreateCallInvoker(), options), options, Card());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed class UserHandler(string user) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("x-test-user", user);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
