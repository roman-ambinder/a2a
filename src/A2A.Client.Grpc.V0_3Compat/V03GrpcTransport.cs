using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Client.Grpc;
using A2A.Client.Resilience;
using A2A.Client.V0_3Compat;
using A2A.V0_3Compat;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using V03Pb = A2a.V1;

namespace A2A.Client.Grpc.V0_3Compat;

/// <summary>
///     The v0.3 gRPC transport: the service <c>a2a.v1.A2AService</c> of the v0.3 proto. Each message goes through its
///     ProtoJSON form with the v0.3 HTTP+JSON codec, the same mapping of the same proto. It sends <c>a2a-version: 0.3</c>
///     and <c>x-a2a-extensions</c> metadata, reads errors as the v0.3 Python SDK sends them (<see cref="V03GrpcErrors" />),
///     and retries an idempotent call that found the server unavailable, up to <see cref="A2AClientOptions.MaxRetries" />.
///     v0.3 gRPC has no ListTasks.
/// </summary>
internal sealed class V03GrpcTransport : IA2ATransport
{
    private static readonly JsonFormatter Formatter = new(JsonFormatter.Settings.Default);
    private static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private readonly V03Pb.A2AService.A2AServiceClient _client;
    private readonly A2AClientOptions _options;

    public V03GrpcTransport(CallInvoker invoker, A2AClientOptions options)
    {
        _client = new V03Pb.A2AService.A2AServiceClient(invoker);
        _options = options;
    }

    private static V03ProtoJsonCodec Codec => V03ProtoJsonCodec.Instance;

    public async Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var proto = To<V03Pb.SendMessageRequest>(Codec.WriteSendRequest(request));
        var result = await UnaryAsync(false, options, o => _client.SendMessageAsync(proto, o), cancellationToken)
            .ConfigureAwait(false);
        return Read(() => Codec.ReadSendResult(ToNode(result)), "send result");
    }

    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var proto = To<V03Pb.SendMessageRequest>(Codec.WriteSendRequest(request));
        return StreamAsync(o => _client.SendStreamingMessage(proto, o), options, cancellationToken);
    }

    public async Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var proto = new V03Pb.GetTaskRequest { Name = $"tasks/{request.Id}", HistoryLength = request.HistoryLength ?? 0 };
        var result = await UnaryAsync(true, options, o => _client.GetTaskAsync(proto, o), cancellationToken)
            .ConfigureAwait(false);
        var task = Read(() => Codec.ReadTask(ToNode(result)), "task");
        // proto3 int32 has no presence, so 0 ("no history") reads as "not set" on the server and the whole history comes
        // back. Honor the request here.
        if (request.HistoryLength == 0) task.History = null;
        return task;
    }

    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // The v0.3 spec table lists ListTask for gRPC, but the v0.3 proto has no such method.
        return Task.FromException<ListTasksResponse>(V03ClientSupport.Unsupported("ListTasks over gRPC"));
    }

    public async Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // The v0.3 proto CancelTaskRequest has only the task name.
        if (request.Metadata is { Count: > 0 }) V03LossReporter.Drop("tasks/cancel.metadata");
        var proto = new V03Pb.CancelTaskRequest { Name = $"tasks/{request.Id}" };
        var result = await UnaryAsync(true, options, o => _client.CancelTaskAsync(proto, o), cancellationToken)
            .ConfigureAwait(false);
        return Read(() => Codec.ReadTask(ToNode(result)), "task");
    }

    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var proto = new V03Pb.TaskSubscriptionRequest { Name = $"tasks/{request.Id}" };
        return StreamAsync(o => _client.TaskSubscription(proto, o), options, cancellationToken);
    }

    public async Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(
        TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var taskId = config.TaskId ?? string.Empty;
        var proto = new V03Pb.CreateTaskPushNotificationConfigRequest
        {
            Parent = $"tasks/{taskId}", ConfigId = config.Id ?? string.Empty,
            Config = To<V03Pb.TaskPushNotificationConfig>(Codec.WritePushConfig(config))
        };
        var result = await UnaryAsync(false, options, o => _client.CreateTaskPushNotificationConfigAsync(proto, o),
            cancellationToken).ConfigureAwait(false);
        return Read(() => Codec.ReadPushConfig(ToNode(result)), "push config");
    }

    public async Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(
        GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var proto = new V03Pb.GetTaskPushNotificationConfigRequest
            { Name = V03ProtoJsonCodec.PushConfigName(request.TaskId, request.Id) };
        var result = await UnaryAsync(true, options, o => _client.GetTaskPushNotificationConfigAsync(proto, o),
            cancellationToken).ConfigureAwait(false);
        return Read(() => Codec.ReadPushConfig(ToNode(result)), "push config");
    }

    public async Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(
        ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var proto = new V03Pb.ListTaskPushNotificationConfigRequest
        {
            Parent = $"tasks/{request.TaskId}", PageSize = request.PageSize ?? 0, PageToken = request.PageToken ?? ""
        };
        var result = await UnaryAsync(true, options, o => _client.ListTaskPushNotificationConfigAsync(proto, o),
            cancellationToken).ConfigureAwait(false);
        return Read(() => new ListTaskPushNotificationConfigsResponse
        {
            Configs = result.Configs.Select(c => Codec.ReadPushConfig(ToNode(c))).ToList(),
            NextPageToken = result.NextPageToken.Length > 0 ? result.NextPageToken : null
        }, "push config list");
    }

    public async Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var proto = new V03Pb.DeleteTaskPushNotificationConfigRequest
            { Name = V03ProtoJsonCodec.PushConfigName(request.TaskId, request.Id) };
        await UnaryAsync(false, options, o => _client.DeleteTaskPushNotificationConfigAsync(proto, o), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        // v0.3 gRPC has one card call, GetAgentCard; the v0.3 Python client uses it for the extended card.
        var result = await UnaryAsync(true, options, o => _client.GetAgentCardAsync(new V03Pb.GetAgentCardRequest(), o),
            cancellationToken).ConfigureAwait(false);
        return Read(() => V03CardCodec.FromV03(V03CardCodec.FromProtoCard(V03Json.Obj(ToNode(result), "card"))),
            "Agent Card");
    }

    // ---- plumbing ----

    private async Task<TOut> UnaryAsync<TOut>(bool idempotent, A2ARequestOptions? options,
        Func<CallOptions, AsyncUnaryCall<TOut>> invoke, CancellationToken ct)
    {
        var timeout = options?.Timeout ?? A2ARequestOptions.DefaultTimeout;
        var maxAttempts = 1 + (idempotent ? options?.MaxRetries ?? _options.MaxRetries : 0);
        for (var attempt = 1;; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            DateTime? deadline = timeout == Timeout.InfiniteTimeSpan ? null : DateTime.UtcNow + timeout;
            try
            {
                using var call = invoke(CallOptions(options, deadline, ct));
                return await call.ResponseAsync.ConfigureAwait(false);
            }
            catch (RpcException ex) when (attempt < maxAttempts && ex.StatusCode == StatusCode.Unavailable &&
                                          !ct.IsCancellationRequested)
            {
                await Task.Delay(RetryPolicy.Delay(attempt + 1, null), _options.TimeProvider, ct).ConfigureAwait(false);
            }
            catch (RpcException ex)
            {
                throw Translate(ex, timeout, ct);
            }
        }
    }

    private async IAsyncEnumerable<StreamResponse> StreamAsync(
        Func<CallOptions, AsyncServerStreamingCall<V03Pb.StreamResponse>> start, A2ARequestOptions? options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var call = start(CallOptions(options, null, ct));
        var stream = call.ResponseStream;
        while (true)
        {
            bool moved;
            try
            {
                moved = await stream.MoveNext(ct).ConfigureAwait(false);
            }
            catch (RpcException ex)
            {
                throw Translate(ex, Timeout.InfiniteTimeSpan, ct);
            }

            if (!moved) yield break;
            // An event of a kind this client does not know (a newer proto) is skipped, as on the other bindings.
            if (stream.Current.PayloadCase == V03Pb.StreamResponse.PayloadOneofCase.None) continue;
            var final = false;
            var evt = Read(() => Codec.ReadStreamEvent(ToNode(stream.Current), out final), "stream event");
            yield return evt;
            // As on the v0.3 HTTP transports: the stream ends on final, or when the server closes it.
            if (final) yield break;
        }
    }

    /// <summary>
    ///     Metadata in the order of the HTTP transports: <c>a2a-version</c> 0.3, <c>x-a2a-extensions</c>, then
    ///     <see cref="A2ARequestOptions.Headers" />.
    /// </summary>
    private CallOptions CallOptions(A2ARequestOptions? options, DateTime? deadline, CancellationToken ct)
    {
        if (options?.ConfigureRequest is not null)
            throw new NotSupportedException(
                "The v0.3 gRPC transport does not support A2ARequestOptions.ConfigureRequest. Use Headers (sent as gRPC metadata).");
        var headers = new Metadata { { "a2a-version", V03Compat.HeaderVersion } };
        var extensions = options?.Extensions is { Count: > 0 } perCall
            ? _options.Extensions.Concat(perCall).Distinct(StringComparer.Ordinal)
            : _options.Extensions;
        var joined = string.Join(",", extensions);
        if (joined.Length > 0) headers.Add(V03Compat.ExtensionsHeader.ToLowerInvariant(), joined);
        if (options is not null)
            foreach (var (name, value) in options.Headers)
                headers.Add(name.ToLowerInvariant(), value);
        return new CallOptions(headers, deadline, ct);
    }

    private static Exception Translate(RpcException ex, TimeSpan timeout, CancellationToken ct)
    {
        if (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
            return new OperationCanceledException(ex.Message, ex, ct);
        if (ex.StatusCode == StatusCode.DeadlineExceeded && timeout != Timeout.InfiniteTimeSpan)
            return new TaskCanceledException($"The request timed out after {timeout.TotalSeconds:0.###} s.",
                new TimeoutException(null, ex));
        return V03GrpcErrors.FromStatus((int)ex.StatusCode, ex.Status.Detail,
            V03GrpcErrors.ParseRetryAfter(ex.Trailers?.GetValue(V03GrpcErrors.RetryAfterKey)));
    }

    private static T To<T>(JsonNode json) where T : IMessage<T>, new()
    {
        return Parser.Parse<T>(json.ToJsonString());
    }

    private static JsonNode ToNode(IMessage message)
    {
        return JsonNode.Parse(Formatter.Format(message))!;
    }

    /// <summary>What the agent sent that the model cannot hold is the agent's error, as on the HTTP transports.</summary>
    private static T Read<T>(Func<T> read, string what)
    {
        try
        {
            return read();
        }
        catch (JsonException ex)
        {
            throw new A2AException(A2AErrorCode.InvalidAgentResponse, $"Malformed v0.3 {what}: {ex.Message}", ex);
        }
    }
}

/// <summary>Creates v0.3 gRPC transports for the v0.3 GRPC interfaces of a card. Added by <see cref="A2AGrpcV03ClientOptionsExtensions.UseGrpcV03" />.</summary>
internal sealed class V03GrpcTransportFactory(GrpcChannelOptions? channelOptions) : IV03TransportFactory
{
    // The channel pool of the v1.0 gRPC transport (GrpcChannelPool): one channel per address and channel options, leased
    // per call, and disposed when no call uses it after an idle timeout or past an idle limit.
    public string ProtocolBinding => ProtocolBindings.Grpc;

    public bool CanCreate(AgentInterface agentInterface)
    {
        ArgumentNullException.ThrowIfNull(agentInterface);
        if (!V03Compat.IsV03Version(agentInterface.ProtocolVersion) || string.IsNullOrWhiteSpace(agentInterface.Url))
            return false;
        try
        {
            _ = GrpcTransport.ParseAddress(agentInterface.Url);
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    public IA2ATransport Create(AgentInterface agentInterface, HttpClient httpClient, A2AClientOptions options)
    {
        return new V03GrpcTransport(
            GrpcChannelPool.For(channelOptions).CallInvoker(GrpcTransport.ParseAddress(agentInterface.Url)), options);
    }
}

/// <summary>Opt-in to calling v0.3 agents over gRPC.</summary>
public static class A2AGrpcV03ClientOptionsExtensions
{
    /// <summary>
    ///     Lets <see cref="V03CompatClientFactory" /> use the v0.3 <c>GRPC</c> interfaces of a card (service
    ///     <c>a2a.v1.A2AService</c>), in card order with the v0.3 JSON-RPC and HTTP+JSON ones. Adds <c>GRPC</c> to
    ///     <see cref="A2AClientOptions.SupportedBindings" />. A card that offers a v1.x interface still takes the v1.0 path
    ///     (add <c>UseGrpc()</c> for v1.0 gRPC).
    /// </summary>
    /// <param name="options">The client options. Changed and returned.</param>
    /// <param name="channelOptions">Options of the gRPC channels (one per address), for example an HTTP handler.</param>
    /// <returns><paramref name="options" />.</returns>
    public static A2AClientOptions UseGrpcV03(this A2AClientOptions options, GrpcChannelOptions? channelOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        V03Compat.RequireSameVersion(typeof(A2AGrpcV03ClientOptionsExtensions).Assembly,
            typeof(V03CompatClientFactory).Assembly, typeof(A2AClient).Assembly, typeof(V03Compat).Assembly,
            typeof(AgentCard).Assembly);
        options.TransportFactories.Add(new V03GrpcTransportFactory(channelOptions));
        if (!options.SupportedBindings.Contains(ProtocolBindings.Grpc))
            options.SupportedBindings.Add(ProtocolBindings.Grpc);
        return options;
    }
}
