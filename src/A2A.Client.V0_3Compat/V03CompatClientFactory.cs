using A2A.V0_3Compat;

namespace A2A.Client.V0_3Compat;

/// <summary>
///     Creates clients that can talk to A2A protocol v0.3 agents. Calling this factory is the opt-in:
///     <see cref="A2AClientFactory" /> is unchanged. The result is a normal <see cref="A2AClient" />.
/// </summary>
/// <remarks>
///     A card that offers a v1.x interface always takes the normal v1.0 path: it is never downgraded. Otherwise the first
///     v0.3
///     <c>JSONRPC</c> or <c>HTTP+JSON</c> interface in card order is used. gRPC is not supported on v0.3.
/// </remarks>
public static class V03CompatClientFactory
{
    /// <summary>
    ///     Sets the logger for the v0.3 warnings (spec A.2): a v0.3 agent the client talks to, once per URL, and each field
    ///     the conversion drops, once. The first call wins, and a v0.3 server surface in the same process may have set it
    ///     already. Without it nothing is logged.
    /// </summary>
    public static void UseLogger(Microsoft.Extensions.Logging.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        V03LossReporter.UseLogger(logger);
    }

    /// <summary>
    ///     Resolves the card at <paramref name="agentUrl" /> and creates a client for it. A v0.3 card (top-level
    ///     <c>protocolVersion</c> <c>0.3.x</c>, no <c>supportedInterfaces</c>) is converted to the v1.0 model first, and reads
    ///     as unsigned. A v0.2 card is converted the same way, with only its JSON-RPC interfaces. When the well-known card
    ///     path answers 404, the A2A 0.1 to 0.2.x path <c>/.well-known/agent.json</c> is tried, unless
    ///     <paramref name="agentUrl" /> names a <c>.json</c> document. <paramref name="resolverOptions" /> is copied, never
    ///     changed.
    /// </summary>
    public static async Task<A2AClient> CreateAsync(Uri agentUrl, HttpClient httpClient,
        A2AClientOptions? options = null,
        A2ACardResolverOptions? resolverOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentUrl);
        ArgumentNullException.ThrowIfNull(httpClient);
        RequireSameVersion();
        var resolverCopy = resolverOptions?.Clone() ?? new A2ACardResolverOptions();
        resolverCopy.CardNormalizer = V03CardCodec.NormalizeForResolver;
        var resolver = new A2ACardResolver(httpClient, resolverCopy);
        AgentCard card;
        try
        {
            card = await resolver.GetAgentCardAsync(agentUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (A2AException ex) when (ex.HttpStatus == 404 && LegacyCardUri(agentUrl) is { } legacy)
        {
            // An A2A 0.1 to 0.2.x agent publishes its card at /.well-known/agent.json (0.3 moved it to agent-card.json).
            card = await resolver.GetAgentCardAsync(legacy, cancellationToken).ConfigureAwait(false);
        }

        await EnsureEndpointsAllowedAsync(card, agentUrl, options ?? new A2AClientOptions(), cancellationToken)
            .ConfigureAwait(false);
        return Create(card, httpClient, options);
    }

    /// <summary>
    ///     The legacy card URL of <paramref name="agentUrl" />, or null when the caller named a card document itself (a URL
    ///     that ends in <c>.json</c>): then the caller's URL is the only one tried.
    /// </summary>
    private static Uri? LegacyCardUri(Uri agentUrl)
    {
        return agentUrl.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? null
            : new Uri(agentUrl, A2AProtocol.LegacyAgentCardPath);
    }

    /// <summary>
    ///     Creates a client for <paramref name="card" />. If the card offers a v1.x interface the normal v1.0 path is used.
    ///     Otherwise the first v0.3 JSON-RPC or HTTP+JSON interface (marked <c>0.3</c> or <c>0.3.x</c>) in card order is used.
    ///     Throws <see cref="A2AErrorCode.UnsupportedOperation" /> when neither exists.
    /// </summary>
    public static A2AClient Create(AgentCard card, HttpClient httpClient, A2AClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(httpClient);
        RequireSameVersion();
        options ??= new A2AClientOptions();

        // A card that offers v1.0 is never downgraded.
        if (A2AClientFactory.SelectInterface(card, options) is not null)
            return A2AClientFactory.Create(card, httpClient, options);
        // RequireExactProtocolVersion forbids falling back to an older version (spec 3.6.3). A card whose v1.x interfaces
        // the exact rule refused is not downgraded to v0.3 either: the v1 factory refuses it, naming those interfaces.
        if (options.RequireExactProtocolVersion && A2AClientFactory.SelectableInterfaces(card, options).Any())
            return A2AClientFactory.Create(card, httpClient, options);

        var selected = SelectV03Interface(card, options)
                       ?? throw new A2AException(A2AErrorCode.UnsupportedOperation,
                           $"No supported interface in Agent Card '{card.Name}'. The client supports v1.x and v0.3 over {string.Join(", ", options.SupportedBindings.Where(b => b is ProtocolBindings.JsonRpc or ProtocolBindings.HttpJson))}.");

        V03LossReporter.LegacyAgent(card.Name, selected.Url);
        var v03Options = V03ClientSupport.CopyForV03(options, selected.Tenant);
        EnsureAllowedNoDns(card, v03Options);
        IA2ATransport transport = selected.ProtocolBinding switch
        {
            ProtocolBindings.JsonRpc => new V03JsonRpcTransport(httpClient, new Uri(selected.Url, UriKind.Absolute),
                v03Options),
            ProtocolBindings.HttpJson => new V03HttpJsonTransport(httpClient, new Uri(selected.Url, UriKind.Absolute),
                v03Options),
            _ => V03FactoryFor(options, selected)!.Create(selected, httpClient, v03Options)
        };
        return new A2AClient(transport, v03Options, card);
    }

    /// <summary>
    ///     The endpoint check for a card that may be v1.x or v0.3: every interface the client could select under either
    ///     version, with the card source.
    /// </summary>
    private static ValueTask EnsureEndpointsAllowedAsync(AgentCard card, Uri cardSource, A2AClientOptions options,
        CancellationToken ct)
    {
        return A2AClientFactory.EnsureAllowedAsync(Candidates(card, options), cardSource, options, true, ct);
    }

    private static void EnsureAllowedNoDns(AgentCard card, A2AClientOptions options)
    {
        A2AClientFactory.EnsureAllowedAsync(Candidates(card, options), null, options, false, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
    }

    private static List<AgentInterface> Candidates(AgentCard card, A2AClientOptions options)
    {
        var v1 = A2AClientFactory.SelectableInterfaces(card, options).ToHashSet();
        return (card.SupportedInterfaces ?? []).Where(i => v1.Contains(i)
                                                           || (V03Compat.IsV03Version(i.ProtocolVersion) &&
                                                               options.SupportedBindings.Contains(i.ProtocolBinding)))
            .ToList();
    }

    private static AgentInterface? SelectV03Interface(AgentCard card, A2AClientOptions options)
    {
        foreach (var itf in card.SupportedInterfaces ?? [])
        {
            if (!V03Compat.IsV03Version(itf.ProtocolVersion)) continue;
            if (!options.SupportedBindings.Contains(itf.ProtocolBinding)) continue;
            // Other bindings (v0.3 gRPC) need a v0.3 transport factory, such as UseGrpcV03() adds.
            if (itf.ProtocolBinding is not (ProtocolBindings.JsonRpc or ProtocolBindings.HttpJson))
            {
                if (V03FactoryFor(options, itf) is not null) return itf;
                continue;
            }

            if (!Uri.TryCreate(itf.Url, UriKind.Absolute, out _)) continue;
            return itf;
        }

        return null;
    }

    /// <summary>The last v0.3 transport factory that can build a transport for <paramref name="itf" />, or null.</summary>
    private static IV03TransportFactory? V03FactoryFor(A2AClientOptions options, AgentInterface itf)
    {
        return options.TransportFactories.OfType<IV03TransportFactory>().LastOrDefault(f =>
            string.Equals(f.ProtocolBinding, itf.ProtocolBinding, StringComparison.OrdinalIgnoreCase) && f.CanCreate(itf));
    }

    private static void RequireSameVersion()
    {
        V03Compat.RequireSameVersion(typeof(V03CompatClientFactory).Assembly, typeof(A2AClient).Assembly,
            typeof(V03Compat).Assembly, typeof(AgentCard).Assembly);
    }
}

/// <summary>
///     A transport factory for a v0.3 binding other than JSON-RPC and HTTP+JSON (v0.3 gRPC, A2A.Client.Grpc.V0_3Compat).
///     <see cref="V03CompatClientFactory" /> uses it for the v0.3 interfaces it can create. The v1.0 path never does: it
///     selects v1.x interfaces only.
/// </summary>
internal interface IV03TransportFactory : IA2ATransportFactory;
