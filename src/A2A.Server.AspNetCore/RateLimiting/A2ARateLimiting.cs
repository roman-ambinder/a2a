using System.Threading.RateLimiting;
using A2A.JsonRpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>Settings of the default per-caller rate limit (a token bucket per tenant and principal).</summary>
public sealed class A2ARateLimitOptions
{
    /// <summary>Burst size: the most requests a caller can make at once. Default 20.</summary>
    public int TokenLimit { get; set; } = 20;

    /// <summary>Tokens added back every <see cref="ReplenishmentPeriod"/>. Default 60.</summary>
    public int TokensPerPeriod { get; set; } = 60;

    /// <summary>How often tokens are added back. Default 1 minute.</summary>
    public TimeSpan ReplenishmentPeriod { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>Registers the rate limit policy and adds the middleware for A2A endpoints.</summary>
public static class A2ARateLimitingExtensions
{
    /// <summary>Name of the ASP.NET Core rate limiter policy applied to A2A endpoints.</summary>
    public const string PolicyName = "a2a";

    /// <summary>
    /// Registers the ASP.NET Core rate limiter policy <c>"a2a"</c>: a token bucket per <c>"{tenant}|{principal}"</c>
    /// (20 requests burst, 60 per minute by default). <c>MapA2A</c> applies it unless
    /// <see cref="A2AServerOptions.EnableRateLimiting"/> is false. A rejected request gets HTTP 429 with a <c>Retry-After</c>
    /// header and the <c>RATE_LIMITED</c> error, in the format of the binding. <c>AddA2AServerAspNetCore</c> calls this with
    /// the defaults; call it again to change them. The host must add the middleware with <c>app.UseA2A()</c>.
    /// </summary>
    public static IServiceCollection AddA2ARateLimiting(this IServiceCollection services, Action<A2ARateLimitOptions>? configure = null)
    {
        if (configure is not null) services.Configure(configure);
        if (services.Any(d => d.ServiceType == typeof(A2ARateLimitMarker))) return services;
        services.AddSingleton<A2ARateLimitMarker>();
        services.TryAddSingleton<A2ARateLimiterPolicy>();
        services.AddRateLimiter(o => o.AddPolicy<string, A2ARateLimiterPolicy>(PolicyName));
        return services;
    }

    /// <summary>
    /// Adds the A2A middleware: the ASP.NET Core rate limiter (<c>UseRateLimiter()</c>). Call it after <c>UseRouting()</c> (implicit
    /// with <c>WebApplication</c>) and <c>UseAuthentication()</c>, so the caller is known, and before <c>UseAuthorization()</c>.
    /// Use it instead of calling <c>UseRateLimiter()</c> yourself: the startup check looks for this call, and the limiter
    /// must not run twice.
    /// </summary>
    public static IApplicationBuilder UseA2A(this IApplicationBuilder app)
    {
        app.ApplicationServices.GetService<A2ARateLimitMarker>()?.MiddlewareAdded();
        return app.UseRateLimiter();
    }
}

/// <summary>
/// Records that the rate limit policy is registered and whether <c>UseA2A()</c> added the middleware. Set when the
/// pipeline is configured, which is before requests flow, so the startup check can read it.
/// </summary>
internal sealed class A2ARateLimitMarker
{
    private volatile bool _middlewareAdded;

    public bool IsMiddlewareAdded => _middlewareAdded;

    public void MiddlewareAdded() => _middlewareAdded = true;
}

/// <summary>
/// The <c>"a2a"</c> policy. The partition is <c>"{tenant}|{principal}"</c>, computed from the <see cref="HttpContext"/> with the
/// same resolvers the endpoints use (the rate limiter runs before the A2A endpoint builds its call context).
/// Unauthenticated callers share the principal <see cref="ServerCallContext.AnonymousPrincipal"/>.
/// </summary>
internal sealed class A2ARateLimiterPolicy(IOptions<A2ARateLimitOptions> options, ITenantResolver tenants, IPrincipalResolver principals)
    : IRateLimiterPolicy<string>
{
    private readonly A2ARateLimitOptions _options = options.Value;

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => RejectAsync;

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        var tenant = tenants.Resolve(httpContext) ?? "";
        var principal = ServerCallContext.AnonymousPrincipal;
        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            var resolved = principals.Resolve(httpContext.User);
            if (!string.IsNullOrEmpty(resolved)) principal = resolved;
        }
        return RateLimitPartition.GetTokenBucketLimiter($"{tenant}|{principal}", _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = _options.TokenLimit,
            TokensPerPeriod = _options.TokensPerPeriod,
            ReplenishmentPeriod = _options.ReplenishmentPeriod,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    private ValueTask RejectAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var wait = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : _options.ReplenishmentPeriod;
        if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
        var error = A2AException.RateLimited(wait);

        HttpPlumbing.SetRetryAfter(http, error);
        var isJsonRpc = http.GetEndpoint()?.DisplayName?.StartsWith(JsonRpcEndpoint.DisplayName, StringComparison.Ordinal) == true;
        return new ValueTask(isJsonRpc
            ? HttpPlumbing.WriteJsonAsync(http, JsonRpcResponse.Failure(null, error), A2AProtocol.JsonMediaType, 429)
            : HttpPlumbing.WriteJsonAsync(http, RestEndpoint.StatusBody(error), A2AProtocol.MediaType, 429));
    }
}
