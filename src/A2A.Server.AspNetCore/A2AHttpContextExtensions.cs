using Microsoft.AspNetCore.Http;

namespace A2A.Server.AspNetCore;

/// <summary>Helpers for bindings hosted on ASP.NET Core (for example the gRPC binding).</summary>
public static class A2AHttpContextExtensions
{
    /// <summary>
    ///     Builds the <see cref="ServerCallContext" /> for a request exactly as the JSON-RPC and REST bindings do: principal
    ///     from
    ///     <see cref="IPrincipalResolver" />, tenant from <see cref="ITenantResolver" />, and the A2A-Version, A2A-Extensions
    ///     and
    ///     Idempotency-Key headers. gRPC metadata are HTTP/2 headers, so the same names apply. It is the same code the
    ///     built-in
    ///     bindings use; it also reads the W3C <c>traceparent</c>/<c>tracestate</c> headers into
    ///     <see cref="ServerCallContext.TraceParent" />, and sets <see cref="ServerCallContext.Binding" /> to <c>GRPC</c> for
    ///     a gRPC
    ///     request (by content type) or <paramref name="binding" />. Pass the result to
    ///     <see cref="A2ARequestHandler.ValidateCall" /> before any operation, as they do.
    /// </summary>
    /// <param name="http">The request.</param>
    /// <param name="binding">The binding to report in traces; null to detect gRPC, else unknown.</param>
    /// <exception cref="A2AException">Unauthenticated when an authenticated user maps to no principal.</exception>
    public static ServerCallContext CreateA2ACallContext(this HttpContext http, string? binding = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (binding is null &&
            http.Request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true)
            binding = ProtocolBindings.Grpc;
        return HttpPlumbing.CreateCallContext(http, binding);
    }

    /// <summary>
    ///     The scheme to name in an authentication challenge for <paramref name="call" /> (spec 3.3.2, 7.4): the first HTTP
    ///     auth scheme of the card of the call's tenant, else <c>Bearer</c>. The JSON-RPC and REST bindings send it as
    ///     <c>WWW-Authenticate</c>; the gRPC binding sends it as <c>www-authenticate</c> metadata. Never throws for a card
    ///     that cannot be built: it falls back to the host's card, then to <c>Bearer</c>.
    /// </summary>
    public static ValueTask<string> GetA2AAuthChallengeSchemeAsync(this HttpContext http, ServerCallContext call)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(call);
        return HttpPlumbing.AuthChallengeSchemeAsync(http, call);
    }
}