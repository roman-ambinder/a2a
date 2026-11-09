using A2A.Grpc;
using A2A.Server.AspNetCore;
using A2A.Server.Streaming;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using A2aPb = Lf.A2a.V1;
using GrpcContext = Grpc.Core.ServerCallContext;

namespace A2A.Server.Grpc;

/// <summary>
/// gRPC binding (spec §10). Implements <c>lf.a2a.v1.A2AService</c> on top of <see cref="IA2ARequestHandler"/>, so behavior,
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
public sealed class A2AGrpcService : A2aPb.A2AService.A2AServiceBase
{
    private readonly IA2ARequestHandler _handler;
    private readonly GrpcCallSupport _support;
    private readonly int _maxDepth;

    /// <summary>Creates the service. The DI container does this.</summary>
    public A2AGrpcService(IA2ARequestHandler handler, IOptions<A2AServerOptions> serverOptions, IHostEnvironment environment, ILogger<A2AGrpcService> logger)
    {
        _handler = handler;
        _maxDepth = serverOptions.Value.Limits.MaxJsonDepth;
        _support = new GrpcCallSupport(logger, GrpcCallSupport.ShouldIncludeDetails(serverOptions, environment));
    }

    /// <summary>SendMessage (spec §3.1.1) over gRPC.</summary>
    public override Task<A2aPb.SendMessageResponse> SendMessage(A2aPb.SendMessageRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<A2aPb.SendMessageResponse, SendMessageResponse>(
            await _handler.SendMessageAsync(In<SendMessageRequest>(request), call, ct).ConfigureAwait(false)));

    /// <summary>SendStreamingMessage (spec §3.1.2) over gRPC.</summary>
    public override Task SendStreamingMessage(A2aPb.SendMessageRequest request, IServerStreamWriter<A2aPb.StreamResponse> responseStream, GrpcContext context) =>
        Stream(context, responseStream, (call, ct) => _handler.SendStreamingMessageAsync(In<SendMessageRequest>(request), call, cancellationToken: ct));

    /// <summary>
    /// A2A 1.1 SendLiveMessage (§3.1.12) over gRPC: a native two-way stream. The first request binds the stream to a task;
    /// a later request that cannot be taken comes back as a stream error and the stream stays open.
    /// </summary>
    public override Task SendLiveMessage(IAsyncStreamReader<A2aPb.StreamRequest> requestStream, IServerStreamWriter<A2aPb.StreamResponse> responseStream, GrpcContext context) =>
        Stream(context, responseStream, (call, ct) => _handler.SendLiveMessageAsync(ReadRequests(requestStream, ct), call, ct));

    /// <summary>
    /// The client's requests; one that does not map (malformed, or a kind this SDK does not know) is passed on empty, with its
    /// error and the id of its message (or first artifact), to be refused on its own.
    /// </summary>
    private async IAsyncEnumerable<StreamRequest> ReadRequests(IAsyncStreamReader<A2aPb.StreamRequest> reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        while (await reader.MoveNext(ct).ConfigureAwait(false))
        {
            StreamRequest request;
            try { request = In<StreamRequest>(reader.Current); }
            catch (A2AException ex)
            {
                var send = reader.Current.SendMessage;
                var referenceId = !string.IsNullOrEmpty(send?.Message?.MessageId) ? send.Message.MessageId
                    : send?.ArtifactUpdates.Select(u => u.Artifact?.ArtifactId).FirstOrDefault(id => !string.IsNullOrEmpty(id));
                request = MalformedStreamRequest.Create(ex, referenceId);
            }
            yield return request;
        }
    }

    /// <summary>GetTask (spec §3.1.3) over gRPC.</summary>
    public override Task<A2aPb.Task> GetTask(A2aPb.GetTaskRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<A2aPb.Task, AgentTask>(
            await _handler.GetTaskAsync(In<GetTaskRequest>(request), call, ct).ConfigureAwait(false)));

    /// <summary>ListTasks (spec §3.1.4) over gRPC.</summary>
    public override Task<A2aPb.ListTasksResponse> ListTasks(A2aPb.ListTasksRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<A2aPb.ListTasksResponse, ListTasksResponse>(
            await _handler.ListTasksAsync(In<ListTasksRequest>(request), call, ct).ConfigureAwait(false)));

    /// <summary>CancelTask (spec §3.1.5) over gRPC.</summary>
    public override Task<A2aPb.Task> CancelTask(A2aPb.CancelTaskRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<A2aPb.Task, AgentTask>(
            await _handler.CancelTaskAsync(In<CancelTaskRequest>(request), call, ct).ConfigureAwait(false)));

    /// <summary>SubscribeToTask (spec §3.1.6) over gRPC.</summary>
    public override Task SubscribeToTask(A2aPb.SubscribeToTaskRequest request, IServerStreamWriter<A2aPb.StreamResponse> responseStream, GrpcContext context) =>
        Stream(context, responseStream, (call, ct) => _handler.SubscribeToTaskAsync(In<SubscribeToTaskRequest>(request), call, cancellationToken: ct));

    /// <summary>CreateTaskPushNotificationConfig (spec §3.1.7) over gRPC.</summary>
    public override Task<A2aPb.TaskPushNotificationConfig> CreateTaskPushNotificationConfig(A2aPb.TaskPushNotificationConfig request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<A2aPb.TaskPushNotificationConfig, TaskPushNotificationConfig>(
            await _handler.CreateTaskPushNotificationConfigAsync(In<TaskPushNotificationConfig>(request), call, ct).ConfigureAwait(false)));

    /// <summary>GetTaskPushNotificationConfig (spec §3.1.8) over gRPC.</summary>
    public override Task<A2aPb.TaskPushNotificationConfig> GetTaskPushNotificationConfig(A2aPb.GetTaskPushNotificationConfigRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<A2aPb.TaskPushNotificationConfig, TaskPushNotificationConfig>(
            await _handler.GetTaskPushNotificationConfigAsync(In<GetTaskPushNotificationConfigRequest>(request), call, ct).ConfigureAwait(false)));

    /// <summary>ListTaskPushNotificationConfigs (spec §3.1.9) over gRPC.</summary>
    public override Task<A2aPb.ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigs(A2aPb.ListTaskPushNotificationConfigsRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<A2aPb.ListTaskPushNotificationConfigsResponse, ListTaskPushNotificationConfigsResponse>(
            await _handler.ListTaskPushNotificationConfigsAsync(In<ListTaskPushNotificationConfigsRequest>(request), call, ct).ConfigureAwait(false)));

    /// <summary>DeleteTaskPushNotificationConfig (spec §3.1.10) over gRPC.</summary>
    public override Task<Empty> DeleteTaskPushNotificationConfig(A2aPb.DeleteTaskPushNotificationConfigRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) =>
        {
            await _handler.DeleteTaskPushNotificationConfigAsync(In<DeleteTaskPushNotificationConfigRequest>(request), call, ct).ConfigureAwait(false);
            return new Empty();
        });

    /// <summary>GetExtendedAgentCard (spec §3.1.11) over gRPC.</summary>
    public override Task<A2aPb.AgentCard> GetExtendedAgentCard(A2aPb.GetExtendedAgentCardRequest request, GrpcContext context) =>
        Unary(context, async (call, ct) => ProtoMapper.ToProto<A2aPb.AgentCard, AgentCard>(
            await _handler.GetExtendedAgentCardAsync(In<GetExtendedAgentCardRequest>(request), call, ct).ConfigureAwait(false)));

    // ---- plumbing ----

    /// <summary>Maps an incoming request with the request depth limit.</summary>
    private T In<T>(IMessage request) => ProtoMapper.ToModel<T>(request, _maxDepth);

    private async Task<T> Unary<T>(GrpcContext context, Func<ServerCallContext, CancellationToken, Task<T>> action)
    {
        ServerCallContext? call = null;
        try
        {
            call = context.GetHttpContext().CreateA2ACallContext(ProtocolBindings.Grpc);
            _handler.ValidateCall(call);
            var result = await action(call, context.CancellationToken).ConfigureAwait(false);
            await GrpcCallSupport.WriteExtensionsHeaderAsync(context, call).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw _support.Fail(ex, context, call, headersSent: false);
        }
    }

    /// <summary>
    /// A2A 1.1: a stored event carries the task's generation, which equals its sequence. An event read back from the store's
    /// bytes may not have it in its model, so it is set here when missing.
    /// </summary>
    private static void FillGeneration(A2aPb.StreamResponse proto, long sequence)
    {
        switch (proto.PayloadCase)
        {
            case A2aPb.StreamResponse.PayloadOneofCase.Task when proto.Task.Generation == 0: proto.Task.Generation = sequence; break;
            case A2aPb.StreamResponse.PayloadOneofCase.StatusUpdate when proto.StatusUpdate.Generation == 0: proto.StatusUpdate.Generation = sequence; break;
            case A2aPb.StreamResponse.PayloadOneofCase.ArtifactUpdate when proto.ArtifactUpdate.Generation == 0: proto.ArtifactUpdate.Generation = sequence; break;
            case A2aPb.StreamResponse.PayloadOneofCase.MessageUpdate when proto.MessageUpdate.Generation == 0: proto.MessageUpdate.Generation = sequence; break;
        }
    }

    private async Task Stream(GrpcContext context, IServerStreamWriter<A2aPb.StreamResponse> writer,
        Func<ServerCallContext, CancellationToken, IAsyncEnumerable<SequencedEvent>> source)
    {
        ServerCallContext? call = null;
        var headersSent = false;
        try
        {
            call = context.GetHttpContext().CreateA2ACallContext(ProtocolBindings.Grpc);
            _handler.ValidateCall(call);
            await foreach (var evt in source(call, context.CancellationToken).ConfigureAwait(false))
            {
                if (!headersSent)
                {
                    headersSent = true;
                    await GrpcCallSupport.WriteExtensionsHeaderAsync(context, call).ConfigureAwait(false);
                }
                // Stream events are mapped field by field (ProtoMapper's direct path), not through JSON.
                var proto = ProtoMapper.ToProto<A2aPb.StreamResponse, StreamResponse>(evt.Event);
                if (evt.Sequence > 0 && A2AProtocol.IsVersion11OrLater(call.ProtocolVersion)) FillGeneration(proto, evt.Sequence);
                await writer.WriteAsync(proto).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            // gRPC carries the status in trailers, so errors after the first message still reach the client cleanly.
            // A stream that sent its first event already echoed the extensions in the initial metadata.
            throw _support.Fail(ex, context, call, headersSent);
        }
    }
}
