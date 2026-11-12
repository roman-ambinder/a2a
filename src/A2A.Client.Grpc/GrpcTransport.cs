using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using A2A.Client.Resilience;
using A2A.Diagnostics;
using A2A.Grpc;
using A2A.Grpc.Extensions.Protos;
using A2A.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using A2aPb = Lf.A2a.V1;

namespace A2A.Client.Grpc;

/// <summary>gRPC binding (spec §10). Wrap in <see cref="A2AClient" /> for tenant handling and capability checks.</summary>
/// <remarks>
///     <para>
///         Per-call behavior matches the HTTP transports: <see cref="A2ARequestOptions.Headers" /> become request metadata
///         (a key
///         replaces the SDK value of the same name; keys are lowercase on the wire),
///         <see cref="A2ARequestOptions.Timeout" /> is the
///         deadline of each unary attempt (default 100 seconds; streams have none), SendMessage and SendStreamingMessage
///         send the
///         <c>idempotency-key</c> metadata (default: the message id), and unary calls are retried like the HTTP transports
///         (<see cref="A2ARequestOptions.MaxRetries" />): on errors the server marked retryable, on <c>UNAVAILABLE</c>
///         sent by the
///         server, and on transport failures; SendMessage only when the request provably never left; push config create
///         and
///         delete never.
///     </para>
///     <para>
///         Not supported: <see cref="A2ARequestOptions.ConfigureRequest" /> (there is no <see cref="HttpRequestMessage" />
///         ; a call that
///         sets it fails with <see cref="NotSupportedException" /> instead of going out without the hook; use
///         <see cref="A2ARequestOptions.Headers" /> or a gRPC interceptor), and stream resume (gRPC carries no event
///         cursor; after a
///         lost stream, call <c>SubscribeToTask</c> again).
///     </para>
///     <para>
///         Streaming calls (<see cref="SendStreamingMessageAsync" />, <see cref="SubscribeToTaskAsync" />) start when the
///         stream is first
///         enumerated: a stream that is never read sends nothing.
///     </para>
/// </remarks>
#pragma warning disable A2AEXP005 // the live exchange of A2A 1.1
public sealed class GrpcTransport : IA2AClient, IA2AExtensionOperationClient, IDisposable, ITransportBinding,
    ILiveTransport
#pragma warning restore A2AEXP005
{
    private readonly A2aPb.A2AService.A2AServiceClient _client;
    private readonly A2AExtensionService.A2AExtensionServiceClient _extensionClient;
    private readonly A2AClientOptions _options;
    private readonly GrpcChannel? _ownedChannel;

    /// <summary>Uses an existing channel. The caller owns and disposes it.</summary>
    public GrpcTransport(CallInvoker callInvoker, A2AClientOptions? options = null)
    {
        _client = new A2aPb.A2AService.A2AServiceClient(callInvoker ??
                                                        throw new ArgumentNullException(nameof(callInvoker)));
        _extensionClient = new A2AExtensionService.A2AExtensionServiceClient(callInvoker);
        _options = options ?? new A2AClientOptions();
    }

    /// <summary>Creates and owns a channel to <paramref name="address" />.</summary>
    public GrpcTransport(Uri address, A2AClientOptions? options = null, GrpcChannelOptions? channelOptions = null)
        : this(CreateChannel(address, channelOptions, out var channel).CreateCallInvoker(), options)
    {
        _ownedChannel = channel;
    }

    /// <inheritdoc />
    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var key = PrepareSend(request, options);
        var proto = ProtoMapper.ToProto<A2aPb.SendMessageRequest, SendMessageRequest>(request);
        return UnaryAsync(RetryMode.UnsentOnly, options, key, o => _client.SendMessageAsync(proto, o),
            ProtoMapper.ToModel<SendMessageResponse>, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        RejectConfigureRequest(options);
        var key = PrepareSend(request, options);
        var proto = ProtoMapper.ToProto<A2aPb.SendMessageRequest, SendMessageRequest>(request);
        return StreamAsync(o => _client.SendStreamingMessage(proto, o), options, key, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var proto = ProtoMapper.ToProto<A2aPb.GetTaskRequest, GetTaskRequest>(request);
        return UnaryAsync(RetryMode.Idempotent, options, null, o => _client.GetTaskAsync(proto, o),
            ProtoMapper.ToModel<AgentTask>, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var proto = ProtoMapper.ToProto<A2aPb.ListTasksRequest, ListTasksRequest>(request);
        return UnaryAsync(RetryMode.Idempotent, options, null, o => _client.ListTasksAsync(proto, o),
            ProtoMapper.ToModel<ListTasksResponse>, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var proto = ProtoMapper.ToProto<A2aPb.CancelTaskRequest, CancelTaskRequest>(request);
        return UnaryAsync(RetryMode.Idempotent, options, null, o => _client.CancelTaskAsync(proto, o),
            ProtoMapper.ToModel<AgentTask>, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        RejectConfigureRequest(options);
        var proto = ProtoMapper.ToProto<A2aPb.SubscribeToTaskRequest, SubscribeToTaskRequest>(request);
        return StreamAsync(o => _client.SubscribeToTask(proto, o), options, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var proto = ProtoMapper.ToProto<A2aPb.TaskPushNotificationConfig, TaskPushNotificationConfig>(config);
        return UnaryAsync(RetryMode.None, options, null, o => _client.CreateTaskPushNotificationConfigAsync(proto, o),
            ProtoMapper.ToModel<TaskPushNotificationConfig>, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(
        GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var proto = ProtoMapper
            .ToProto<A2aPb.GetTaskPushNotificationConfigRequest, GetTaskPushNotificationConfigRequest>(request);
        return UnaryAsync(RetryMode.Idempotent, options, null,
            o => _client.GetTaskPushNotificationConfigAsync(proto, o), ProtoMapper.ToModel<TaskPushNotificationConfig>,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(
        ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var proto = ProtoMapper
            .ToProto<A2aPb.ListTaskPushNotificationConfigsRequest, ListTaskPushNotificationConfigsRequest>(request);
        return UnaryAsync(RetryMode.Idempotent, options, null,
            o => _client.ListTaskPushNotificationConfigsAsync(proto, o),
            ProtoMapper.ToModel<ListTaskPushNotificationConfigsResponse>, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var proto = ProtoMapper
            .ToProto<A2aPb.DeleteTaskPushNotificationConfigRequest, DeleteTaskPushNotificationConfigRequest>(request);
        return UnaryAsync(RetryMode.None, options, null, o => _client.DeleteTaskPushNotificationConfigAsync(proto, o),
            _ => true, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var proto = ProtoMapper.ToProto<A2aPb.GetExtendedAgentCardRequest, GetExtendedAgentCardRequest>(request);
        return UnaryAsync(RetryMode.Idempotent, options, null, o => _client.GetExtendedAgentCardAsync(proto, o),
            ProtoMapper.ToModel<AgentCard>, cancellationToken);
    }

    /// <summary>
    ///     Calls a custom operation with the unary <c>a2a.extensions.v1.A2AExtensionService.InvokeExtensionOperation</c>: the
    ///     method is the
    ///     <c>operation_id</c> and the params are UTF-8 JSON in <c>payload</c>. It uses the same metadata, retry rules (only
    ///     when the
    ///     request never left) and error mapping as the other calls. An empty result payload is the empty object. The server
    ///     must map
    ///     the service (<c>MapA2AGrpcExtensions</c>).
    /// </summary>
    /// <inheritdoc cref="IA2AExtensionOperationClient.InvokeExtensionOperationAsync" />
    public Task<JsonElement> InvokeExtensionOperationAsync(string method, JsonElement parameters,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ExtensionOperationParams.RequireMethod(method);
        var withTenant = ExtensionOperationParams.WithTenant(parameters, _options.Tenant);
        var request = new ExtensionOperationRequest
            { OperationId = method, Payload = ByteString.CopyFromUtf8(withTenant.GetRawText()) };
        return UnaryAsync(RetryMode.UnsentOnly, options, null,
            o => _extensionClient.InvokeExtensionOperationAsync(request, o), ParseExtensionResult, cancellationToken);
    }

    /// <summary>Disposes the channel when the transport created it. A channel passed in by the caller stays open.</summary>
    public void Dispose()
    {
        _ownedChannel?.Dispose();
    }

    /// <summary>
    ///     A2A 1.1 SendLiveMessage over gRPC: a native two-way stream, opened here with <paramref name="first" /> written on
    ///     it.
    ///     Sends <c>a2a-version: 1.1</c>.
    /// </summary>
    [Experimental("A2AEXP005",
        UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public async Task<A2ALiveConnection> OpenLiveAsync(StreamRequest first, A2ARequestOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(first);
        var callOptions = CreateCallOptions(options, null, null, CancellationToken.None);
        Set(callOptions.Headers!, GrpcErrors.VersionKey, A2AProtocol.Version11);
        var call = _client.SendLiveMessage(callOptions);
        var connection = new GrpcLiveConnection(call);
        try
        {
            await connection.SendAsync(first, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }

    string ITransportBinding.Binding => ProtocolBindings.Grpc;

    private static GrpcChannel CreateChannel(Uri address, GrpcChannelOptions? options, out GrpcChannel channel)
    {
        return channel = GrpcChannel.ForAddress(address, options ?? new GrpcChannelOptions());
    }

    /// <summary>
    ///     Normalizes an Agent Card gRPC URL. The spec allows "host:port"; that form is read as HTTPS.
    /// </summary>
    public static Uri ParseAddress(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)) return u;
        return new Uri("https://" + url.TrimStart('/'));
    }

    private static JsonElement ParseExtensionResult(ExtensionOperationResponse response)
    {
        if (response.Payload.Length == 0) return A2AJson.EmptyObject;
        try
        {
            using var doc = JsonDocument.Parse(response.Payload.Memory);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new A2AException(A2AErrorCode.InvalidAgentResponse,
                $"Malformed extension operation result in agent response: {ex.Message}", ex);
        }
    }

    // ---- plumbing ----

    /// <summary>
    ///     The key for SendMessage: the caller's, else the message id. Fills an empty message id first (as the HTTP
    ///     transports do).
    /// </summary>
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
            throw new NotSupportedException(
                "GrpcTransport does not support A2ARequestOptions.ConfigureRequest. Use Headers (sent as gRPC metadata) or a gRPC interceptor.");
    }

    /// <summary>
    ///     Request metadata in the order of the HTTP transports: <c>a2a-version</c>, <c>a2a-extensions</c>,
    ///     <c>idempotency-key</c>,
    ///     the W3C <c>traceparent</c>/<c>tracestate</c> of the current activity, then <see cref="A2ARequestOptions.Headers" />
    ///     (replacing an SDK entry of the same name).
    /// </summary>
    private CallOptions CreateCallOptions(A2ARequestOptions? call, string? idempotencyKey, DateTime? deadline,
        CancellationToken ct)
    {
        var headers = new Metadata();
        Set(headers, GrpcErrors.VersionKey, A2AProtocol.ToMajorMinor(_options.ProtocolVersion)!); // spec §3.6: no patch number
        var extensions = call?.Extensions is { Count: > 0 } perCall
            ? _options.Extensions.Concat(perCall).Distinct(StringComparer.Ordinal)
            : _options.Extensions;
        var joined = string.Join(",", extensions);
        if (joined.Length > 0) Set(headers, GrpcErrors.ExtensionsKey, joined);
        if (!string.IsNullOrEmpty(idempotencyKey)) Set(headers, GrpcErrors.IdempotencyKeyKey, idempotencyKey);
        A2ATelemetry.InjectTraceContext(Activity.Current, (name, value) => Set(headers, name, value));
        if (call is not null)
            foreach (var (name, value) in call.Headers)
                Set(headers, name.ToLowerInvariant(), value);
        return new CallOptions(headers, deadline, ct);
    }

    private static void Set(Metadata headers, string key, string value)
    {
        for (var i = headers.Count - 1; i >= 0; i--)
            if (string.Equals(headers[i].Key, key, StringComparison.Ordinal))
                headers.RemoveAt(i);
        headers.Add(key, value);
    }

    /// <summary>
    ///     Runs one unary operation: each attempt gets the call timeout as its deadline, and failed attempts are retried under
    ///     <paramref name="retry" />, at most <see cref="A2ARequestOptions.MaxRetries" /> (else
    ///     <see cref="A2AClientOptions.MaxRetries" />) times.
    /// </summary>
    private async Task<TModel> UnaryAsync<TProto, TModel>(RetryMode retry, A2ARequestOptions? call,
        string? idempotencyKey,
        Func<CallOptions, AsyncUnaryCall<TProto>> invoke, Func<TProto, TModel> map, CancellationToken ct)
        where TProto : IMessage
    {
        RejectConfigureRequest(call);
        var timeout = call?.Timeout ?? A2ARequestOptions.DefaultTimeout;
        var maxAttempts = 1 + (call?.MaxRetries ?? _options.MaxRetries);
        for (var n = 1;; n++)
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
                    await Task.Delay(RetryPolicy.Delay(n + 1, retryAfter), _options.TimeProvider, ct)
                        .ConfigureAwait(false);
                    continue;
                }

                throw Translate(ex, timeout, ct);
            }

            return FromAgent(map, response);
        }
    }

    /// <summary>
    ///     Maps what the agent sent. A message the model cannot hold is the agent's error (InvalidAgentResponse), as the HTTP
    ///     transports report it, not the caller's (InvalidParams, which <see cref="ProtoMapper" /> uses for requests).
    /// </summary>
    private static TModel FromAgent<TProto, TModel>(Func<TProto, TModel> map, TProto response)
    {
        try
        {
            return map(response);
        }
        catch (A2AException ex) when (ex.Code == A2AErrorCode.InvalidParams)
        {
            throw new A2AException(A2AErrorCode.InvalidAgentResponse, $"The agent's response is invalid: {ex.Message}");
        }
    }

    /// <summary>
    ///     The HTTP transports' retry rules applied to a gRPC failure: a client-side transport failure (an
    ///     <see cref="HttpRequestException" /> or <see cref="IOException" /> behind the status) is judged like an
    ///     <see cref="HttpRequestException" /> (SendMessage only when it provably never reached the server); other client-side
    ///     failures, such as a response that cannot be parsed, are not transport failures; <c>UNAVAILABLE</c> from the server
    ///     is
    ///     the gRPC form of HTTP 503; anything else only when the server marked it retryable.
    /// </summary>
    private static bool ShouldRetry(RetryMode retry, RpcException ex, out TimeSpan? retryAfter)
    {
        retryAfter = null;
        if (retry == RetryMode.None || ex.StatusCode is StatusCode.Cancelled or StatusCode.DeadlineExceeded)
            return false;
        switch (ex.Status.DebugException)
        {
            case HttpRequestException transport:
                return RetryPolicy.ShouldRetry(retry, transport, out retryAfter);
            case IOException io:
                return RetryPolicy.ShouldRetry(retry,
                    new HttpRequestException(HttpRequestError.Unknown, io.Message, io), out retryAfter);
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

    /// <summary>
    ///     Caller cancellation stays cancellation, an expired deadline is a timeout (as in the HTTP transports), the rest
    ///     is an <see cref="A2AException" />.
    /// </summary>
    private static Exception Translate(RpcException ex, TimeSpan timeout, CancellationToken ct)
    {
        if (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
            return new OperationCanceledException(ex.Message, ex, ct);
        if (ex.StatusCode == StatusCode.DeadlineExceeded && timeout != Timeout.InfiniteTimeSpan)
            return new TaskCanceledException($"The request timed out after {timeout.TotalSeconds:0.###} s.",
                new TimeoutException(null, ex));
        return GrpcErrors.FromRpcException(ex);
    }

    private async IAsyncEnumerable<StreamResponse> StreamAsync(
        Func<CallOptions, AsyncServerStreamingCall<A2aPb.StreamResponse>> start,
        A2ARequestOptions? options, string? idempotencyKey, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var call = start(CreateCallOptions(options, idempotencyKey, null, ct));
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
            // A2A #2269: an event of a type this SDK does not know (a newer protocol version) is skipped, not fatal.
            if (stream.Current.PayloadCase == A2aPb.StreamResponse.PayloadOneofCase.None) continue;
            yield return FromAgent(ProtoMapper.ToModel<StreamResponse>, stream.Current);
        }
    }

#pragma warning disable A2AEXP005
    /// <summary>The gRPC form of a live exchange: one duplex call. Writes are one at a time, as gRPC requires.</summary>
    private sealed class GrpcLiveConnection(AsyncDuplexStreamingCall<A2aPb.StreamRequest, A2aPb.StreamResponse> call)
        : A2ALiveConnection
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _write = new(1, 1);
        private int _disposed;

        public override IAsyncEnumerable<StreamResponse> Events => ReadAsync(_stop.Token);

        private async IAsyncEnumerable<StreamResponse> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
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
                // A2A #2269 and 1.1 §3.2.3: an event of a kind this SDK does not know is skipped.
                if (stream.Current.PayloadCase == A2aPb.StreamResponse.PayloadOneofCase.None) continue;
                yield return FromAgent(ProtoMapper.ToModel<StreamResponse>, stream.Current);
            }
        }

        public override async Task SendAsync(StreamRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var proto = ProtoMapper.ToProto<A2aPb.StreamRequest, StreamRequest>(request);
            await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await call.RequestStream.WriteAsync(proto, cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException ex)
            {
                throw Translate(ex, Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                _write.Release();
            }
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try
            {
                await _write.WaitAsync().ConfigureAwait(false);
                try
                {
                    await call.RequestStream.CompleteAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                } // the call already ended
                finally
                {
                    _write.Release();
                }
            }
            finally
            {
                _stop.Cancel();
                call.Dispose();
                _stop.Dispose();
            }
        }
    }
#pragma warning restore A2AEXP005
}

/// <summary>
///     Lets <see cref="A2AClientFactory" /> select gRPC interfaces. Add it with
///     <see cref="A2AGrpcClientOptionsExtensions.UseGrpc" />. Transports to the same address with the same channel options
///     share one
///     <see cref="GrpcChannel" /> (one HTTP/2 connection pool), which stays open for the life of those options.
/// </summary>
public sealed class GrpcTransportFactory(GrpcChannelOptions? channelOptions = null) : IA2ATransportFactory
{
    // Channels come from a pool shared per address and channel options (GrpcChannelPool): a call leases its channel while it
    // runs, and a channel no call uses is disposed after an idle timeout or past an idle limit, so a host that calls many
    // agents does not keep a channel per agent for the life of the process.

    /// <inheritdoc />
    public string ProtocolBinding => ProtocolBindings.Grpc;

    /// <inheritdoc />
    public bool CanCreate(AgentInterface agentInterface)
    {
        ArgumentNullException.ThrowIfNull(agentInterface);
        if (string.IsNullOrWhiteSpace(agentInterface.Url)) return false;
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

    /// <summary>
    ///     Creates a transport on the shared channel of its address (see the class remarks). <paramref name="httpClient" /> is
    ///     not
    ///     used: gRPC needs its own HTTP/2 handler.
    /// </summary>
    public IA2AClient Create(AgentInterface agentInterface, HttpClient httpClient, A2AClientOptions options)
    {
        return new GrpcTransport(
            GrpcChannelPool.For(channelOptions).CallInvoker(GrpcTransport.ParseAddress(agentInterface.Url)), options);
    }
}

/// <summary>Registers the gRPC transport on <see cref="A2AClientOptions" />.</summary>
public static class A2AGrpcClientOptionsExtensions
{
    /// <summary>
    ///     Adds <see cref="GrpcTransportFactory" /> and GRPC to <see cref="A2AClientOptions.SupportedBindings" />, so
    ///     <see cref="A2AClientFactory" /> may select a gRPC interface. The card's interface order still decides (spec
    ///     §8.3.2).
    /// </summary>
    public static A2AClientOptions UseGrpc(this A2AClientOptions options, GrpcChannelOptions? channelOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.TransportFactories.Add(new GrpcTransportFactory(channelOptions));
        if (!options.SupportedBindings.Contains(ProtocolBindings.Grpc))
            options.SupportedBindings.Add(ProtocolBindings.Grpc);
        return options;
    }
}

/// <summary>Creates gRPC-only clients from Agent Cards.</summary>
public static class A2AGrpcClientFactory
{
    private static readonly HttpClient NoHttp = new();

    /// <summary>
    ///     Builds a client for the first interface in the card's order whose binding is GRPC and whose major version matches.
    ///     Throws UnsupportedOperation if the card declares no usable gRPC interface. To let the card choose among all
    ///     bindings, use <see cref="A2AClientFactory" /> with <see cref="A2AGrpcClientOptionsExtensions.UseGrpc" />.
    /// </summary>
    public static A2AClient Create(AgentCard card, A2AClientOptions? options = null,
        GrpcChannelOptions? channelOptions = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        var grpcOnly = GrpcOnly(options, channelOptions);
        if (A2AClientFactory.SelectInterface(card, grpcOnly) is null)
            throw new A2AException(A2AErrorCode.UnsupportedOperation,
                $"Agent Card '{card.Name}' declares no gRPC interface for v{grpcOnly.ProtocolVersion.Split('.')[0]}.x.");
        // The gRPC transport never touches the HttpClient.
        return A2AClientFactory.Create(card, NoHttp, grpcOnly);
    }

    private static A2AClientOptions GrpcOnly(A2AClientOptions? options, GrpcChannelOptions? channelOptions)
    {
        var grpcOnly = (options ?? new A2AClientOptions()).Clone();
        grpcOnly.SupportedBindings = new List<string>();
        grpcOnly.TransportFactories = new List<IA2ATransportFactory>();
        return grpcOnly.UseGrpc(channelOptions);
    }

    /// <summary>
    ///     Fetches the Agent Card at <paramref name="agentUrl" /> with <paramref name="httpClient" /> (default
    ///     <see cref="A2ACardResolverOptions" />), then builds a gRPC client as <see cref="Create" /> does.
    ///     <paramref name="httpClient" /> is used only for the card; the gRPC channel is configured by
    ///     <paramref name="channelOptions" />.
    /// </summary>
    public static async Task<A2AClient> CreateAsync(Uri agentUrl, HttpClient httpClient,
        A2AClientOptions? options = null,
        GrpcChannelOptions? channelOptions = null, CancellationToken cancellationToken = default)
    {
        var resolver = new A2ACardResolver(httpClient);
        var card = await resolver.GetAgentCardAsync(agentUrl, cancellationToken).ConfigureAwait(false);
        // The endpoint check needs the card source, which Create(card) does not have.
        await A2AClientFactory
            .EnsureEndpointsAllowedAsync(card, agentUrl, GrpcOnly(options, channelOptions), cancellationToken)
            .ConfigureAwait(false);
        return Create(card, options, channelOptions);
    }
}