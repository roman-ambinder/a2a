using A2A.Grpc;
using A2A.Server.AspNetCore;
using A2A.Server.Streaming;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pb = Lf.A2a.V1;
using GrpcContext = Grpc.Core.ServerCallContext;

namespace A2A.Server.Grpc;

/// <summary>
/// gRPC binding (spec §10). Implements <c>lf.a2a.v1.A2AService</c> on top of <see cref="A2ARequestHandler"/>, so behavior,
/// authorization scoping, limits and errors match the JSON-RPC and REST bindings.
/// </summary>
/// <remarks>
/// <para>
/// The call context is built exactly as for HTTP: the principal comes from the authenticated user through
/// <see cref="IPrincipalResolver"/>, the tenant from the request's <c>tenant</c> field (and the host's <see cref="ITenantResolver"/>),
/// the remote address from the connection, and <c>a2a-version</c>, <c>a2a-extensions</c> and <c>idempotency-key</c> from the
/// request metadata.
/// </para>
/// <para>
/// Stream resume is not supported over gRPC in this version: gRPC has no per-message metadata to carry an event cursor, so a
/// client that loses a stream calls <c>SubscribeToTask</c> again (a task snapshot, then live events). The server-side stream
/// limits (admission, re-authorization, maximum duration) apply as on the other bindings.
/// </para>
/// </remarks>
public sealed class A2AGrpcService : Pb.A2AService.A2AServiceBase
{
    private readonly A2ARequestHandler _handler;
    private readonly ILogger<A2AGrpcService> _logger;
    private readonly int _maxDepth;
    private readonly bool _includeDetails;

    public A2AGrpcService(A2ARequestHandler handler, IOptions<A2AServerOptions> serverOptions, IHostEnvironment environment, ILogger<A2AGrpcService> logger)
    {
        _handler = handler;
        _logger = logger;
        _maxDepth = serverOptions.Value.Limits.MaxJsonDepth;
        _includeDetails = HttpPlumbing.ShouldIncludeDetails(serverOptions.Value, environment);
    }

    public override Task<Pb.SendMessageResponse> SendMessage(Pb.SendMessageRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.SendMessageResponse, SendMessageResponse>(
            await _handler.SendMessageAsync(In<SendMessageRequest>(request), call, ct).ConfigureAwait(false)));

    public override Task SendStreamingMessage(Pb.SendMessageRequest request, IServerStreamWriter<Pb.StreamResponse> responseStream, GrpcContext context) =>
        Stream(context, responseStream, (call, ct) => _handler.SendStreamingMessageAsync(In<SendMessageRequest>(request), call, ct));

    public override Task<Pb.Task> GetTask(Pb.GetTaskRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.Task, AgentTask>(
            await _handler.GetTaskAsync(In<GetTaskRequest>(request), call, ct).ConfigureAwait(false)));

    public override Task<Pb.ListTasksResponse> ListTasks(Pb.ListTasksRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.ListTasksResponse, ListTasksResponse>(
            await _handler.ListTasksAsync(In<ListTasksRequest>(request), call, ct).ConfigureAwait(false)));

    public override Task<Pb.Task> CancelTask(Pb.CancelTaskRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.Task, AgentTask>(
            await _handler.CancelTaskAsync(In<CancelTaskRequest>(request), call, ct).ConfigureAwait(false)));

    public override Task SubscribeToTask(Pb.SubscribeToTaskRequest request, IServerStreamWriter<Pb.StreamResponse> responseStream, GrpcContext context) =>
        Stream(context, responseStream, (call, ct) => _handler.SubscribeToTaskAsync(In<SubscribeToTaskRequest>(request), call, ct));

    public override Task<Pb.TaskPushNotificationConfig> CreateTaskPushNotificationConfig(Pb.TaskPushNotificationConfig request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.TaskPushNotificationConfig, TaskPushNotificationConfig>(
            await _handler.CreateTaskPushNotificationConfigAsync(In<TaskPushNotificationConfig>(request), call, ct).ConfigureAwait(false)));

    public override Task<Pb.TaskPushNotificationConfig> GetTaskPushNotificationConfig(Pb.GetTaskPushNotificationConfigRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.TaskPushNotificationConfig, TaskPushNotificationConfig>(
            await _handler.GetTaskPushNotificationConfigAsync(In<GetTaskPushNotificationConfigRequest>(request), call, ct).ConfigureAwait(false)));

    public override Task<Pb.ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigs(Pb.ListTaskPushNotificationConfigsRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.ListTaskPushNotificationConfigsResponse, ListTaskPushNotificationConfigsResponse>(
            await _handler.ListTaskPushNotificationConfigsAsync(In<ListTaskPushNotificationConfigsRequest>(request), call, ct).ConfigureAwait(false)));

    public override Task<Empty> DeleteTaskPushNotificationConfig(Pb.DeleteTaskPushNotificationConfigRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) =>
        {
            await _handler.DeleteTaskPushNotificationConfigAsync(In<DeleteTaskPushNotificationConfigRequest>(request), call, ct).ConfigureAwait(false);
            return new Empty();
        });

    public override Task<Pb.AgentCard> GetExtendedAgentCard(Pb.GetExtendedAgentCardRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<Pb.AgentCard, AgentCard>(
            await _handler.GetExtendedAgentCardAsync(In<GetExtendedAgentCardRequest>(request), call, ct).ConfigureAwait(false)));

    // ---- plumbing ----

    /// <summary>Maps an incoming request with the request depth limit.</summary>
    private T In<T>(IMessage request) => ProtoMapper.ToModel<T>(request, _maxDepth);

    private async Task<T> Unary<T>(GrpcContext context, Func<ServerCallContext, CancellationToken, Task<T>> action)
    {
        try
        {
            var call = HttpPlumbing.CreateCallContext(context.GetHttpContext(), ProtocolBindings.Grpc);
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
            var call = HttpPlumbing.CreateCallContext(context.GetHttpContext(), ProtocolBindings.Grpc);
            _handler.ValidateCall(call);
            var first = true;
            await foreach (var evt in source(call, context.CancellationToken).ConfigureAwait(false))
            {
                if (first)
                {
                    await WriteExtensionsHeaderAsync(context, call).ConfigureAwait(false);
                    first = false;
                }
                await writer.WriteAsync(ProtoMapper.ToProto<Pb.StreamResponse, StreamResponse>(evt.Event)).ConfigureAwait(false);
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

    /// <summary>
    /// The gRPC status for a failure: A2A errors through the shared table; unexpected errors are logged and sent as a generic
    /// INTERNAL (with the exception text only when <see cref="A2AServerOptions.IncludeExceptionDetails"/> allows it).
    /// </summary>
    private RpcException Translate(Exception ex, GrpcContext context)
    {
        if (ex is OperationCanceledException && context.CancellationToken.IsCancellationRequested)
            return new RpcException(new global::Grpc.Core.Status(StatusCode.Cancelled, "Call canceled by the client"));
        return GrpcErrors.ToRpcException(HttpPlumbing.Normalize(ex, _logger, _includeDetails));
    }
}
