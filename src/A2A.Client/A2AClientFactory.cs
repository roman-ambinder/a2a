using A2A.Client.Transports;

namespace A2A.Client;

/// <summary>Creates clients from Agent Cards, selecting the interface per spec §8.3.2.</summary>
public static class A2AClientFactory
{
    /// <summary>
    /// Picks the first interface in the card's order whose binding this client supports and whose major version matches,
    /// sets the tenant from that interface, and builds the transport.
    /// </summary>
    public static A2AClient Create(AgentCard card, HttpClient httpClient, A2AClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        options = Copy(options ?? new A2AClientOptions());
        var selected = SelectInterface(card, options)
            ?? throw new A2AException(A2AErrorCode.UnsupportedOperation,
                $"No supported interface in Agent Card '{card.Name}'. Client supports: {string.Join(", ", options.SupportedBindings)} (v{Major(options.ProtocolVersion)}.x).");

        if (!string.IsNullOrEmpty(selected.Tenant)) options.Tenant = selected.Tenant;
        options.ProtocolVersion = selected.ProtocolVersion is { Length: > 0 } v && Major(v) == Major(options.ProtocolVersion) ? v : options.ProtocolVersion;

        var url = new Uri(selected.Url, UriKind.Absolute);
        IA2AClient transport = selected.ProtocolBinding switch
        {
            ProtocolBindings.JsonRpc => new JsonRpcTransport(httpClient, url, options),
            ProtocolBindings.HttpJson => new HttpJsonTransport(httpClient, url, options),
            _ => throw new A2AException(A2AErrorCode.UnsupportedOperation, $"Binding '{selected.ProtocolBinding}' is not implemented."),
        };
        return new A2AClient(transport, options, card);
    }

    /// <summary>Resolves the card at <paramref name="agentUrl"/> and creates a client for it.</summary>
    public static async Task<A2AClient> CreateAsync(Uri agentUrl, HttpClient httpClient, A2AClientOptions? options = null,
        A2ACardResolverOptions? resolverOptions = null, CancellationToken cancellationToken = default)
    {
        var resolver = new A2ACardResolver(httpClient, resolverOptions);
        var card = await resolver.GetAgentCardAsync(agentUrl, cancellationToken).ConfigureAwait(false);
        return Create(card, httpClient, options);
    }

    public static AgentInterface? SelectInterface(AgentCard card, A2AClientOptions options)
    {
        var major = Major(options.ProtocolVersion);
        foreach (var itf in card.SupportedInterfaces ?? [])
        {
            if (!options.SupportedBindings.Contains(itf.ProtocolBinding)) continue;
            if (itf.ProtocolBinding is not (ProtocolBindings.JsonRpc or ProtocolBindings.HttpJson)) continue;
            if (Major(itf.ProtocolVersion) != major) continue;
            if (!Uri.TryCreate(itf.Url, UriKind.Absolute, out _)) continue;
            return itf;
        }
        return null;
    }

    private static string Major(string? version) =>
        string.IsNullOrEmpty(version) ? "0" : version.Split('.')[0];

    private static A2AClientOptions Copy(A2AClientOptions o) => new()
    {
        ProtocolVersion = o.ProtocolVersion,
        Extensions = new List<string>(o.Extensions),
        Tenant = o.Tenant,
        SupportedBindings = new List<string>(o.SupportedBindings),
        RestSubscribeMethod = o.RestSubscribeMethod,
        ValidateCapabilities = o.ValidateCapabilities,
        TimeProvider = o.TimeProvider,
    };
}
