using System.Security.Claims;

namespace A2A.Server;

/// <summary>Per-request information supplied by the protocol binding (HTTP layer).</summary>
public sealed class ServerCallContext
{
    /// <summary>Principal used for anonymous callers.</summary>
    public const string AnonymousPrincipal = "anonymous";

    /// <summary>Stable id of the caller. Never null; <see cref="AnonymousPrincipal"/> for unauthenticated callers.</summary>
    public string Principal { get; init; } = AnonymousPrincipal;

    public ClaimsPrincipal? User { get; init; }

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;

    /// <summary>Tenant of the request (path segment or <c>tenant</c> field). Never null; <c>""</c> is the default tenant. Compared ordinally.</summary>
    public string Tenant { get; set; } = "";

    /// <summary>True when <see cref="Tenant"/> came from the endpoint route, so a different body tenant is a conflict.</summary>
    public bool TenantFromRoute { get; init; }

    /// <summary>
    /// Applies the tenant carried in a request body. Null or empty is the same as absent. If the context has no tenant yet,
    /// the body tenant is adopted. If both are non-empty and differ (ordinal), the request is rejected.
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

    /// <summary>Value of the A2A-Version header, or null if absent.</summary>
    public string? ProtocolVersion { get; init; }

    /// <summary>
    /// Value of the <c>Idempotency-Key</c> header, or null if absent. SendMessage uses it to recognize a retry; without it the
    /// message id is the key. Keys are scoped by <see cref="Tenant"/> and <see cref="Principal"/>.
    /// </summary>
    public string? IdempotencyKey { get; init; }

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
