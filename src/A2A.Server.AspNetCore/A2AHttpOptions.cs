using System.Security.Claims;
using A2A.Signing;
using Microsoft.AspNetCore.Http;

namespace A2A.Server.AspNetCore;

/// <summary>HTTP-specific settings.</summary>
public sealed class A2AHttpOptions
{
    /// <summary>
    /// Maps the request to a stable caller id for task ownership. Default: the <c>sub</c> claim, then NameIdentifier,
    /// then the identity name; null when unauthenticated.
    /// </summary>
    public Func<HttpContext, string?> CallerIdResolver { get; set; } = DefaultCallerId;

    /// <summary>Interval for SSE keep-alive comments while a stream is idle. Default 15 seconds. Zero disables.</summary>
    public TimeSpan StreamKeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>When set, the served Agent Card is signed with this key and its public key is published at <see cref="JwksPath"/>.</summary>
    public AgentCardSigningKey? SigningKey { get; set; }

    public string JwksPath { get; set; } = "/.well-known/jwks.json";

    /// <summary>Cache-Control max-age for the Agent Card. Default 5 minutes.</summary>
    public TimeSpan AgentCardMaxAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum request body size in bytes for A2A endpoints. Default 10 MB.</summary>
    public long MaxRequestBodySize { get; set; } = 10 * 1024 * 1024;

    public static string? DefaultCallerId(HttpContext http)
    {
        var user = http.User;
        if (user.Identity?.IsAuthenticated != true) return null;
        return user.FindFirst("sub")?.Value
               ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
               ?? user.Identity.Name;
    }
}
