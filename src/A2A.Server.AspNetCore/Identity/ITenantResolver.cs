using Microsoft.AspNetCore.Http;

namespace A2A.Server.AspNetCore;

/// <summary>Determines the tenant of an HTTP request. Return <c>""</c> for the default tenant. Tenants compare ordinally.</summary>
public interface ITenantResolver
{
    string Resolve(HttpContext http);
}
