using System.Text.Json;
using A2A.Grpc;
using A2A.Server.AspNetCore;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pb = Lf.A2a.V1;
using GrpcContext = Grpc.Core.ServerCallContext;

namespace A2A.Server.Grpc;

/// <summary>
/// gRPC binding (spec §10). Implements <c>lf.a2a.v1.A2AService</c> on top of <see cref="A2ARequestHandler"/>, so behavior,
/// authorization scoping, and validation match the JSON-RPC and REST bindings exactly.
/// </summary>
public sealed class A2AGrpcService : Pb.A2AService.A2AServiceBase
{
    private readonly A2ARequestHandler _handler;
    private readonly A2AHttpOptions _http;
    private readonly ILogger<A2AGrpcService> _logger;

    public A2AGrpcService(A2ARequestHandler handler, IOptions<A2AHttpOptions> httpOptions, ILogger<A2AGrpcService> logger)
    {
        _handler = handler;
        _http = httpOptions.Value;
        _logger = logger;
    }

    public override Task<Pb.SendMessageResponse> SendMessage(Pb.SendMessageRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.SendMessageResponse>(
            await _handler.SendMessageAsync(ProtoMapper.ToModel<SendMessageRequest>(request), call, ct).ConfigureAwait(false)));

    public override Task SendStreamingMessage(Pb.SendMessageRequest request, IServerStreamWriter<Pb.StreamResponse> responseStream, GrpcContext context) =>
        Stream(context, responseStream, (call, ct) => _handler.SendStreamingMessageAsync(ProtoMapper.ToModel<SendMessageRequest>(request), call, ct));

    public override Task<Pb.Task> GetTask(Pb.GetTaskRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.Task>(
            await _handler.GetTaskAsync(ProtoMapper.ToModel<GetTaskRequest>(request), call, ct).ConfigureAwait(false)));

    public override Task<Pb.ListTasksResponse> ListTasks(Pb.ListTasksRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.ListTasksResponse>(
            await _handler.ListTasksAsync(ProtoMapper.ToModel<ListTasksRequest>(request), call, ct).ConfigureAwait(false)));

    public override Task<Pb.Task> CancelTask(Pb.CancelTaskRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.Task>(
            await _handler.CancelTaskAsync(ProtoMapper.ToModel<CancelTaskRequest>(request), call, ct).ConfigureAwait(false)));

    public override Task SubscribeToTask(Pb.SubscribeToTaskRequest request, IServerStreamWriter<Pb.StreamResponse> responseStream, GrpcContext context) =>
        Stream(context, responseStream, (call, ct) => _handler.SubscribeToTaskAsync(ProtoMapper.ToModel<SubscribeToTaskRequest>(request), call, ct));

    public override Task<Pb.TaskPushNotificationConfig> CreateTaskPushNotificationConfig(Pb.TaskPushNotificationConfig request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.TaskPushNotificationConfig>(
            await _handler.CreateTaskPushNotificationConfigAsync(ProtoMapper.ToModel<TaskPushNotificationConfig>(request), call, ct).ConfigureAwait(false)));

    public override Task<Pb.TaskPushNotificationConfig> GetTaskPushNotificationConfig(Pb.GetTaskPushNotificationConfigRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.TaskPushNotificationConfig>(
            await _handler.GetTaskPushNotificationConfigAsync(ProtoMapper.ToModel<GetTaskPushNotificationConfigRequest>(request), call, ct).ConfigureAwait(false)));

    public override Task<Pb.ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigs(Pb.ListTaskPushNotificationConfigsRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.ListTaskPushNotificationConfigsResponse>(
            await _handler.ListTaskPushNotificationConfigsAsync(ProtoMapper.ToModel<ListTaskPushNotificationConfigsRequest>(request), call, ct).ConfigureAwait(false)));

    public override Task<Empty> DeleteTaskPushNotificationConfig(Pb.DeleteTaskPushNotificationConfigRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) =>
        {
            await _handler.DeleteTaskPushNotificationConfigAsync(ProtoMapper.ToModel<DeleteTaskPushNotificationConfigRequest>(request), call, ct).ConfigureAwait(false);
            return new Empty();
        });

    public override Task<Pb.AgentCard> GetExtendedAgentCard(Pb.GetExtendedAgentCardRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.AgentCard>(
            await _handler.GetExtendedAgentCardAsync(ProtoMapper.ToModel<GetExtendedAgentCardRequest>(request), call, ct).ConfigureAwait(false)));

    // ---- plumbing ----

    private ServerCallContext CreateCall(GrpcContext context)
    {
        var http = context.GetHttpContext();
        var extensions = context.RequestHeaders
            .Where(h => !h.IsBinary && string.Equals(h.Key, GrpcErrors.ExtensionsKey, StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var version = context.RequestHeaders.GetValue(GrpcErrors.VersionKey);
        return new ServerCallContext
        {
            User = http.User,
            CallerId = _http.CallerIdResolver(http),
            ProtocolVersion = string.IsNullOrWhiteSpace(version) ? null : version,
            RequestedExtensions = extensions,
        };
    }

    private async Task<T> Unary<T>(GrpcContext context, Func<ServerCallContext, CancellationToken, Task<T>> action)
    {
        var call = CreateCall(context);
        try
        {
            _handler.ValidateCall(call);
            var result = await action(call, context.CancellationToken).ConfigureAwait(false);
            await WriteExtensionsHeaderAsync(context, call).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw Translate(ex, context);
        }
    }

    private async Task Stream(GrpcContext context, IServerStreamWriter<Pb.StreamResponse> writer,
        Func<ServerCallContext, CancellationToken, IAsyncEnumerable<StreamResponse>> source)
    {
        var call = CreateCall(context);
        try
        {
            _handler.ValidateCall(call);
            var first = true;
            await foreach (var evt in source(call, context.CancellationToken).ConfigureAwait(false))
            {
                if (first)
                {
                    await WriteExtensionsHeaderAsync(context, call).ConfigureAwait(false);
                    first = false;
                }
                await writer.WriteAsync(ProtoMapper.ToProto<Pb.StreamResponse>(evt)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            // gRPC carries the status in trailers, so errors after the first message still reach the client cleanly.
            throw Translate(ex, context);
        }
    }

    private static Task WriteExtensionsHeaderAsync(GrpcContext context, ServerCallContext call) =>
        call.ActivatedExtensions.Count == 0
            ? Task.CompletedTask
            : context.WriteResponseHeadersAsync(new Metadata { { GrpcErrors.ExtensionsKey, string.Join(",", call.ActivatedExtensions) } });

    private RpcException Translate(Exception ex, GrpcContext context)
    {
        if (ex is OperationCanceledException && context.CancellationToken.IsCancellationRequested)
            return new RpcException(new global::Grpc.Core.Status(StatusCode.Cancelled, "Call canceled by the client"));
        var a2a = ex switch
        {
            A2AException e => e,
            JsonException json => A2AException.InvalidParams($"Invalid request: {json.Message}"),
            _ => null,
        };
        if (a2a is null)
        {
            _logger.LogError(ex, "Unhandled error in A2A gRPC call {Method}", context.Method);
            a2a = new A2AException(A2AErrorCode.Internal);
        }
        return GrpcErrors.ToRpcException(a2a);
    }
}
