using System.Diagnostics.Metrics;
using System.Net;
using A2A.Client.Credentials;
using A2A.Client.Transports;

namespace A2A.Client;

/// <summary>Creates clients from Agent Cards, selecting the interface per spec §8.3.2.</summary>
public static class A2AClientFactory
{
    private const long MaxResponseBytes = 16 * 1024 * 1024;

    // Spec: metrics live under the "A2A" meter (same as A2ACardResolver).
    private static readonly Meter Meter = new("A2A");

    private static readonly Counter<long> EndpointRefusedCounter =
        Meter.CreateCounter<long>("a2a.client.endpoint.refused",
            description: "Agent Card endpoints the client's endpoint policy refused.");

    /// <summary>Longest wait for one DNS lookup of the endpoint check.</summary>
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Picks the first interface in the card's order whose binding this client supports and whose major version matches,
    ///     sets the tenant from that interface, and builds the transport.
    /// </summary>
    /// <remarks>
    ///     The card's endpoints are first judged by <see cref="A2AClientOptions.EndpointPolicy" /> with no card source and
    ///     without DNS
    ///     (IP literals and <c>localhost</c> only). Use
    ///     <see cref="CreateAsync(Uri, HttpClient, A2AClientOptions?, A2ACardResolverOptions?, CancellationToken)" />
    ///     or <see cref="EnsureEndpointsAllowedAsync" /> for the full check.
    /// </remarks>
    public static A2AClient Create(AgentCard card, HttpClient httpClient, A2AClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        options = (options ?? new A2AClientOptions()).Clone();
        EnsureAllowedNoDns(card.SupportedInterfaces, options);
        return Build(card, httpClient, options);
    }

    private static A2AClient Build(AgentCard card, HttpClient httpClient, A2AClientOptions options)
    {
        var selected = SelectInterface(card, options)
                       ?? throw NoInterface(card, options);

        if (!string.IsNullOrEmpty(selected.Tenant)) options.Tenant = selected.Tenant;
        // Major.Minor only: a card that lists "1.0.2" is called with A2A-Version 1.0 (spec §3.6).
        options.ProtocolVersion = A2AProtocol.ToMajorMinor(
            selected.ProtocolVersion is { Length: > 0 } v && Major(v) == Major(options.ProtocolVersion)
                ? v
                : options.ProtocolVersion)!;

        return new A2AClient(CreateTransport(selected, httpClient, options), options, card);
    }

    private static IA2AClient CreateTransport(AgentInterface selected, HttpClient httpClient,
        A2AClientOptions options)
    {
        return FactoryFor(options, selected.ProtocolBinding) is { } factory
            ? factory.Create(selected, httpClient, options)
            : selected.ProtocolBinding switch
            {
                ProtocolBindings.JsonRpc => new JsonRpcTransport(httpClient, new Uri(selected.Url, UriKind.Absolute),
                    options),
                ProtocolBindings.HttpJson => new HttpJsonTransport(httpClient, new Uri(selected.Url, UriKind.Absolute),
                    options),
                _ => throw new A2AException(A2AErrorCode.UnsupportedOperation,
                    $"Binding '{selected.ProtocolBinding}' is not implemented.")
            };
    }

    /// <summary>
    ///     Creates a client for an endpoint you already know, without fetching the Agent Card: for example an agent in the
    ///     same process, or one whose address comes from your own configuration.
    /// </summary>
    /// <param name="endpoint">
    ///     The A2A endpoint itself, not the card's URL: for example <c>https://agent.example.com/a2a</c> for an agent mapped
    ///     with <c>MapA2A("/a2a")</c>. An absolute <c>http</c> or <c>https</c> URL.
    /// </param>
    /// <param name="httpClient">The HTTP client. It carries any credentials the agent needs.</param>
    /// <param name="protocolBinding">
    ///     The binding the endpoint speaks: <see cref="ProtocolBindings.JsonRpc" /> (default) or
    ///     <see cref="ProtocolBindings.HttpJson" />, or another binding with a factory in
    ///     <see cref="A2AClientOptions.TransportFactories" /> (such as gRPC after <c>UseGrpc()</c>).
    /// </param>
    /// <param name="options">Client options. Copied.</param>
    /// <remarks>
    ///     The client has no <see cref="A2AClient.AgentCard" />, so it does not check the agent's capabilities before a call
    ///     (the agent still refuses what it does not support), and no card signature is verified. Use
    ///     <see cref="CreateAsync(Uri, HttpClient, A2AClientOptions?, A2ACardResolverOptions?, CancellationToken)" /> for an
    ///     agent you discover by URL. The endpoint is your choice, so <see cref="A2AClientOptions.EndpointPolicy" />, which
    ///     judges the endpoints a card names, does not apply to it.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="endpoint" /> is not an absolute http or https URL.</exception>
    /// <exception cref="A2AException">UnsupportedOperation: no transport for <paramref name="protocolBinding" />.</exception>
    public static A2AClient CreateForEndpoint(Uri endpoint, HttpClient httpClient,
        string protocolBinding = ProtocolBindings.JsonRpc, A2AClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrEmpty(protocolBinding);
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("The endpoint must be an absolute http or https URL.", nameof(endpoint));
        options = (options ?? new A2AClientOptions()).Clone();
        var selected = new AgentInterface
        {
            Url = endpoint.AbsoluteUri, ProtocolBinding = protocolBinding, ProtocolVersion = options.ProtocolVersion
        };
        return new A2AClient(CreateTransport(selected, httpClient, options), options);
    }

#if !NETSTANDARD2_0
    /// <summary>
    /// The primary handler of clients made without one: no cookies, no redirects, and connections recycled every 2 minutes so
    /// DNS changes are seen. Shared by all of them and never disposed.
    /// </summary>
    private static readonly Lazy<SocketsHttpHandler> DefaultPrimary = new(() => new SocketsHttpHandler
    {
        UseCookies = false, AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    });

    /// <summary>
    /// Puts the shared handler under a client without handing it over: requests go through an invoker that does not own it, so
    /// disposing the client leaves it open for the others.
    /// </summary>
    private sealed class SharedHandler(HttpMessageHandler shared) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(shared, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _invoker.SendAsync(request, cancellationToken);

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _invoker.Send(request, cancellationToken);
    }
#endif

    /// <summary>Resolves the card at <paramref name="agentUrl" /> and creates a client for it.</summary>
    public static async Task<A2AClient> CreateAsync(Uri agentUrl, HttpClient httpClient,
        A2AClientOptions? options = null,
        A2ACardResolverOptions? resolverOptions = null, CancellationToken cancellationToken = default)
    {
        var resolver = new A2ACardResolver(httpClient, resolverOptions);
        var card = await resolver.GetAgentCardAsync(agentUrl, cancellationToken).ConfigureAwait(false);
        var copy = (options ?? new A2AClientOptions()).Clone();
        await EnsureEndpointsAllowedAsync(card, agentUrl, copy, cancellationToken).ConfigureAwait(false);
        return Build(card, httpClient, copy);
    }

    /// <summary>
    ///     Resolves the card at <paramref name="agentUrl" /> and creates a client that applies the credentials the card asks
    ///     for.
    ///     Credentials go only to the card's origin, and never follow a redirect. The card fetch carries no credentials.
    /// </summary>
    /// <param name="agentUrl">The agent's URL.</param>
    /// <param name="credentialProviders">Providers, in order of preference. Must not be empty.</param>
    /// <param name="handler">
    ///     The primary handler. Defaults to a <see cref="SocketsHttpHandler" /> that does not follow
    ///     redirects or keep cookies. Must not follow redirects.
    /// </param>
    /// <param name="options">Client options.</param>
    /// <param name="resolverOptions">Card resolver options.</param>
    /// <param name="cancellationToken">Cancels the card fetch.</param>
    public static async Task<A2AClient> CreateAsync(Uri agentUrl,
        IEnumerable<IA2ACredentialProvider> credentialProviders,
        HttpMessageHandler? handler = null, A2AClientOptions? options = null,
        A2ACardResolverOptions? resolverOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentUrl);
        ArgumentNullException.ThrowIfNull(credentialProviders);
        var providers = credentialProviders.ToList();
        if (providers.Count == 0)
            throw new ArgumentException("At least one credential provider is required.", nameof(credentialProviders));
#if NETSTANDARD2_0
        // No SocketsHttpHandler on netstandard2.0.
        var primary = handler ?? new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };
#else
        // One connection pool for every client made without a handler, not one per call: a pool per client was never
        // disposed (A2AClient owns no resources) and kept its sockets.
        var primary = handler ?? new SharedHandler(DefaultPrimary.Value);
#endif
        A2ACredentialHandler.EnsureNoAutoRedirect(primary);

        using var cardClient = new HttpClient(primary, false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaxResponseBytes
        };
        var resolved = await new A2ACardResolver(cardClient, resolverOptions).ResolveAsync(agentUrl, cancellationToken)
            .ConfigureAwait(false);

        var copy = (options ?? new A2AClientOptions()).Clone();
        await EnsureEndpointsAllowedAsync(resolved.Card, resolved.Source, copy, cancellationToken)
            .ConfigureAwait(false);
        var selected = SelectInterface(resolved.Card, copy) ?? throw NoInterface(resolved.Card, copy);
        var binding = A2ACredentialBinding.Create(resolved, selected, providers, resolverOptions?.RequireHttps ?? true);
        var client = new HttpClient(new A2ACredentialHandler(binding, primary))
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaxResponseBytes
        };
        return Build(resolved.Card, client, copy);
    }

    /// <summary>
    ///     Checks every interface of <paramref name="card" /> that this client could select (its binding is in
    ///     <see cref="A2AClientOptions.SupportedBindings" /> and its major version matches) against
    ///     <see cref="A2AClientOptions.EndpointPolicy" />, before any client is built. A card chooses the URL the client
    ///     calls, so a
    ///     card from the internet must not reach internal addresses (SSRF). Throws
    ///     <see cref="A2AErrorCode.InvalidAgentResponse" />
    ///     naming only the host of the refused URL, and counts <c>a2a.client.endpoint.refused</c>.
    /// </summary>
    /// <remarks>
    ///     With no policy or <see cref="AgentEndpointPolicies.AllowAll" /> it returns at once, without DNS. <c>localhost</c>
    ///     and
    ///     <c>*.localhost</c> count as loopback and IP literals are parsed, both without DNS. Other hosts are resolved on .NET
    ///     10
    ///     (5 second limit, plus <paramref name="cancellationToken" />); a failed lookup is an empty address list, not an
    ///     error.
    ///     On netstandard2.0 nothing is resolved. This is a pre-flight check: the host can resolve elsewhere when the client
    ///     connects, so full
    ///     DNS-rebinding protection needs an <see cref="HttpClient" /> built on <c>SafeHttp.CreateHandler</c>.
    /// </remarks>
    /// <param name="card">The card to check.</param>
    /// <param name="cardSource">Where the card came from, or null when the caller supplied it.</param>
    /// <param name="options">The client options; <see cref="A2AClientOptions.EndpointPolicy" /> is read from them.</param>
    /// <param name="cancellationToken">Cancels the DNS lookups.</param>
    public static ValueTask EnsureEndpointsAllowedAsync(AgentCard card, Uri? cardSource, A2AClientOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(options);
        return EnsureAllowedAsync(SelectableInterfaces(card, options), cardSource, options, true, cancellationToken);
    }

    /// <summary>Interfaces of <paramref name="card" /> whose binding is supported and whose major version matches.</summary>
    internal static IEnumerable<AgentInterface> SelectableInterfaces(AgentCard card, A2AClientOptions options)
    {
        var major = Major(options.ProtocolVersion);
        return (card.SupportedInterfaces ?? []).Where(i =>
            options.SupportedBindings.Contains(i.ProtocolBinding) && Major(i.ProtocolVersion) == major);
    }

    private static void EnsureAllowedNoDns(IEnumerable<AgentInterface>? interfaces, A2AClientOptions options)
    {
        if (options.EndpointPolicy is null ||
            ReferenceEquals(options.EndpointPolicy, AgentEndpointPolicies.AllowAll)) return;
        var major = Major(options.ProtocolVersion);
        var selectable = (interfaces ?? []).Where(i =>
            options.SupportedBindings.Contains(i.ProtocolBinding) && Major(i.ProtocolVersion) == major);
        var task = EnsureAllowedAsync(selectable, null, options, false, CancellationToken.None);
        // Without DNS the check completes synchronously.
        task.AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Runs the endpoint policy over <paramref name="interfaces" />. Also used by the v0.3 compat factory, which
    ///     selects its own interfaces.
    /// </summary>
    internal static async ValueTask EnsureAllowedAsync(IEnumerable<AgentInterface> interfaces, Uri? cardSource,
        A2AClientOptions options,
        bool allowDns, CancellationToken cancellationToken)
    {
        var policy = options.EndpointPolicy;
        if (policy is null || ReferenceEquals(policy, AgentEndpointPolicies.AllowAll)) return;

        IReadOnlyList<IPAddress>? cardAddresses = null;
        foreach (var itf in interfaces)
        {
            var endpoint = ParseEndpoint(itf);
            // A custom binding (a transport factory) may use a URL that has no host, such as a pipe or a custom scheme. The SDK
            // does not know what it means, so it does not judge it. For the built-in bindings a missing host is a refusal.
            if ((endpoint is null || endpoint.Host.Length == 0) && !IsBuiltInBinding(itf.ProtocolBinding)) continue;
            endpoint ??= NoEndpoint;
            var endpointAddresses = await ResolveAsync(endpoint, allowDns, cancellationToken).ConfigureAwait(false);
            cardAddresses ??= cardSource is null
                ? []
                : await ResolveAsync(cardSource, allowDns, cancellationToken).ConfigureAwait(false);
            if (policy(new AgentEndpointCheck(endpoint, itf.ProtocolBinding ?? string.Empty, cardSource,
                    endpointAddresses, cardAddresses))) continue;

            EndpointRefusedCounter.Add(1, new KeyValuePair<string, object?>("binding", itf.ProtocolBinding));
            // Name the host only: never text from the card.
            var host = endpoint.IsAbsoluteUri && endpoint.Host.Length > 0
                ? $"host '{endpoint.Host}'"
                : "an endpoint without a host";
            throw new A2AException(A2AErrorCode.InvalidAgentResponse,
                $"The client's endpoint policy refused the Agent Card endpoint: {host}.");
        }
    }

    private static bool IsBuiltInBinding(string? binding)
    {
        return binding is ProtocolBindings.JsonRpc or ProtocolBindings.HttpJson or ProtocolBindings.Grpc;
    }

    private static readonly Uri NoEndpoint = new("urn:a2a:no-endpoint");

    /// <summary>
    ///     The URL the transport would call. A gRPC URL may be a bare <c>host:port</c>, which <see cref="Uri" /> reads as a
    ///     scheme with
    ///     no host; like <c>GrpcTransport.ParseAddress</c> it is read as <c>https://host:port</c>. Null when no URL can be
    ///     made of it.
    /// </summary>
    private static Uri? ParseEndpoint(AgentInterface itf)
    {
        var url = itf.Url?.Trim();
        if (string.IsNullOrEmpty(url)) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)) return u;
        if (string.Equals(itf.ProtocolBinding, ProtocolBindings.Grpc, StringComparison.Ordinal))
            return Uri.TryCreate("https://" + url!.TrimStart('/'), UriKind.Absolute, out var g) ? g : null;
        return u;
    }

    private static readonly IReadOnlyList<IPAddress> NoAddresses = [];

    private static async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(Uri uri, bool allowDns,
        CancellationToken cancellationToken)
    {
        if (!uri.IsAbsoluteUri) return NoAddresses;
        var host = uri.Host.Trim('[', ']');
        var zone = host.IndexOf('%');
        if (zone >= 0) host = host.Substring(0, zone);
        if (host.Length == 0) return NoAddresses;
        var name = host.TrimEnd('.');
        if (IPAddress.TryParse(name, out var literal))
            return [literal.IsIPv4MappedToIPv6 ? literal.MapToIPv4() : literal];
        if (string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return [IPAddress.Loopback, IPAddress.IPv6Loopback];
#if NETSTANDARD2_0
        return NoAddresses;
#else
        if (!allowDns) return NoAddresses;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DnsTimeout);
        try
        {
            // WaitAsync: getaddrinfo is not cancellable on every platform, so the token must bound the wait itself.
            return await ResolveHost(name, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return NoAddresses; // a timeout or a failed lookup: unresolved
        }
#endif
    }

#if !NETSTANDARD2_0
    /// <summary>DNS seam: tests replace it.</summary>
    internal static Func<string, CancellationToken, Task<IPAddress[]>> ResolveHost { get; set; } = DefaultResolveHost;

    internal static Task<IPAddress[]> DefaultResolveHost(string host, CancellationToken cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);
#endif

    private static A2AException NoInterface(AgentCard card, A2AClientOptions options)
    {
        return new A2AException(A2AErrorCode.UnsupportedOperation,
            $"No supported interface in Agent Card '{card.Name}'. Client supports: {string.Join(", ", options.SupportedBindings)} (v{Major(options.ProtocolVersion)}.x).");
    }

    /// <summary>
    ///     Picks the first interface of the card that matches the client's supported bindings and protocol major version.
    ///     Returns null when none fits.
    /// </summary>
    public static AgentInterface? SelectInterface(AgentCard card, A2AClientOptions options)
    {
        var major = Major(options.ProtocolVersion);
        foreach (var itf in card.SupportedInterfaces ?? [])
        {
            if (!options.SupportedBindings.Contains(itf.ProtocolBinding)) continue;
            if (Major(itf.ProtocolVersion) != major) continue;
            if (FactoryFor(options, itf.ProtocolBinding) is { } factory)
            {
                if (factory.CanCreate(itf)) return itf;
                continue;
            }

            if (itf.ProtocolBinding is not (ProtocolBindings.JsonRpc or ProtocolBindings.HttpJson)) continue;
            if (!Uri.TryCreate(itf.Url, UriKind.Absolute, out _)) continue;
            return itf;
        }

        return null;
    }

    private static IA2ATransportFactory? FactoryFor(A2AClientOptions options, string binding)
    {
        return options.TransportFactories.LastOrDefault(f =>
            string.Equals(f.ProtocolBinding, binding, StringComparison.Ordinal));
    }

    private static string Major(string? version)
    {
        return version is null || version.Length == 0 ? "0" : version.Split('.')[0];
    }
}