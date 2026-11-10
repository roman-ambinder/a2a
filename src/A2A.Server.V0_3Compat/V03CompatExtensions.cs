using A2A.Server.AspNetCore;
using A2A.V0_3Compat;

namespace A2A.Server.V0_3Compat;

/// <summary>Opt-in to A2A protocol v0.3 on <c>MapA2A</c>.</summary>
public static class A2AV03CompatExtensions
{
    /// <summary>
    ///     Also serves v0.3 clients on this <c>MapA2A</c> mapping: the v0.3 JSON-RPC methods, the v0.3 HTTP+JSON routes under
    ///     <c>{pattern}/v1/...</c>, and a blended Agent Card (the v1.0 card plus the v0.3 members). The routes sit in the
    ///     group
    ///     <c>MapA2A</c> returns, so authorization, rate limiting and CORS on that group cover them. v0.3 push notifications
    ///     are not
    ///     supported (<c>-32003</c>). A tenant named <c>v1</c> on a path-tenant host is shadowed by the <c>/v1/...</c> routes.
    /// </summary>
    /// <param name="options">The options passed to <c>MapA2A</c>.</param>
    /// <returns><paramref name="options" />, for chaining.</returns>
    /// <exception cref="InvalidOperationException">The compat package and the core packages have different versions.</exception>
    public static A2AEndpointOptions EnableV03Compat(this A2AEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // The package uses core internals: refuse a mix of versions at startup instead of failing later on a missing member.
        var self = typeof(A2AV03CompatExtensions).Assembly;
        V03Compat.RequireSameVersion(self, typeof(A2AEndpointOptions).Assembly, typeof(V03Compat).Assembly,
            typeof(AgentCard).Assembly);
        options.ProtocolAdapter = new V03ProtocolAdapter();
        return options;
    }
}