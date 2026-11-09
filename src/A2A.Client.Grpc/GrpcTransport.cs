using System.Runtime.CompilerServices;
using A2A.Client.Resilience;
using A2A.Diagnostics;
using A2A.Grpc;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Pb = Lf.A2a.V1;

namespace A2A.Client.Grpc;

/// <summary>gRPC binding (spec §10). Wrap in <see cref="A2AClient"/> for tenant handling and capability checks.</summary>
/// <remarks>
/// <para>
/// Per-call behavior matches the HTTP transports: <see cref="A2ARequestOptions.Headers"/> become request metadata (a key
/// replaces the SDK value of the same name; keys are lowercase on the wire), <see cref="A2ARequestOptions.Timeout"/> is the
/// deadline of each unary attempt (default 100 seconds; streams have none), SendMessage and SendStreamingMessage send the
/// <c>idempotency-key</c> metadata (default: the message id), and unary calls are retried like the HTTP transports
/// (<see cref="A2ARequestOptions.MaxRetries"/>): on errors the server marked retryable, on <c>UNAVAILABLE</c> sent by the
/// server, and on transport failures; SendMessage only when the request provably never left; push config create and
/// delete never.
/// </para>
/// <para>
/// Not supported: <see cref="A2ARequestOptions.ConfigureRequest"/> (there is no <see cref="HttpRequestMessage"/>; a call that
/// sets it fails with <see cref="NotSupportedException"/> instead of going out without the hook; use
/// <see cref="A2ARequestOptions.Headers"/> or a gRPC interceptor), and stream resume (gRPC carries no event cursor; after a
/// lost stream, call <c>SubscribeToTask</c> again).
/// </para>
/// <para>
/// Streaming calls (<see cref="SendStreamingMessageAsync"/>, <see cref="SubscribeToTaskAsync"/>) start when the stream is first
/// enumerated: a stream that is never read sends nothing.
/// </para>
/// </remarks>
public sealed class GrpcTransport : IA2AClient, IDisposable, ITransportBinding
{
    string ITransportBinding.Binding => ProtocolBindings.Grpc;

    private readonly Pb.A2AService.A2AServiceClient _client;
    private readonly A2AClientOptions _options;
    private readonly GrpcChannel? _ownedChannel;

    /// <summary>Uses an existing channel. The caller owns and disposes it.</summary>
    public GrpcTransport(CallInvoker callInvoker, A2AClientOptions? options = null)
    {
        _client = new Pb.A2AService.A2AServiceClient(callInvoker ?? throw new ArgumentNullException(nameof(callInvoker)));
        _options = options ?? new A2AClientOptions();
    }

    /// <summary>Creates and owns a channel to <paramref name="address"/>.</summary>
    public GrpcTransport(Uri address, A2AClientOptions? options = null, GrpcChannelOptions? channelOptions = null)
        : this(CreateChannel(address, channelOptions, out var channel).CreateCallInvoker(), options)
    {
        _ownedChannel = channel;
    }

    private static GrpcChannel CreateChannel(Uri address, GrpcChannelOptions? options, out GrpcChannel channel) =>
        channel = GrpcChannel.ForAddress(address, options ?? new GrpcChannelOptions());

    /// <summary>
    /// Normalizes an Agent Card gRPC URL. The spec allows "host:port"; that form is read as HTTPS.
    /// </summary>
    public static Uri ParseAddress(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)) return u;
        return new Uri("https://" + url.TrimStart('/'));
    }

    /// <inheritdoc />
    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var key = PrepareSend(request, options);
        var proto = ProtoMapper.ToProto<Pb.SendMessageRequest, SendMessageRequest>(request);
        return UnaryAsync(RetryMode.UnsentOnly, options, key, o => _client.SendMessageAsync(proto, o), ProtoMapper.ToModel<SendMessageResponse>, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        RejectConfigureRequest(options);
        var key = PrepareSend(request, options);
        var proto = ProtoMapper.ToProto<Pb.SendMessageRequest, SendMessageRequest>(request);
        return StreamAsync(o => _client.SendStreamingMessage(proto, o), options, key, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var proto = ProtoMapper.ToProto<Pb.GetTaskRequest, GetTaskRequest>(request);
        return UnaryAsync(RetryMode.Idempotent, options, null, o => _client.GetTaskAsync(proto, o), ProtoMapper.ToModel<AgentTask>, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var proto = ProtoMapper.ToProto<Pb.ListTasksRequest, ListTasksRequest>(request);
        return UnaryAsync(RetryMode.Idempotent, options, null, o => _client.ListTasksAsync(proto, o), ProtoMapper.ToModel<ListTasksResponse>, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var proto = ProtoMapper.ToProto<Pb.CancelTaskRequest, CancelTaskRequest>(request);
        return UnaryAsync(RetryMode.Idempotent, options, null, o => _client.CancelTaskAsync(proto, o), ProtoMapper.ToModel<AgentTask>, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        RejectConfigureRequest(options);
        var proto = ProtoMapper.ToProto<Pb.SubscribeToTaskRequest, SubscribeToTaskRequest>(request);
        return StreamAsync(o => _client.SubscribeToTask(proto, o), options, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var proto = ProtoMapper.ToProto<Pb.TaskPushNotificationConfig, TaskPushNotificationConfig>(config);
        return UnaryAsync(RetryMode.None, options, null, o => _client.CreateTaskPushNotificationConfigAsync(proto, o), ProtoMapper.ToModel<TaskPushNotificationConfig>, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var proto = ProtoMapper.ToProto<Pb.GetTaskPushNotificationConfigRequest, GetTaskPushNotificationConfigRequest>(request);
        return UnaryAsync(RetryMode.Idempotent, options, null, o => _client.GetTaskPushNotificationConfigAsync(proto, o), ProtoMapper.ToModel<TaskPushNotificationConfig>, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var proto = ProtoMapper.ToProto<Pb.ListTaskPushNotificationConfigsRequest, ListTaskPushNotificationConfigsRequest>(request);
        return UnaryAsync(RetryMode.Idempotent, options, null, o => _client.ListTaskPushNotificationConfigsAsync(proto, o), ProtoMapper.ToModel<ListTaskPushNotificationConfigsResponse>, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var proto = ProtoMapper.ToProto<Pb.DeleteTaskPushNotificationConfigRequest, DeleteTaskPushNotificationConfigRequest>(request);
        return UnaryAsync(RetryMode.None, options, null, o => _client.DeleteTaskPushNotificationConfigAsync(proto, o), _ => true, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var proto = ProtoMapper.ToProto<Pb.GetExtendedAgentCardRequest, GetExtendedAgentCardRequest>(request);
        return UnaryAsync(RetryMode.Idempotent, options, null, o => _client.GetExtendedAgentCardAsync(proto, o), ProtoMapper.ToModel<AgentCard>, cancellationToken);
    }

    // ---- plumbing ----

    /// <summary>The key for SendMessage: the caller's, else the message id. Fills an empty message id first (as the HTTP transports do).</summary>
    private static string? PrepareSend(SendMessageRequest request, A2ARequestOptions? call)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Message is not { } message) return call?.IdempotencyKey;
        if (string.IsNullOrWhiteSpace(message.MessageId)) message.MessageId = Message.NewId();
        return string.IsNullOrEmpty(call?.IdempotencyKey) ? message.MessageId : call.IdempotencyKey;
    }

    private static void RejectConfigureRequest(A2ARequestOptions? call)
    {
        if (call?.ConfigureRequest is not null)
            throw new NotSupportedException("GrpcTransport does not support A2ARequestOptions.ConfigureRequest. Use Headers (sent as gRPC metadata) or a gRPC interceptor.");
    }

    /// <summary>
    /// Request metadata in the order of the HTTP transports: <c>a2a-version</c>, <c>a2a-extensions</c>, <c>idempotency-key</c>,
    /// the W3C <c>traceparent</c>/<c>tracestate</c> of the current activity, then <see cref="A2ARequestOptions.Headers"/>
    /// (replacing an SDK entry of the same name).
    /// </summary>
    private CallOptions CreateCallOptions(A2ARequestOptions? call, string? idempotencyKey, DateTime? deadline, CancellationToken ct)
    {
        var headers = new Metadata();
        Set(headers, GrpcErrors.VersionKey, _options.ProtocolVersion);
        var extensions = call?.Extensions is { Count: > 0 } perCall ? _options.Extensions.Concat(perCall).Distinct(StringComparer.Ordinal) : _options.Extensions;
        var joined = string.Join(",", extensions);
        if (joined.Length > 0) Set(headers, GrpcErrors.ExtensionsKey, joined);
        if (!string.IsNullOrEmpty(idempotencyKey)) Set(headers, GrpcErrors.IdempotencyKeyKey, idempotencyKey);
        A2ATelemetry.InjectTraceContext(System.Diagnostics.Activity.Current, (name, value) => Set(headers, name, value));
        if (call is not null)
            foreach (var (name, value) in call.Headers) Set(headers, name.ToLowerInvariant(), value);
        return new CallOptions(headers, deadline, ct);
    }

    private static void Set(Metadata headers, string key, string value)
    {
        for (var i = headers.Count - 1; i >= 0; i--)
            if (string.Equals(headers[i].Key, key, StringComparison.Ordinal)) headers.RemoveAt(i);
        headers.Add(key, value);
    }

    /// <summary>
    /// Runs one unary operation: each attempt gets the call timeout as its deadline, and failed attempts are retried under
    /// <paramref name="retry"/>, at most <see cref="A2ARequestOptions.MaxRetries"/> (else <see cref="A2AClientOptions.MaxRetries"/>) times.
    /// </summary>
    private async Task<TModel> UnaryAsync<TProto, TModel>(RetryMode retry, A2ARequestOptions? call, string? idempotencyKey,
        Func<CallOptions, AsyncUnaryCall<TProto>> invoke, Func<TProto, TModel> map, CancellationToken ct) where TProto : IMessage
    {
        RejectConfigureRequest(call);
        var timeout = call?.Timeout ?? A2ARequestOptions.DefaultTimeout;
        var maxAttempts = 1 + (call?.MaxRetries ?? _options.MaxRetries);
        for (var n = 1; ; n++)
        {
            ct.ThrowIfCancellationRequested();
            DateTime? deadline = timeout == Timeout.InfiniteTimeSpan ? null : DateTime.UtcNow + timeout;
            TProto response;
            try
            {
                using var pending = invoke(CreateCallOptions(call, idempotencyKey, deadline, ct));
                response = await pending.ResponseAsync.ConfigureAwait(false);
            }
            catch (RpcException ex)
            {
                if (n < maxAttempts && !ct.IsCancellationRequested && ShouldRetry(retry, ex, out var retryAfter))
                {
                    await Task.Delay(RetryPolicy.Delay(n + 1, retryAfter), _options.TimeProvider, ct).ConfigureAwait(false);
                    continue;
                }
                throw Translate(ex, timeout, ct);
            }
            return map(response);
        }
    }

    /// <summary>
    /// The HTTP transports' retry rules applied to a gRPC failure: a client-side transport failure (an
    /// <see cref="HttpRequestException"/> or <see cref="IOException"/> behind the status) is judged like an
    /// <see cref="HttpRequestException"/> (SendMessage only when it provably never reached the server); other client-side
    /// failures, such as a response that cannot be parsed, are not transport failures; <c>UNAVAILABLE</c> from the server is
    /// the gRPC form of HTTP 503; anything else only when the server marked it retryable.
    /// </summary>
    private static bool ShouldRetry(RetryMode retry, RpcException ex, out TimeSpan? retryAfter)
    {
        retryAfter = null;
        if (retry == RetryMode.None || ex.StatusCode is StatusCode.Cancelled or StatusCode.DeadlineExceeded) return false;
        switch (ex.Status.DebugException)
        {
            case HttpRequestException transport:
                return RetryPolicy.ShouldRetry(retry, transport, out retryAfter);
            case IOException io:
                return RetryPolicy.ShouldRetry(retry, new HttpRequestException(HttpRequestError.Unknown, io.Message, io), out retryAfter);
            case not null:
                return false;
        }
        var error = GrpcErrors.FromRpcException(ex);
        if (ex.StatusCode == StatusCode.Unavailable && error.Details is null)
        {
            retryAfter = error.RetryAfter;
            return retryAfter is null || retryAfter <= RetryPolicy.MaxRetryAfter;
        }
        return RetryPolicy.ShouldRetry(retry, error, out retryAfter);
    }

    /// <summary>Caller cancellation stays cancellation, an expired deadline is a timeout (as in the HTTP transports), the rest is an <see cref="A2AException"/>.</summary>
    private static Exception Translate(RpcException ex, TimeSpan timeout, CancellationToken ct)
    {
        if (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested) return new OperationCanceledException(ex.Message, ex, ct);
        if (ex.StatusCode == StatusCode.DeadlineExceeded && timeout != Timeout.InfiniteTimeSpan)
            return new TaskCanceledException($"The request timed out after {timeout.TotalSeconds:0.###} s.", new TimeoutException(null, ex));
        return GrpcErrors.FromRpcException(ex);
    }

    private async IAsyncEnumerable<StreamResponse> StreamAsync(Func<CallOptions, AsyncServerStreamingCall<Pb.StreamResponse>> start,
        A2ARequestOptions? options, string? idempotencyKey, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var call = start(CreateCallOptions(options, idempotencyKey, deadline: null, ct));
        var stream = call.ResponseStream;
        while (true)
        {
            bool moved;
            try { moved = await stream.MoveNext(ct).ConfigureAwait(false); }
            catch (RpcException ex) { throw Translate(ex, Timeout.InfiniteTimeSpan, ct); }
            if (!moved) yield break;
            // A2A #2269: an event of a type this SDK does not know (a newer protocol version) is skipped, not fatal.
            if (stream.Current.PayloadCase == Pb.StreamResponse.PayloadOneofCase.None) continue;
            yield return ProtoMapper.ToModel<StreamResponse>(stream.Current);
        }
    }

    /// <summary>Disposes the channel when the transport created it. A channel passed in by the caller stays open.</summary>
    public void Dispose() => _ownedChannel?.Dispose();
}

/// <summary>
/// Lets <see cref="A2AClientFactory"/> select gRPC interfaces. Add it with
/// <see cref="A2AGrpcClientOptionsExtensions.UseGrpc"/>. Each created transport owns its own <see cref="GrpcChannel"/>.
/// </summary>
public sealed class GrpcTransportFactory(GrpcChannelOptions? channelOptions = null) : IA2ATransportFactory
{
    /// <inheritdoc />
    public string ProtocolBinding => ProtocolBindings.Grpc;

    /// <inheritdoc />
    public bool CanCreate(AgentInterface agentInterface)
    {
        ArgumentNullException.ThrowIfNull(agentInterface);
        if (string.IsNullOrWhiteSpace(agentInterface.Url)) return false;
        try { _ = GrpcTransport.ParseAddress(agentInterface.Url); return true; }
        catch (UriFormatException) { return false; }
    }

    /// <summary>Creates a transport with its own channel. <paramref name="httpClient"/> is not used: gRPC needs its own HTTP/2 handler.</summary>
    public IA2AClient Create(AgentInterface agentInterface, HttpClient httpClient, A2AClientOptions options) =>
        new GrpcTransport(GrpcTransport.ParseAddress(agentInterface.Url), options, channelOptions);
}

/// <summary>Registers the gRPC transport on <see cref="A2AClientOptions"/>.</summary>
public static class A2AGrpcClientOptionsExtensions
{
    /// <summary>
    /// Adds <see cref="GrpcTransportFactory"/> and GRPC to <see cref="A2AClientOptions.SupportedBindings"/>, so
    /// <see cref="A2AClientFactory"/> may select a gRPC interface. The card's interface order still decides (spec §8.3.2).
    /// </summary>
    public static A2AClientOptions UseGrpc(this A2AClientOptions options, GrpcChannelOptions? channelOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.TransportFactories.Add(new GrpcTransportFactory(channelOptions));
        if (!options.SupportedBindings.Contains(ProtocolBindings.Grpc)) options.SupportedBindings.Add(ProtocolBindings.Grpc);
        return options;
    }
}

/// <summary>Creates gRPC-only clients from Agent Cards.</summary>
public static class A2AGrpcClientFactory
{
    /// <summary>
    /// Builds a client for the first interface in the card's order whose binding is GRPC and whose major version matches.
    /// Throws UnsupportedOperation if the card declares no usable gRPC interface. To let the card choose among all
    /// bindings, use <see cref="A2AClientFactory"/> with <see cref="A2AGrpcClientOptionsExtensions.UseGrpc"/>.
    /// </summary>
    public static A2AClient Create(AgentCard card, A2AClientOptions? options = null, GrpcChannelOptions? channelOptions = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        var src = options ?? new A2AClientOptions();
        var grpcOnly = new A2AClientOptions
        {
            ProtocolVersion = src.ProtocolVersion,
            Extensions = new List<string>(src.Extensions),
            Tenant = src.Tenant,
            RestSubscribeMethod = src.RestSubscribeMethod,
            ValidateCapabilities = src.ValidateCapabilities,
            MaxRetries = src.MaxRetries,
            TimeProvider = src.TimeProvider,
            OnMessageReceived = src.OnMessageReceived,
            CircuitBreaker = src.CircuitBreaker,
            SupportedBindings = new List<string>(),
        }.UseGrpc(channelOptions);
        if (A2AClientFactory.SelectInterface(card, grpcOnly) is null)
            throw new A2AException(A2AErrorCode.UnsupportedOperation,
                $"Agent Card '{card.Name}' declares no gRPC interface for v{grpcOnly.ProtocolVersion.Split('.')[0]}.x.");
        // The gRPC transport never touches the HttpClient.
        return A2AClientFactory.Create(card, NoHttp, grpcOnly);
    }

    /// <summary>
    /// Fetches the Agent Card at <paramref name="agentUrl"/> with <paramref name="httpClient"/> (default
    /// <see cref="A2ACardResolverOptions"/>), then builds a gRPC client as <see cref="Create"/> does.
    /// <paramref name="httpClient"/> is used only for the card; the gRPC channel is configured by <paramref name="channelOptions"/>.
    /// </summary>
    public static async Task<A2AClient> CreateAsync(Uri agentUrl, HttpClient httpClient, A2AClientOptions? options = null,
        GrpcChannelOptions? channelOptions = null, CancellationToken cancellationToken = default)
    {
        var resolver = new A2ACardResolver(httpClient);
        var card = await resolver.GetAgentCardAsync(agentUrl, cancellationToken).ConfigureAwait(false);
        return Create(card, options, channelOptions);
    }

    private static readonly HttpClient NoHttp = new();
}
