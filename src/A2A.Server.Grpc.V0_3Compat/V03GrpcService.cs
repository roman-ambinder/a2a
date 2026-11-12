using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Server.Agents;
using A2A.Server.AspNetCore;
using A2A.Server.V0_3Compat;
using A2A.V0_3Compat;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using A2ACall = A2A.Server.ServerCallContext;
using GrpcContext = Grpc.Core.ServerCallContext;
using V03Pb = A2a.V1;

namespace A2A.Server.Grpc.V0_3Compat;

/// <summary>
///     The v0.3 gRPC service <c>a2a.v1.A2AService</c> (the proto of tag v0.3.0) over the v1.0 request handler. Each message
///     is converted through its ProtoJSON form with the v0.3 HTTP+JSON codec, which is the same mapping of the same proto,
///     so the two bindings cannot drift apart. The call is set up as on the v0.3 HTTP surface: a v1.0 call whose wire
///     version is 0.3, <c>x-a2a-extensions</c> metadata read as the requested extensions, and an <c>a2a-version</c> other
///     than <c>0.3</c> refused. Errors follow the v0.3 Python SDK: a gRPC status, and a message that starts with the error
///     name (<see cref="V03GrpcErrors" />). The activated extensions are sent back in <c>x-a2a-extensions</c> trailers.
/// </summary>
internal sealed class V03GrpcService : V03Pb.A2AService.A2AServiceBase
{
    private const string Binding = ProtocolBindings.Grpc;

    private static readonly JsonFormatter Formatter = new(JsonFormatter.Settings.Default);

    // Unknown fields are skipped, as ProtoJSON readers of a newer peer's messages should.
    private static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private readonly IAgentCardProvider _cards;
    private readonly IA2ARequestHandler _handler;
    private readonly bool _includeDetails;
    private readonly ILogger _logger;
    private readonly int _maxDepth;

    public V03GrpcService(IA2ARequestHandler handler, IOptions<A2AServerOptions> serverOptions,
        IHostEnvironment environment, ILoggerFactory loggerFactory, IAgentCardProvider cards)
    {
        _handler = handler;
        _cards = cards;
        _logger = loggerFactory.CreateLogger("A2A.Server.Grpc.V0_3Compat");
        _includeDetails = HttpPlumbing.ShouldIncludeDetails(serverOptions.Value, environment);
        _maxDepth = serverOptions.Value.Limits.MaxJsonDepth;
    }

    // ---- unary ----

    public override Task<V03Pb.SendMessageResponse> SendMessage(V03Pb.SendMessageRequest request, GrpcContext context)
    {
        return Unary<V03Pb.SendMessageResponse>(context, "SendMessage", async (call, ct) =>
        {
            var send = V03ProtoJsonCodec.Instance.ReadSendRequest(ToNode(request));
            return V03ProtoJsonCodec.Instance.WriteSendResult(
                await _handler.SendMessageAsync(send, call, ct).ConfigureAwait(false));
        });
    }

    public override Task<V03Pb.Task> GetTask(V03Pb.GetTaskRequest request, GrpcContext context)
    {
        return Unary<V03Pb.Task>(context, "GetTask", async (call, ct) =>
        {
            // proto3 int32 has no presence: 0 is "not set" (all of the history), as in the v0.3 Python SDK.
            var get = new GetTaskRequest
            {
                Id = TaskIdOf(request.Name), Tenant = call.Tenant,
                HistoryLength = request.HistoryLength > 0 ? request.HistoryLength : null
            };
            return V03ProtoJsonCodec.Instance.WriteTask(await _handler.GetTaskAsync(get, call, ct).ConfigureAwait(false));
        });
    }

    public override Task<V03Pb.Task> CancelTask(V03Pb.CancelTaskRequest request, GrpcContext context)
    {
        return Unary<V03Pb.Task>(context, "CancelTask", async (call, ct) =>
            V03ProtoJsonCodec.Instance.WriteTask(await _handler
                .CancelTaskAsync(new CancelTaskRequest { Id = TaskIdOf(request.Name), Tenant = call.Tenant }, call, ct)
                .ConfigureAwait(false)));
    }

    public override Task<V03Pb.TaskPushNotificationConfig> CreateTaskPushNotificationConfig(
        V03Pb.CreateTaskPushNotificationConfigRequest request, GrpcContext context)
    {
        return Unary<V03Pb.TaskPushNotificationConfig>(context, "CreateTaskPushNotificationConfig", async (call, ct) =>
        {
            // parent is "tasks/{id}" (the proto's http rule spells it "task/*/pushNotificationConfigs"; both are read).
            var taskId = TaskIdOf(request.Parent.StartsWith("task/", StringComparison.Ordinal)
                ? "tasks/" + request.Parent["task/".Length..]
                : request.Parent);
            var body = request.Config is null ? new JsonObject() : V03Json.Obj(ToNode(request.Config), "config");
            if (string.IsNullOrEmpty(V03Json.Str(body, "name")))
                body["name"] = V03ProtoJsonCodec.PushConfigName(taskId, null);
            var config = V03ProtoJsonCodec.Instance.ReadPushConfig(body);
            if (config.TaskId != taskId)
                throw A2AException.InvalidField("config.name", "config.name must name the task of parent");
            if (request.ConfigId.Length > 0) config.Id = request.ConfigId;
            config.Tenant = call.Tenant;
            return V03ProtoJsonCodec.Instance.WritePushConfig(await _handler
                .CreateTaskPushNotificationConfigAsync(config, call, ct).ConfigureAwait(false));
        });
    }

    public override Task<V03Pb.TaskPushNotificationConfig> GetTaskPushNotificationConfig(
        V03Pb.GetTaskPushNotificationConfigRequest request, GrpcContext context)
    {
        return Unary<V03Pb.TaskPushNotificationConfig>(context, "GetTaskPushNotificationConfig", async (call, ct) =>
        {
            var (taskId, configId) = PushConfigOf(request.Name);
            TaskPushNotificationConfig config;
            if (configId is null)
            {
                // No config id: the task's first config, as the v0.3 Python SDK answers.
                var first = await _handler.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest
                    { TaskId = taskId, Tenant = call.Tenant, PageSize = 1 }, call, ct).ConfigureAwait(false);
                config = first.Configs.Count > 0
                    ? first.Configs[0]
                    : await GetConfigAsync(call, taskId, taskId, ct).ConfigureAwait(false);
            }
            else
            {
                config = await GetConfigAsync(call, taskId, configId, ct).ConfigureAwait(false);
            }

            return V03ProtoJsonCodec.Instance.WritePushConfig(config);
        });
    }

    public override Task<V03Pb.ListTaskPushNotificationConfigResponse> ListTaskPushNotificationConfig(
        V03Pb.ListTaskPushNotificationConfigRequest request, GrpcContext context)
    {
        return Unary<V03Pb.ListTaskPushNotificationConfigResponse>(context, "ListTaskPushNotificationConfig",
            async (call, ct) =>
            {
                var response = await _handler.ListTaskPushNotificationConfigsAsync(
                    new ListTaskPushNotificationConfigsRequest
                    {
                        TaskId = TaskIdOf(request.Parent), Tenant = call.Tenant,
                        PageSize = request.PageSize > 0 ? request.PageSize : null,
                        PageToken = request.PageToken.Length > 0 ? request.PageToken : null
                    }, call, ct).ConfigureAwait(false);
                var configs = new JsonArray();
                foreach (var config in response.Configs)
                    V03Json.Append(configs, V03ProtoJsonCodec.Instance.WritePushConfig(config));
                var result = new JsonObject { ["configs"] = configs };
                if (response.NextPageToken is { Length: > 0 } next) result["nextPageToken"] = next;
                return result;
            });
    }

    public override Task<Google.Protobuf.WellKnownTypes.Empty> DeleteTaskPushNotificationConfig(
        V03Pb.DeleteTaskPushNotificationConfigRequest request, GrpcContext context)
    {
        return Unary<Google.Protobuf.WellKnownTypes.Empty>(context, "DeleteTaskPushNotificationConfig",
            async (call, ct) =>
            {
                var (taskId, configId) = PushConfigOf(request.Name);
                if (configId is null)
                    throw A2AException.InvalidField("name", "name must be tasks/{id}/pushNotificationConfigs/{configId}");
                await _handler.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest
                    { TaskId = taskId, Id = configId, Tenant = call.Tenant }, call, ct).ConfigureAwait(false);
                return new JsonObject();
            });
    }

    public override Task<V03Pb.AgentCard> GetAgentCard(V03Pb.GetAgentCardRequest request, GrpcContext context)
    {
        return Unary<V03Pb.AgentCard>(context, "GetAgentCard", async (call, ct) =>
        {
            // v0.3 gRPC has one card call. The v0.3 Python client uses it for the authenticated extended card, and a client
            // without credentials uses it to discover the agent. An authenticated caller gets the extended card when the
            // tenant's agent offers one; everyone else gets the tenant's public card (the extended card requires
            // authentication, and a caller who is refused it can still read the public one).
            var publicCard = await _cards.GetAsync(new AgentRequestScope(call.Tenant, call.RouteValues), ct)
                .ConfigureAwait(false);
            var extended = call.IsAuthenticated && publicCard?.Capabilities.ExtendedAgentCard == true;
            JsonObject card;
            try
            {
                card = await V03Surface.V03CardAsync(_handler, _cards, call, extended, includeGrpc: true, ct)
                    .ConfigureAwait(false);
            }
            catch (A2AException ex) when (extended && ex.Code is A2AErrorCode.ExtendedAgentCardNotConfigured
                                              or A2AErrorCode.Unauthenticated or A2AErrorCode.PermissionDenied)
            {
                card = await V03Surface.V03CardAsync(_handler, _cards, call, extended: false, includeGrpc: true, ct)
                    .ConfigureAwait(false);
            }

            return V03CardCodec.ToProtoCard(card);
        });
    }

    // ---- streams ----

    public override Task SendStreamingMessage(V03Pb.SendMessageRequest request,
        IServerStreamWriter<V03Pb.StreamResponse> responseStream, GrpcContext context)
    {
        return Stream(context, responseStream, "SendStreamingMessage", call =>
            _handler.SendStreamingMessageAsync(V03ProtoJsonCodec.Instance.ReadSendRequest(ToNode(request)), call,
                cancellationToken: context.CancellationToken));
    }

    public override Task TaskSubscription(V03Pb.TaskSubscriptionRequest request,
        IServerStreamWriter<V03Pb.StreamResponse> responseStream, GrpcContext context)
    {
        return Stream(context, responseStream, "TaskSubscription", call =>
            _handler.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = TaskIdOf(request.Name), Tenant = call.Tenant },
                call, cancellationToken: context.CancellationToken));
    }

    // ---- plumbing ----

    private async Task<TOut> Unary<TOut>(GrpcContext context, string operation,
        Func<A2ACall, CancellationToken, Task<JsonNode>> action) where TOut : IMessage<TOut>, new()
    {
        A2ACall? call = null;
        try
        {
            call = Begin(context, operation);
            var result = await action(call, context.CancellationToken).ConfigureAwait(false);
            WriteExtensions(context, call);
            return Parser.Parse<TOut>(result.ToJsonString());
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw Fail(ex, context, call);
        }
    }

    private async Task Stream(GrpcContext context, IServerStreamWriter<V03Pb.StreamResponse> writer, string operation,
        Func<A2ACall, IAsyncEnumerable<Streaming.SequencedEvent>> source)
    {
        A2ACall? call = null;
        try
        {
            call = Begin(context, operation);
            var any = false;
            await foreach (var evt in source(call).ConfigureAwait(false))
            {
                any = true;
                var json = V03ProtoJsonCodec.Instance.WriteStreamEvent(evt.Event);
                await writer.WriteAsync(Parser.Parse<V03Pb.StreamResponse>(json.ToJsonString())).ConfigureAwait(false);
            }

            // As on the other bindings: a handler that yields no event is the server's failure, not an empty stream.
            if (!any) throw new A2AException(A2AErrorCode.InvalidAgentResponse, "Empty stream");
            WriteExtensions(context, call);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw Fail(ex, context, call);
        }
    }

    private A2ACall Begin(GrpcContext context, string operation)
    {
        var http = context.GetHttpContext();
        var call = V03Surface.CreateCall(http, Binding);
        V03Surface.RequireV03Header(http);
        _handler.ValidateCall(call);
        V03LossReporter.Request(Binding, operation);
        return call;
    }

    private Task<TaskPushNotificationConfig> GetConfigAsync(A2ACall call, string taskId, string configId,
        CancellationToken ct)
    {
        return _handler.GetTaskPushNotificationConfigAsync(
            new GetTaskPushNotificationConfigRequest { TaskId = taskId, Id = configId, Tenant = call.Tenant }, call, ct);
    }

    private static void WriteExtensions(GrpcContext context, A2ACall call)
    {
        if (call.ActivatedExtensions.Count > 0)
            context.ResponseTrailers.Add(V03Compat.ExtensionsHeader.ToLowerInvariant(),
                string.Join(",", call.ActivatedExtensions));
    }

    private RpcException Fail(Exception ex, GrpcContext context, A2ACall? call)
    {
        if (ex is OperationCanceledException && context.CancellationToken.IsCancellationRequested)
            return new RpcException(new Status(StatusCode.Cancelled, "Call canceled by the client"));
        if (call is not null) WriteExtensions(context, call);
        var a2a = ex switch
        {
            JsonException json => A2AException.InvalidParams($"Invalid request: {json.Message}"),
            InvalidProtocolBufferException proto => A2AException.InvalidParams($"Invalid message: {proto.Message}"),
            _ => HttpPlumbing.Normalize(ex, _logger, _includeDetails)
        };
        var (status, message) = V03GrpcErrors.ToStatus(a2a);
        return new RpcException(new Status((StatusCode)status, message));
    }

    private JsonNode ToNode(IMessage message)
    {
        return JsonNode.Parse(Formatter.Format(message), documentOptions: new JsonDocumentOptions { MaxDepth = _maxDepth })!;
    }

    /// <summary>The task id of a resource name <c>tasks/{id}</c>.</summary>
    private static string TaskIdOf(string name)
    {
        return V03ProtoJsonCodec.TryParseName(name, out var taskId, out _)
            ? taskId
            : throw A2AException.InvalidField("name", "name must be tasks/{id}");
    }

    /// <summary>The task id and config id (null when absent) of <c>tasks/{id}/pushNotificationConfigs[/{configId}]</c>.</summary>
    private static (string TaskId, string? ConfigId) PushConfigOf(string name)
    {
        return V03ProtoJsonCodec.TryParseName(name, out var taskId, out var configId)
            ? (taskId, string.IsNullOrEmpty(configId) ? null : configId)
            : throw A2AException.InvalidField("name", "name must be tasks/{id}/pushNotificationConfigs/{configId}");
    }
}
