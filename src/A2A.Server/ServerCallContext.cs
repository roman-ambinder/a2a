using System.Security.Claims;

namespace A2A.Server;

/// <summary>Per-request information supplied by the protocol binding (HTTP layer).</summary>
public sealed class ServerCallContext
{
    /// <summary>Stable id of the authenticated caller. Used for task ownership. Null for anonymous callers.</summary>
    public string? CallerId { get; init; }

    public ClaimsPrincipal? User { get; init; }

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;

    /// <summary>Tenant from the request (path segment or <c>tenant</c> field).</summary>
    public string? Tenant { get; set; }

    /// <summary>Value of the A2A-Version header, or null if absent.</summary>
    public string? ProtocolVersion { get; init; }

    /// <summary>Extension URIs from the A2A-Extensions header.</summary>
    public IReadOnlyList<string> RequestedExtensions { get; init; } = [];

    /// <summary>
    /// Extensions the server applied to this request. Starts as requested ∩ declared; executors may remove entries.
    /// Echoed back in the A2A-Extensions response header so clients know what was honored.
    /// </summary>
    public ISet<string> ActivatedExtensions { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Free-form per-request state for bindings and executors.</summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    public static ServerCallContext Anonymous(string? protocolVersion = A2AProtocol.Version) => new() { ProtocolVersion = protocolVersion };
}
