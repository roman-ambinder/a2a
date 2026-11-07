using System.Text.Json;
using A2A.Grpc;
using A2A.Server.AspNetCore;
using A2A.Server.Streaming;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Pb = Lf.A2a.V1;
using GrpcContext = Grpc.Core.ServerCallContext;

namespace A2A.Server.Grpc;

/// <summary>
/// gRPC binding (spec §10). Implements <c>lf.a2a.v1.A2AService</c> on top of <see cref="IA2ARequestHandler"/>, so behavior,
/// authorization scoping, and validation match the JSON-RPC and REST bindings exactly.
/// </summary>
public sealed class A2AGrpcService : Pb.A2AService.A2AServiceBase
{
    private readonly IA2ARequestHandler _handler;
    private readonly ILogger<A2AGrpcService> _logger;

    /// <summary>Creates the service. The DI container does this.</summary>
    public A2AGrpcService(IA2ARequestHandler handler, ILogger<A2AGrpcService> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    /// <summary>SendMessage (spec §3.1.1) over gRPC.</summary>
    public override Task<Pb.SendMessageResponse> SendMessage(Pb.SendMessageRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.SendMessageResponse>(
            await _handler.SendMessageAsync(ProtoMapper.ToModel<SendMessageRequest>(request), call, ct).ConfigureAwait(false)));

    /// <summary>SendStreamingMessage (spec §3.1.2) over gRPC.</summary>
    public override Task SendStreamingMessage(Pb.SendMessageRequest request, IServerStreamWriter<Pb.StreamResponse> responseStream, GrpcContext context) =>
        Stream(context, responseStream, (call, ct) => _handler.SendStreamingMessageAsync(ProtoMapper.ToModel<SendMessageRequest>(request), call, cancellationToken: ct));

    /// <summary>GetTask (spec §3.1.3) over gRPC.</summary>
    public override Task<Pb.Task> GetTask(Pb.GetTaskRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.Task>(
            await _handler.GetTaskAsync(ProtoMapper.ToModel<GetTaskRequest>(request), call, ct).ConfigureAwait(false)));

    /// <summary>ListTasks (spec §3.1.4) over gRPC.</summary>
    public override Task<Pb.ListTasksResponse> ListTasks(Pb.ListTasksRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.ListTasksResponse>(
            await _handler.ListTasksAsync(ProtoMapper.ToModel<ListTasksRequest>(request), call, ct).ConfigureAwait(false)));

    /// <summary>CancelTask (spec §3.1.5) over gRPC.</summary>
    public override Task<Pb.Task> CancelTask(Pb.CancelTaskRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.Task>(
            await _handler.CancelTaskAsync(ProtoMapper.ToModel<CancelTaskRequest>(request), call, ct).ConfigureAwait(false)));

    /// <summary>SubscribeToTask (spec §3.1.6) over gRPC.</summary>
    public override Task SubscribeToTask(Pb.SubscribeToTaskRequest request, IServerStreamWriter<Pb.StreamResponse> responseStream, GrpcContext context) =>
        Stream(context, responseStream, (call, ct) => _handler.SubscribeToTaskAsync(ProtoMapper.ToModel<SubscribeToTaskRequest>(request), call, cancellationToken: ct));

    /// <summary>CreateTaskPushNotificationConfig (spec §3.1.7) over gRPC.</summary>
    public override Task<Pb.TaskPushNotificationConfig> CreateTaskPushNotificationConfig(Pb.TaskPushNotificationConfig request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.TaskPushNotificationConfig>(
            await _handler.CreateTaskPushNotificationConfigAsync(ProtoMapper.ToModel<TaskPushNotificationConfig>(request), call, ct).ConfigureAwait(false)));

    /// <summary>GetTaskPushNotificationConfig (spec §3.1.8) over gRPC.</summary>
    public override Task<Pb.TaskPushNotificationConfig> GetTaskPushNotificationConfig(Pb.GetTaskPushNotificationConfigRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.TaskPushNotificationConfig>(
            await _handler.GetTaskPushNotificationConfigAsync(ProtoMapper.ToModel<GetTaskPushNotificationConfigRequest>(request), call, ct).ConfigureAwait(false)));

    /// <summary>ListTaskPushNotificationConfigs (spec §3.1.9) over gRPC.</summary>
    public override Task<Pb.ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigs(Pb.ListTaskPushNotificationConfigsRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.ListTaskPushNotificationConfigsResponse>(
            await _handler.ListTaskPushNotificationConfigsAsync(ProtoMapper.ToModel<ListTaskPushNotificationConfigsRequest>(request), call, ct).ConfigureAwait(false)));

    /// <summary>DeleteTaskPushNotificationConfig (spec §3.1.10) over gRPC.</summary>
    public override Task<Empty> DeleteTaskPushNotificationConfig(Pb.DeleteTaskPushNotificationConfigRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) =>
        {
            await _handler.DeleteTaskPushNotificationConfigAsync(ProtoMapper.ToModel<DeleteTaskPushNotificationConfigRequest>(request), call, ct).ConfigureAwait(false);
            return new Empty();
        });

    /// <summary>GetExtendedAgentCard (spec §3.1.11) over gRPC.</summary>
    public override Task<Pb.AgentCard> GetExtendedAgentCard(Pb.GetExtendedAgentCardRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.AgentCard>(
            await _handler.GetExtendedAgentCardAsync(ProtoMapper.ToModel<GetExtendedAgentCardRequest>(request), call, ct).ConfigureAwait(false)));

    // ---- plumbing ----

    // Same principal, tenant, version, extensions and idempotency handling as the HTTP bindings.
    private static ServerCallContext CreateCall(GrpcContext context) => context.GetHttpContext().CreateA2ACallContext();

    private async Task<T> Unary<T>(GrpcContext context, Func<ServerCallContext, CancellationToken, Task<T>> action)
    {
        try
        {
            var call = CreateCall(context);
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
        Func<ServerCallContext, CancellationToken, IAsyncEnumerable<SequencedEvent>> source)
    {
        try
        {
            var call = CreateCall(context);
            _handler.ValidateCall(call);
            var first = true;
            await foreach (var evt in source(call, context.CancellationToken).ConfigureAwait(false))
            {
                if (first)
                {
                    await WriteExtensionsHeaderAsync(context, call).ConfigureAwait(false);
                    first = false;
                }
                await writer.WriteAsync(ProtoMapper.ToProto<Pb.StreamResponse>(evt.Event)).ConfigureAwait(false);
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
