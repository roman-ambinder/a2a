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

    /// <summary>
    /// The caller's IP address as the binding saw it (ASP.NET Core: <c>HttpContext.Connection.RemoteIpAddress</c>), or null
    /// when unknown. Behind a reverse proxy this is the proxy's address unless the host enables the ForwardedHeaders middleware.
    /// </summary>
    public string? RemoteAddress { get; init; }

    /// <summary>
    /// The caller's key for per-caller limits (rate limit, open streams): see <see cref="LimitPartitionFor"/>. Task, context
    /// and push config ownership use <see cref="Principal"/> instead.
    /// </summary>
    public string LimitPartition => LimitPartitionFor(Principal, RemoteAddress);

    /// <summary>
    /// The key for per-caller limits: <c>user:{principal}</c> for an authenticated caller, and <c>anon:{remoteAddress}</c>
    /// (<c>anon:unknown</c> without an address) for <see cref="AnonymousPrincipal"/>, so anonymous callers do not share one
    /// budget. The prefixes keep a principal id from colliding with an address key.
    /// </summary>
    public static string LimitPartitionFor(string principal, string? remoteAddress) =>
        string.Equals(principal, AnonymousPrincipal, StringComparison.Ordinal)
            ? "anon:" + (string.IsNullOrEmpty(remoteAddress) ? "unknown" : remoteAddress)
            : "user:" + principal;

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
