using Microsoft.AspNetCore.Http;

namespace A2A.Server.AspNetCore;

/// <summary>Helpers for bindings hosted on ASP.NET Core (for example the gRPC binding).</summary>
public static class A2AHttpContextExtensions
{
    /// <summary>
    /// Builds the <see cref="ServerCallContext"/> for a request exactly as the JSON-RPC and REST bindings do: principal from
    /// <see cref="IPrincipalResolver"/>, tenant from <see cref="ITenantResolver"/>, and the A2A-Version, A2A-Extensions and
    /// Idempotency-Key headers. gRPC metadata are HTTP/2 headers, so the same names apply.
    /// </summary>
    /// <exception cref="A2AException">Unauthenticated when an authenticated user maps to no principal.</exception>
    public static ServerCallContext CreateA2ACallContext(this HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return HttpPlumbing.CreateCallContext(http);
    }
}
