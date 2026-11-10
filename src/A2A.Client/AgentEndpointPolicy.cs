using System.Net;
#if !NETSTANDARD2_0
using A2A.Security.Http;
#endif

namespace A2A.Client;

/// <summary>
///     What an <see cref="A2AClientOptions.EndpointPolicy" /> judges: one endpoint that an Agent Card names, with the
///     addresses its
///     host and the card's own host resolve to. Empty address lists mean "not resolved": the host did not resolve, or no
///     lookup was made.
/// </summary>
/// <param name="endpoint">
///     The endpoint the client would call. Bare gRPC <c>host:port</c> values are read as
///     <c>https://host:port</c>.
/// </param>
/// <param name="protocolBinding">The interface's binding (<see cref="ProtocolBindings" />).</param>
/// <param name="cardSource">Where the card was fetched from, or null when the caller supplied the card.</param>
/// <param name="endpointAddresses">The addresses of the endpoint host.</param>
/// <param name="cardSourceAddresses">
///     The addresses of the card source host. Empty when <paramref name="cardSource" /> is
///     null.
/// </param>
public sealed class AgentEndpointCheck(
    Uri endpoint,
    string protocolBinding,
    Uri? cardSource,
    IReadOnlyList<IPAddress> endpointAddresses,
    IReadOnlyList<IPAddress> cardSourceAddresses)
{
    /// <summary>The endpoint the client would call.</summary>
    public Uri Endpoint { get; } = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    /// <summary>The interface's binding.</summary>
    public string ProtocolBinding { get; } =
        protocolBinding ?? throw new ArgumentNullException(nameof(protocolBinding));

    /// <summary>Where the card was fetched from. Null when the caller supplied the card.</summary>
    public Uri? CardSource { get; } = cardSource;

    /// <summary>The addresses of the endpoint host. Empty when unresolved.</summary>
    public IReadOnlyList<IPAddress> EndpointAddresses { get; } =
        endpointAddresses ?? throw new ArgumentNullException(nameof(endpointAddresses));

    /// <summary>The addresses of the card source host. Empty when unresolved or when <see cref="CardSource" /> is null.</summary>
    public IReadOnlyList<IPAddress> CardSourceAddresses { get; } =
        cardSourceAddresses ?? throw new ArgumentNullException(nameof(cardSourceAddresses));
}

/// <summary>
///     Ready-made values for <see cref="A2AClientOptions.EndpointPolicy" />. A card chooses the URL the client calls, so a
///     card from
///     the internet can point the client at an internal address (SSRF). The policy is a pre-flight check on the card; it
///     does not
///     stop DNS rebinding. For that, use an <see cref="HttpClient" /> built on <c>SafeHttp.CreateHandler</c>
///     (A2A.Security.Http).
/// </summary>
public static class AgentEndpointPolicies
{
    /// <summary>Allows every endpoint. The client makes no DNS lookup for it.</summary>
    public static Func<AgentEndpointCheck, bool> AllowAll { get; } = _ => true;

#if !NETSTANDARD2_0
    /// <summary>
    /// Refuses an endpoint when any of its addresses is private, loopback, link-local or reserved (<see cref="PrivateAddressGuard.IsBlocked"/>),
    /// when its host is an ambiguous numeric spelling (<c>0177.0.0.1</c>), or when it has no host. Addresses inside
    /// <paramref name="allowedNetworks"/> are allowed. A host that did not resolve is allowed: the connection fails anyway, or a proxy resolves it.
    /// </summary>
    public static Func<AgentEndpointCheck, bool> PublicOnly(params (IPAddress Network, int PrefixLength)[] allowedNetworks)
    {
        ArgumentNullException.ThrowIfNull(allowedNetworks);
        var allowed = allowedNetworks.ToArray();
        return check => IsPublic(check, allowed);
    }

    /// <summary>
    /// The default on .NET 10. A card the caller supplied (<see cref="AgentEndpointCheck.CardSource"/> is null) is trusted, and so is a
    /// card whose host resolves only to private addresses: it may name private endpoints. Any other card follows <see cref="PublicOnly"/>
    /// with no allowed networks. An endpoint with no host is always refused. Whoever controls the card host's DNS also controls this
    /// answer, so use <see cref="PublicOnly"/> for cards from the internet when private agents are not needed.
    /// </summary>
    public static Func<AgentEndpointCheck, bool> MatchCardNetwork { get; } = check =>
    {
        if (!HasHost(check.Endpoint)) return false;
        if (check.CardSource is null) return true;
        if (check.CardSourceAddresses.Count > 0 && check.CardSourceAddresses.All(PrivateAddressGuard.IsPrivate)) return true;
        return IsPublic(check, []);
    };

    private static bool IsPublic(AgentEndpointCheck check, (IPAddress Network, int PrefixLength)[] allowed) =>
        HasHost(check.Endpoint)
        && !PrivateAddressGuard.IsAmbiguousNumericHost(check.Endpoint.Host)
        && !check.EndpointAddresses.Any(a => PrivateAddressGuard.IsBlocked(a, allowed));

    private static bool HasHost(Uri endpoint) => endpoint.IsAbsoluteUri && endpoint.Host.Length > 0;
#endif
}