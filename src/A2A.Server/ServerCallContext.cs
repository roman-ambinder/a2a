using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Claims;
using A2A.Server.Agents;
using IPAddress = System.Net.IPAddress;

namespace A2A.Server;

/// <summary>Per-request information supplied by the protocol binding (HTTP layer).</summary>
/// <remarks>
///     One instance per call. A custom binding creates a new one for every call and must not reuse it across calls: its
///     <see cref="Items" /> carry per-call authorization results, such as
///     <see cref="Pipeline.A2AExtensionOperation.PolicySatisfiedItem" />.
/// </remarks>
public sealed class ServerCallContext
{
    /// <summary>Principal used for anonymous callers.</summary>
    public const string AnonymousPrincipal = "anonymous";

    /// <summary>Stable id of the caller. Never null; <see cref="AnonymousPrincipal" /> for unauthenticated callers.</summary>
    public string Principal { get; init; } = AnonymousPrincipal;

    /// <summary>The authenticated user from the host, or null for anonymous callers.</summary>
    public ClaimsPrincipal? User { get; init; }

    /// <summary>
    ///     The caller's IP address as the binding saw it (ASP.NET Core: <c>HttpContext.Connection.RemoteIpAddress</c>), or
    ///     null
    ///     when unknown. Behind a reverse proxy this is the proxy's address unless the host enables the ForwardedHeaders
    ///     middleware.
    /// </summary>
    public string? RemoteAddress { get; init; }

    /// <summary>
    ///     The caller's key for per-caller limits (rate limit, open streams): see <see cref="LimitPartitionFor" />. Task,
    ///     context
    ///     and push config ownership use <see cref="Principal" /> instead.
    /// </summary>
    public string LimitPartition => LimitPartitionFor(Principal, RemoteAddress);

    /// <summary>
    ///     The caller's bearer token. Set by a binding only when a token exchanger is registered and a bearer scheme
    ///     validated the token as the caller's; readable only by the server
    ///     core,
    ///     so agent code never sees it and cannot forward it. Never logged or stored.
    /// </summary>
    public string? InboundBearerToken { internal get; init; }

    /// <summary>
    ///     Set by a binding whose authentication depends on the tenant and ran before the request body was read (ASP.NET
    ///     Core: <c>RequireAgentCardSchemes</c>), so a <c>tenant</c> the body carries was not seen by it. The server calls it
    ///     with the call's bound tenant before it authorizes the operation; false refuses the call as
    ///     <see cref="A2AErrorCode.Unauthenticated" />. Readable only by the server core.
    /// </summary>
    public Func<ServerCallContext, CancellationToken, ValueTask<bool>>? TenantAuthentication { internal get; init; }

    /// <summary>True when <see cref="User" /> is set and authenticated.</summary>
    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;

    /// <summary>
    ///     Tenant of the request (path segment or <c>tenant</c> field). Never null; <c>""</c> is the default tenant.
    ///     Compared ordinally.
    /// </summary>
    public string Tenant { get; set; } = "";

    /// <summary>True when <see cref="Tenant" /> came from the endpoint route, so a different body tenant is a conflict.</summary>
    public bool TenantFromRoute { get; init; }

    /// <summary>
    ///     The route values of the HTTP request (for example <c>tenant</c>), as strings; empty when the binding has none.
    ///     Passed to
    ///     <see cref="IAgentCardProvider.GetAsync" /> in <see cref="Agents.AgentRequestScope.RouteValues" />.
    /// </summary>
    public IReadOnlyDictionary<string, string?> RouteValues { get; init; } = AgentRequestScope.NoRouteValues;

    /// <summary>
    ///     The card resolved for this request and the tenant it was resolved for (resolved at most once per request and
    ///     tenant).
    /// </summary>
    internal (string Tenant, AgentCard? Card)? ResolvedCard { get; set; }

    /// <summary>
    ///     The skill id the server checked and allowed for this message (see
    ///     <see cref="Authorization.SkillAuthorizationOptions" />). Null when no check ran or no skill was named.
    /// </summary>
    internal string? AuthorizedSkillId { get; set; }

    /// <summary>Value of the A2A-Version header, or null if absent.</summary>
    public string? ProtocolVersion { get; init; }

    /// <summary>
    ///     The version the caller used on the wire, when a compatibility surface translated the call so it differs from
    ///     <see cref="ProtocolVersion" />. Telemetry reports it; null means the same as <see cref="ProtocolVersion" />.
    /// </summary>
    public string? WireProtocolVersion { get; init; }

    /// <summary>
    ///     Value of the <c>Idempotency-Key</c> header, or null if absent. SendMessage uses it to recognize a retry; without it
    ///     the
    ///     message id is the key. Keys are scoped by <see cref="Tenant" /> and <see cref="Principal" />. A key (header or
    ///     message
    ///     id) longer than 256 characters is refused with InvalidParams.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Extension URIs from the A2A-Extensions header.</summary>
    public IReadOnlyList<string> RequestedExtensions { get; init; } = [];

    /// <summary>
    ///     True for a call that comes from a plain HTTP client that cannot send the A2A-Extensions header (an artifact
    ///     download by a
    ///     browser or a downloader): the Agent Card's required extensions are not enforced for it.
    ///     <b>Warning:</b> this turns off the required-extension check for every operation run with this context. Use it only
    ///     in a
    ///     binding that serves a non-A2A resource, never for a call that reaches an agent. Requested extensions that the card
    ///     declares are still activated. Default false.
    /// </summary>
    public bool SkipRequiredExtensions { get; init; }

    /// <summary>
    ///     Extensions the server applied to this request. Starts as requested ∩ declared; executors may remove entries.
    ///     Echoed back in the A2A-Extensions response header so clients know what was honored.
    /// </summary>
    public ISet<string> ActivatedExtensions { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    ///     The protocol binding of the request (<see cref="ProtocolBindings" />: <c>JSONRPC</c>, <c>HTTP+JSON</c>, <c>GRPC</c>
    ///     ), or
    ///     null when unknown. Used as the <c>a2a.binding</c> span tag.
    /// </summary>
    public string? Binding { get; init; }

    /// <summary>
    ///     The caller's W3C trace context (from the <c>traceparent</c> and <c>tracestate</c> headers or gRPC metadata), or the
    ///     default (empty) context. Server spans use it as their parent when no local span of the same trace is current (for
    ///     example when ASP.NET Core did not start its own request activity).
    /// </summary>
    public ActivityContext TraceParent { get; init; }

    /// <summary>
    ///     A2A 1.1: asked before each later request of a live exchange
    ///     (<see cref="A2ARequestHandler.SendLiveMessageAsync" />) that comes on this same call, such as every request of a
    ///     gRPC SendLiveMessage stream after the first. A binding counts one call as one request, so it sets this to apply its
    ///     per-request limits to each of them too. Throw an <see cref="A2AException" /> (for example
    ///     <see cref="A2AErrorCode.RateLimited" />) to refuse that request: it becomes a <see cref="StreamErrorEvent" /> on the
    ///     downlink, as any refusal of a later request does, and the exchange stays open. The ASP.NET Core binding sets it to
    ///     take a token from the caller's <c>"a2a"</c> rate limit. Null (the default): no check.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.Experimental("A2AEXP005",
        UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public Action? LiveRequestAdmission { get; init; }

    /// <summary>Free-form per-request state for bindings and executors.</summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    /// <summary>
    ///     The key for per-caller limits: <c>user:{principal}</c> for an authenticated caller, and for
    ///     <see cref="AnonymousPrincipal" />
    ///     <c>anon:{address}</c> (<c>anon:unknown</c> without an address), so anonymous callers do not share one budget. An
    ///     IPv4
    ///     address (also IPv4-mapped IPv6) is its own partition. An IPv6 address is grouped by its /64 prefix, written
    ///     <c>anon:2001:db8:1:2::/64</c>, because one host usually controls a whole /64 and could otherwise pick a fresh
    ///     address per
    ///     request. The prefixes keep a principal id from colliding with an address key.
    /// </summary>
    public static string LimitPartitionFor(string principal, string? remoteAddress)
    {
        return string.Equals(principal, AnonymousPrincipal, StringComparison.Ordinal)
            ? "anon:" + AnonymousAddressKey(remoteAddress)
            : "user:" + principal;
    }

    private static string AnonymousAddressKey(string? remoteAddress)
    {
        if (string.IsNullOrEmpty(remoteAddress)) return "unknown";
        if (!IPAddress.TryParse(remoteAddress, out var ip)) return remoteAddress;
        if (ip.IsIPv4MappedToIPv6) return ip.MapToIPv4().ToString();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return ip.ToString();
        Span<byte> bytes = stackalloc byte[16];
        ip.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes) + "/64";
    }

    /// <summary>
    ///     Applies the tenant carried in a request body. Null or empty is the same as absent. If the context has no tenant
    ///     yet,
    ///     the body tenant is adopted. If both are non-empty and differ (ordinal), the request is rejected.
    /// </summary>
    public void BindTenant(string? bodyTenant)
    {
        if (string.IsNullOrEmpty(bodyTenant)) return;
        if (Tenant.Length == 0 && !TenantFromRoute)
        {
            Tenant = bodyTenant;
            return;
        }

        if (!string.Equals(Tenant, bodyTenant, StringComparison.Ordinal))
            throw A2AException.InvalidParams("tenant in request body does not match the endpoint tenant");
    }

    /// <summary>
    ///     Creates a context for an anonymous caller. <paramref name="protocolVersion" /> defaults to
    ///     <see cref="A2AProtocol.Version" />.
    /// </summary>
    public static ServerCallContext Anonymous(string? protocolVersion = A2AProtocol.Version)
    {
        return new ServerCallContext { ProtocolVersion = protocolVersion };
    }
}