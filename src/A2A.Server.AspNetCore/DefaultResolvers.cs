using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace A2A.Server.AspNetCore;

/// <summary>Default: the <c>tenant</c> route value, or <c>""</c> when the route has none.</summary>
public sealed class RouteTenantResolver : ITenantResolver
{
    /// <inheritdoc />
    public string Resolve(HttpContext http)
    {
        return http.GetRouteValue("tenant") as string ?? "";
    }
}