using System.Runtime.CompilerServices;
using A2A.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Pb = Lf.A2a.V1;

namespace A2A.Client.Grpc;

/// <summary>gRPC binding (spec §10). Wrap in <see cref="A2AClient"/> for tenant handling and capability checks.</summary>
/// <remarks>
/// <see cref="A2ARequestOptions"/> on gRPC: <see cref="A2ARequestOptions.Headers"/>, <see cref="A2ARequestOptions.Extensions"/>,
/// <see cref="A2ARequestOptions.IdempotencyKey"/> and <see cref="A2ARequestOptions.Timeout"/> (as the call deadline) apply.
/// <see cref="A2ARequestOptions.MaxRetries"/> and <see cref="A2ARequestOptions.ConfigureRequest"/> are HTTP-only and are
/// ignored; configure retries with a gRPC service config on the channel (<see cref="GrpcChannelOptions.ServiceConfig"/>).
/// </remarks>
public sealed class GrpcTransport : IA2AClient, IDisposable
{
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
    public async Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        ProtoMapper.ToModel<SendMessageResponse>(await Call(A2AProtocol.Methods.SendMessage, static (c, r, o) => c.SendMessageAsync(r, o), ProtoMapper.ToProto<Pb.SendMessageRequest>(request), Unary(options, cancellationToken, IdempotencyKey(request, options))).ConfigureAwait(false));

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        Stream(A2AProtocol.Methods.SendStreamingMessage, static (c, r, o) => c.SendStreamingMessage(r, o), ProtoMapper.ToProto<Pb.SendMessageRequest>(request), Streaming(options, cancellationToken, IdempotencyKey(request, options)), cancellationToken);

    /// <inheritdoc />
    public async Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        ProtoMapper.ToModel<AgentTask>(await Call(A2AProtocol.Methods.GetTask, static (c, r, o) => c.GetTaskAsync(r, o), ProtoMapper.ToProto<Pb.GetTaskRequest>(request), Unary(options, cancellationToken)).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        ProtoMapper.ToModel<ListTasksResponse>(await Call(A2AProtocol.Methods.ListTasks, static (c, r, o) => c.ListTasksAsync(r, o), ProtoMapper.ToProto<Pb.ListTasksRequest>(request), Unary(options, cancellationToken)).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        ProtoMapper.ToModel<AgentTask>(await Call(A2AProtocol.Methods.CancelTask, static (c, r, o) => c.CancelTaskAsync(r, o), ProtoMapper.ToProto<Pb.CancelTaskRequest>(request), Unary(options, cancellationToken)).ConfigureAwait(false));

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        Stream(A2AProtocol.Methods.SubscribeToTask, static (c, r, o) => c.SubscribeToTask(r, o), ProtoMapper.ToProto<Pb.SubscribeToTaskRequest>(request), Streaming(options, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public async Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        ProtoMapper.ToModel<TaskPushNotificationConfig>(await Call(A2AProtocol.Methods.CreateTaskPushNotificationConfig, static (c, r, o) => c.CreateTaskPushNotificationConfigAsync(r, o), ProtoMapper.ToProto<Pb.TaskPushNotificationConfig>(config), Unary(options, cancellationToken)).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        ProtoMapper.ToModel<TaskPushNotificationConfig>(await Call(A2AProtocol.Methods.GetTaskPushNotificationConfig, static (c, r, o) => c.GetTaskPushNotificationConfigAsync(r, o), ProtoMapper.ToProto<Pb.GetTaskPushNotificationConfigRequest>(request), Unary(options, cancellationToken)).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        ProtoMapper.ToModel<ListTaskPushNotificationConfigsResponse>(await Call(A2AProtocol.Methods.ListTaskPushNotificationConfigs, static (c, r, o) => c.ListTaskPushNotificationConfigsAsync(r, o), ProtoMapper.ToProto<Pb.ListTaskPushNotificationConfigsRequest>(request), Unary(options, cancellationToken)).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        await Call(A2AProtocol.Methods.DeleteTaskPushNotificationConfig, static (c, r, o) => c.DeleteTaskPushNotificationConfigAsync(r, o), ProtoMapper.ToProto<Pb.DeleteTaskPushNotificationConfigRequest>(request), Unary(options, cancellationToken)).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        ProtoMapper.ToModel<AgentCard>(await Call(A2AProtocol.Methods.GetExtendedAgentCard, static (c, r, o) => c.GetExtendedAgentCardAsync(r, o), ProtoMapper.ToProto<Pb.GetExtendedAgentCardRequest>(request), Unary(options, cancellationToken)).ConfigureAwait(false));

    /// <summary>Time limit for one unary call when <see cref="A2ARequestOptions.Timeout"/> is not set (same as the HTTP transports).</summary>
    private static readonly TimeSpan DefaultUnaryTimeout = TimeSpan.FromSeconds(100);

    private CallOptions Unary(A2ARequestOptions? call, CancellationToken ct, string? idempotencyKey = null)
    {
        var timeout = call?.Timeout ?? DefaultUnaryTimeout;
        DateTime? deadline = timeout == Timeout.InfiniteTimeSpan ? null : DateTime.UtcNow + timeout;
        return new CallOptions(Headers(call, idempotencyKey), deadline, ct);
    }

    private CallOptions Streaming(A2ARequestOptions? call, CancellationToken ct, string? idempotencyKey = null) =>
        new(Headers(call, idempotencyKey), cancellationToken: ct);

    /// <summary>
    /// SDK metadata first (<c>a2a-version</c>, <c>a2a-extensions</c>, <c>idempotency-key</c>), then
    /// <see cref="A2ARequestOptions.Headers"/>, where a key replaces the SDK value of the same name.
    /// </summary>
    private Metadata Headers(A2ARequestOptions? call, string? idempotencyKey)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [GrpcErrors.VersionKey] = _options.ProtocolVersion };
        var extensions = call?.Extensions is { Count: > 0 } perCall ? _options.Extensions.Concat(perCall).Distinct(StringComparer.Ordinal) : _options.Extensions;
        var joined = string.Join(",", extensions);
        if (joined.Length > 0) values[GrpcErrors.ExtensionsKey] = joined;
        if (!string.IsNullOrEmpty(idempotencyKey)) values[IdempotencyKeyHeader] = idempotencyKey;
        if (call is not null)
            foreach (var (key, value) in call.Headers) values[key] = value;
        var metadata = new Metadata();
        foreach (var (key, value) in values) metadata.Add(key.ToLowerInvariant(), value);
        return metadata;
    }

    private const string IdempotencyKeyHeader = "idempotency-key";

    private static string? IdempotencyKey(SendMessageRequest request, A2ARequestOptions? call) =>
        string.IsNullOrEmpty(call?.IdempotencyKey) ? request.Message?.MessageId : call.IdempotencyKey;

    /// <summary>One unary call inside its <c>a2a.client.{operation}</c> span. gRPC errors become <see cref="A2AException"/>.</summary>
    private async Task<TResponse> Call<TRequest, TResponse>(string operation,
        Func<Pb.A2AService.A2AServiceClient, TRequest, CallOptions, AsyncUnaryCall<TResponse>> invoke, TRequest request, CallOptions options)
    {
        var activity = GrpcClientTelemetry.Start(operation);
        try
        {
            var response = await invoke(_client, request, options).ResponseAsync.ConfigureAwait(false);
            GrpcClientTelemetry.Stop(activity, null);
            return response;
        }
        catch (RpcException ex)
        {
            var error = GrpcErrors.FromRpcException(ex);
            GrpcClientTelemetry.Stop(activity, error);
            throw error;
        }
        catch (Exception ex)
        {
            GrpcClientTelemetry.Stop(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// A server-streaming call, started when the stream is first read. Its <c>a2a.client.{operation}</c> span covers opening
    /// the stream: from the call to its first response, end or error.
    /// </summary>
    private async IAsyncEnumerable<StreamResponse> Stream<TRequest>(string operation,
        Func<Pb.A2AService.A2AServiceClient, TRequest, CallOptions, AsyncServerStreamingCall<Pb.StreamResponse>> open, TRequest request,
        CallOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        var activity = GrpcClientTelemetry.Start(operation);
        AsyncServerStreamingCall<Pb.StreamResponse> call;
        bool moved;
        try
        {
            call = open(_client, request, options);
        }
        catch (Exception ex)
        {
            GrpcClientTelemetry.Stop(activity, ex);
            throw;
        }
        using (call)
        {
            var stream = call.ResponseStream;
            try
            {
                moved = await stream.MoveNext(ct).ConfigureAwait(false);
            }
            catch (RpcException ex)
            {
                var error = GrpcErrors.FromRpcException(ex);
                GrpcClientTelemetry.Stop(activity, error);
                throw error;
            }
            catch (Exception ex)
            {
                GrpcClientTelemetry.Stop(activity, ex);
                throw;
            }
            GrpcClientTelemetry.Stop(activity, null);

            while (moved)
            {
                yield return ProtoMapper.ToModel<StreamResponse>(stream.Current);
                try { moved = await stream.MoveNext(ct).ConfigureAwait(false); }
                catch (RpcException ex) { throw GrpcErrors.FromRpcException(ex); }
            }
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
            ValidateCapabilities = src.ValidateCapabilities,
            MaxRetries = src.MaxRetries,
            TimeProvider = src.TimeProvider,
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
