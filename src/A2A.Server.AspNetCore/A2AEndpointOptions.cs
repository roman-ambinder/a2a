using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace A2A.Server.AspNetCore;

/// <summary>Options for <c>MapA2A(pattern, configure)</c>.</summary>
public sealed class A2AEndpointOptions
{
    /// <summary>
    ///     True (the default) to also map the public Agent Card at <c>/.well-known/agent-card.json</c> (and the JWKS when a
    ///     signing
    ///     key is set). The card is anonymous and is never inside the group <c>MapA2A</c> returns. Set false when the host
    ///     maps the
    ///     card elsewhere.
    /// </summary>
    public bool MapAgentCard { get; set; } = true;

    /// <summary>
    ///     The name of an ASP.NET Core authorization policy to require on the A2A endpoints (not on the card). Null:
    ///     none.
    /// </summary>
    public string? AuthorizationPolicy { get; set; }

    /// <summary>Optional protocol adapter (v0.3 compatibility). Set by the compat package. Null: v1.0 only.</summary>
    internal IA2AProtocolAdapter? ProtocolAdapter { get; set; }
}

/// <summary>
///     Lets another package add a protocol surface to <c>MapA2A</c> without changing the v1.0 endpoints. Implemented by
///     the
///     v0.3 compat package. Everything it maps goes into the group <c>MapA2A</c> returns, so the group's authorization,
///     rate limit
///     and CORS cover it.
/// </summary>
internal interface IA2AProtocolAdapter
{
    /// <summary>Rewrites the served Agent Card body (UTF-8 JSON). Null: serve the card as it is.</summary>
    Func<ReadOnlyMemory<byte>, byte[]>? CardBodyTransform { get; }

    /// <summary>
    ///     Wraps the JSON-RPC endpoint delegate. The wrapper either handles the request or calls <paramref name="core" />
    ///     .
    /// </summary>
    RequestDelegate WrapJsonRpc(IServiceProvider services, RequestDelegate core);

    /// <summary>
    ///     Maps extra routes into <paramref name="group" />. <paramref name="tenantInPattern" /> as in
    ///     <c>RestEndpoint.Map</c>.
    /// </summary>
    void MapRoutes(IServiceProvider services, RouteGroupBuilder group, bool tenantInPattern);
}