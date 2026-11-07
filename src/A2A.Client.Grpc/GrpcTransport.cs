using System.Runtime.CompilerServices;
using A2A.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Pb = Lf.A2a.V1;

namespace A2A.Client.Grpc;

/// <summary>gRPC binding (spec §10). Wrap in <see cref="A2AClient"/> for tenant handling and capability checks.</summary>
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

    public async Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default) =>
        ProtoMapper.ToModel<SendMessageResponse>(await Call(_client.SendMessageAsync(ProtoMapper.ToProto<Pb.SendMessageRequest>(request), Options(cancellationToken)).ResponseAsync).ConfigureAwait(false));

    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default) =>
        Stream(_client.SendStreamingMessage(ProtoMapper.ToProto<Pb.SendMessageRequest>(request), Options(cancellationToken)), cancellationToken);

    public async Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken cancellationToken = default) =>
        ProtoMapper.ToModel<AgentTask>(await Call(_client.GetTaskAsync(ProtoMapper.ToProto<Pb.GetTaskRequest>(request), Options(cancellationToken)).ResponseAsync).ConfigureAwait(false));

    public async Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken cancellationToken = default) =>
        ProtoMapper.ToModel<ListTasksResponse>(await Call(_client.ListTasksAsync(ProtoMapper.ToProto<Pb.ListTasksRequest>(request), Options(cancellationToken)).ResponseAsync).ConfigureAwait(false));

    public async Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken cancellationToken = default) =>
        ProtoMapper.ToModel<AgentTask>(await Call(_client.CancelTaskAsync(ProtoMapper.ToProto<Pb.CancelTaskRequest>(request), Options(cancellationToken)).ResponseAsync).ConfigureAwait(false));

    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken cancellationToken = default) =>
        Stream(_client.SubscribeToTask(ProtoMapper.ToProto<Pb.SubscribeToTaskRequest>(request), Options(cancellationToken)), cancellationToken);

    public async Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, CancellationToken cancellationToken = default) =>
        ProtoMapper.ToModel<TaskPushNotificationConfig>(await Call(_client.CreateTaskPushNotificationConfigAsync(ProtoMapper.ToProto<Pb.TaskPushNotificationConfig>(config), Options(cancellationToken)).ResponseAsync).ConfigureAwait(false));

    public async Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default) =>
        ProtoMapper.ToModel<TaskPushNotificationConfig>(await Call(_client.GetTaskPushNotificationConfigAsync(ProtoMapper.ToProto<Pb.GetTaskPushNotificationConfigRequest>(request), Options(cancellationToken)).ResponseAsync).ConfigureAwait(false));

    public async Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, CancellationToken cancellationToken = default) =>
        ProtoMapper.ToModel<ListTaskPushNotificationConfigsResponse>(await Call(_client.ListTaskPushNotificationConfigsAsync(ProtoMapper.ToProto<Pb.ListTaskPushNotificationConfigsRequest>(request), Options(cancellationToken)).ResponseAsync).ConfigureAwait(false));

    public async Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default) =>
        await Call(_client.DeleteTaskPushNotificationConfigAsync(ProtoMapper.ToProto<Pb.DeleteTaskPushNotificationConfigRequest>(request), Options(cancellationToken)).ResponseAsync).ConfigureAwait(false);

    public async Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, CancellationToken cancellationToken = default) =>
        ProtoMapper.ToModel<AgentCard>(await Call(_client.GetExtendedAgentCardAsync(ProtoMapper.ToProto<Pb.GetExtendedAgentCardRequest>(request), Options(cancellationToken)).ResponseAsync).ConfigureAwait(false));

    private CallOptions Options(CancellationToken ct)
    {
        var headers = new Metadata { { GrpcErrors.VersionKey, _options.ProtocolVersion } };
        if (_options.Extensions.Count > 0) headers.Add(GrpcErrors.ExtensionsKey, string.Join(",", _options.Extensions));
        return new CallOptions(headers, cancellationToken: ct);
    }

    private static async Task<T> Call<T>(Task<T> call)
    {
        try { return await call.ConfigureAwait(false); }
        catch (RpcException ex) { throw GrpcErrors.FromRpcException(ex); }
    }

    private static async IAsyncEnumerable<StreamResponse> Stream(AsyncServerStreamingCall<Pb.StreamResponse> call, [EnumeratorCancellation] CancellationToken ct)
    {
        using (call)
        {
            var stream = call.ResponseStream;
            while (true)
            {
                bool moved;
                try { moved = await stream.MoveNext(ct).ConfigureAwait(false); }
                catch (RpcException ex) { throw GrpcErrors.FromRpcException(ex); }
                if (!moved) yield break;
                yield return ProtoMapper.ToModel<StreamResponse>(stream.Current);
            }
        }
    }

    public void Dispose() => _ownedChannel?.Dispose();
}

/// <summary>Creates gRPC clients from Agent Cards.</summary>
public static class A2AGrpcClientFactory
{
    /// <summary>
    /// Builds a client for the first interface in the card's order whose binding is GRPC and whose major version matches.
    /// Sets the tenant from that interface. Throws UnsupportedOperation if the card declares no usable gRPC interface.
    /// </summary>
    public static A2AClient Create(AgentCard card, A2AClientOptions? options = null, GrpcChannelOptions? channelOptions = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        var src = options ?? new A2AClientOptions();
        options = new A2AClientOptions
        {
            ProtocolVersion = src.ProtocolVersion,
            Extensions = new List<string>(src.Extensions),
            Tenant = src.Tenant,
            SupportedBindings = new List<string>(src.SupportedBindings),
            ValidateCapabilities = src.ValidateCapabilities,
        };
        var major = options.ProtocolVersion.Split('.')[0];
        var itf = card.SupportedInterfaces?.FirstOrDefault(i =>
                      i.ProtocolBinding == ProtocolBindings.Grpc && (i.ProtocolVersion ?? "").Split('.')[0] == major)
                  ?? throw new A2AException(A2AErrorCode.UnsupportedOperation, $"Agent Card '{card.Name}' declares no gRPC interface for v{major}.x.");
        if (!string.IsNullOrEmpty(itf.Tenant)) options.Tenant = itf.Tenant;
        return new A2AClient(new GrpcTransport(GrpcTransport.ParseAddress(itf.Url), options, channelOptions), options, card);
    }
}
