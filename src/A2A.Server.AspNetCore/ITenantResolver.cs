using Microsoft.AspNetCore.Http;

namespace A2A.Server.AspNetCore;

/// <summary>Determines the tenant of an HTTP request. Return <c>""</c> for the default tenant. Tenants compare ordinally.</summary>
public interface ITenantResolver
{
    /// <summary>Returns the tenant of the request. <c>""</c> is the default tenant.</summary>
    string Resolve(HttpContext http);
}
