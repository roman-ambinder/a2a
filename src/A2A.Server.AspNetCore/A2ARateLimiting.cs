using System.Diagnostics;
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

    /// <summary>Tokens added back every <see cref="ReplenishmentPeriod" />. Default 60.</summary>
    public int TokensPerPeriod { get; set; } = 60;

    /// <summary>How often tokens are added back. Default 1 minute.</summary>
    public TimeSpan ReplenishmentPeriod { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     Turns rate limiting on or off. Default true. When false, <c>MapA2A</c> adds no rate limit policy and the startup
    ///     check
    ///     does not require <c>app.UseA2A()</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Registers the rate limit policy and adds the middleware for A2A endpoints.</summary>
public static class A2ARateLimitingExtensions
{
    /// <summary>Name of the ASP.NET Core rate limiter policy applied to A2A endpoints.</summary>
    public const string PolicyName = "a2a";

    /// <summary>
    ///     Registers the ASP.NET Core rate limiter policy <c>"a2a"</c>: a token bucket per tenant and caller
    ///     (20 requests burst, 60 per minute by default). The caller is the principal, or for an anonymous caller its remote
    ///     IP
    ///     address (<c>HttpContext.Connection.RemoteIpAddress</c>). <c>MapA2A</c> applies it unless
    ///     <see cref="A2ARateLimitOptions.Enabled" /> is false. A rejected request gets HTTP 429 with a <c>Retry-After</c>
    ///     header and the <c>RATE_LIMITED</c> error, in the format of the binding. <c>AddA2AServerAspNetCore</c> calls this
    ///     with
    ///     the defaults; call it again to change them. The host must add the middleware with <c>app.UseA2A()</c>.
    ///     One call is one request, so each later request on an A2A 1.1 SendLiveMessage call (a gRPC two-way stream) takes a
    ///     token from the same buckets too; one refused for want of a token becomes a <c>RESOURCE_EXHAUSTED</c> stream error.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Behind a reverse proxy or load balancer every connection comes from the proxy, so all anonymous callers would
    ///         share
    ///         the proxy's bucket. Enable the ForwardedHeaders middleware (<c>app.UseForwardedHeaders()</c> with the proxy
    ///         listed in
    ///         <c>ForwardedHeadersOptions.KnownProxies</c> or <c>KnownNetworks</c>) before <c>UseA2A()</c>, so the remote
    ///         address is the client's.
    ///     </para>
    ///     To replace the policy, register your own under the same name, for example
    ///     <c>services.AddRateLimiter(o =&gt; o.AddPolicy("a2a", ...))</c>. The default is then skipped (no duplicate-name
    ///     exception,
    ///     whatever the registration order) and yours is applied by <c>MapA2A</c>. Your policy counts each call once: the
    ///     later requests of a SendLiveMessage stream do not draw from it. To drop rate limiting, set
    ///     <see cref="A2ARateLimitOptions.Enabled" /> to false.
    /// </remarks>
    public static IServiceCollection AddA2ARateLimiting(this IServiceCollection services,
        Action<A2ARateLimitOptions>? configure = null)
    {
        if (configure is not null) services.Configure(configure);
        if (services.Any(d => d.ServiceType == typeof(A2ARateLimitMarker))) return services;
        services.AddSingleton<A2ARateLimitMarker>();
        services.TryAddSingleton<A2ARateLimiterPolicy>();
        services.AddRateLimiter(_ => { });
        // Post-configure runs after every host Configure, so a policy the host registered under "a2a" always wins.
        // The singleton itself is the policy (not a copy the middleware activates), so the later requests of a live stream
        // draw from the very buckets the middleware uses (see A2ARateLimiterPolicy.LiveAdmission).
        services.AddOptions<RateLimiterOptions>().PostConfigure<A2ARateLimiterPolicy, A2ARateLimitMarker>(
            (o, policy, marker) =>
            {
                try
                {
                    o.AddPolicy(PolicyName, policy);
                    marker.DefaultPolicyAdded = true;
                }
                catch (ArgumentException)
                {
                    /* the host supplied its own "a2a" policy */
                }
            });
        return services;
    }

    /// <summary>
    ///     Adds the A2A middleware: the ASP.NET Core rate limiter (<c>UseRateLimiter()</c>). Call it after <c>UseRouting()</c>
    ///     (implicit
    ///     with <c>WebApplication</c>) and <c>UseAuthentication()</c>, so the caller is known, and before
    ///     <c>UseAuthorization()</c>.
    ///     Use it instead of calling <c>UseRateLimiter()</c> yourself: the startup check looks for this call, and the limiter
    ///     must not run twice.
    /// </summary>
    public static IApplicationBuilder UseA2A(this IApplicationBuilder app)
    {
        app.ApplicationServices.GetService<A2ARateLimitMarker>()?.MiddlewareAdded();
        return app.UseRateLimiter();
    }
}

/// <summary>
///     Records that the rate limit policy is registered and whether <c>UseA2A()</c> added the middleware. Set when the
///     pipeline is configured, which is before requests flow, so the startup check can read it.
/// </summary>
internal sealed class A2ARateLimitMarker
{
    private volatile bool _middlewareAdded;

    public bool IsMiddlewareAdded => _middlewareAdded;

    /// <summary>True when the default policy is the registered <c>"a2a"</c> policy (the host did not supply its own).</summary>
    public bool DefaultPolicyAdded { get; set; }

    public void MiddlewareAdded()
    {
        _middlewareAdded = true;
    }
}

/// <summary>
///     The <c>"a2a"</c> policy. The partition of an authenticated caller is <c>"{tenant}|{caller}"</c>; an anonymous
///     caller's is
///     the caller alone (no tenant: see <see cref="GetPartition" />). The caller is
///     <see cref="ServerCallContext.LimitPartitionFor" />: the principal, computed from the <see cref="HttpContext" />
///     with the same
///     resolvers the endpoints use (the rate limiter runs before the A2A endpoint builds its call context), or for an
///     unauthenticated caller its remote IP address, so anonymous callers do not share one bucket. Every request also
///     draws
///     from a bucket per caller alone (same settings), so one caller cannot get more by spreading requests over tenants.
///     The buckets live here, not in the middleware's partitions, so <see cref="LiveAdmission" /> can take a token from
///     them for each later request of a live stream.
/// </summary>
internal sealed class A2ARateLimiterPolicy(
    IOptions<A2ARateLimitOptions> options,
    ITenantResolver tenants,
    IPrincipalResolver principals,
    IEnumerable<IA2ARejectionWriter> writers)
    : IRateLimiterPolicy<string>, IDisposable
{
    private readonly A2ARateLimitOptions _options = options.Value;

    private readonly PartitionedRateLimiter<string> _perPrincipal =
        PartitionedRateLimiter.Create<string, string>(principal =>
            RateLimitPartition.GetTokenBucketLimiter(principal, _ => Bucket(options.Value)));

    // Keyed by the partition key. A full bucket is idle and freed (with its timer) by the partitioned limiter.
    private readonly PartitionedRateLimiter<string> _perTenant =
        PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetTokenBucketLimiter(key, _ => Bucket(options.Value)));

    public void Dispose()
    {
        _perTenant.Dispose();
        _perPrincipal.Dispose();
    }

    /// <summary>
    ///     The <see cref="ServerCallContext.LiveRequestAdmission" /> of a request to an endpoint under this policy: takes a
    ///     token for each later request of a live stream from the caller's buckets, and throws <c>RateLimited</c> (with the
    ///     wait) when one is empty. Null when the endpoint is not under the <c>"a2a"</c> policy or the host replaced it.
    /// </summary>
    internal static Action? LiveAdmission(HttpContext http)
    {
        var metadata = http.GetEndpoint()?.Metadata;
        if (metadata?.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName != A2ARateLimitingExtensions.PolicyName ||
            metadata.GetMetadata<DisableRateLimitingAttribute>() is not null)
            return null;
        var services = http.RequestServices;
        if (services.GetService<A2ARateLimitMarker>() is not { } marker) return null;
        _ = services.GetRequiredService<IOptions<RateLimiterOptions>>().Value; // runs the post-configure that sets the flag
        if (!marker.DefaultPolicyAdded) return null;
        var policy = services.GetRequiredService<A2ARateLimiterPolicy>();
        (string Key, string Caller)? partition = null; // the same partition the middleware used; most calls never ask
        return () =>
        {
            var (key, caller) = partition ??= policy.Partition(http);
            using var lease = policy.Acquire(key, caller);
            if (!lease.IsAcquired) throw policy.Rejection(lease);
        };
    }

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => RejectAsync;

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        var (key, caller) = Partition(httpContext);
        return RateLimitPartition.Get(key, k => new TenantAndPrincipalLimiter(this, k, caller));
    }

    /// <summary>Takes a permit from the partition's bucket and from the caller's bucket; either one empty refuses.</summary>
    internal RateLimitLease Acquire(string key, string caller, int permitCount = 1)
    {
        var first = _perTenant.AttemptAcquire(key, permitCount);
        if (!first.IsAcquired) return first;
        var second = _perPrincipal.AttemptAcquire(caller, permitCount);
        if (second.IsAcquired) return first;
        first.Dispose();
        return second;
    }

    internal RateLimiterStatistics? Statistics(string key)
    {
        return _perTenant.GetStatistics(key);
    }

    private (string Key, string Caller) Partition(HttpContext httpContext)
    {
        var tenant = tenants.Resolve(httpContext) ?? "";
        var principal = ServerCallContext.AnonymousPrincipal;
        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            var resolved = principals.Resolve(httpContext.User);
            if (!string.IsNullOrEmpty(resolved)) principal = resolved;
        }

        var caller = ServerCallContext.LimitPartitionFor(principal, httpContext.Connection.RemoteIpAddress?.ToString());
        // The limiter runs before authorization and the tenant comes from the route, so it is attacker text for an anonymous
        // caller: POST /a2a/<random> would create a partition (a bucket with a timer) per name. Anonymous callers therefore
        // get no tenant in the key. Cost: anonymous callers from one address share one bucket across tenants. The key has
        // no '|', so it cannot collide with an authenticated key ("{tenant}|user:{id}").
        var key = principal == ServerCallContext.AnonymousPrincipal ? caller : $"{tenant}|{caller}";
        return (key, caller);
    }

    private static TokenBucketRateLimiterOptions Bucket(A2ARateLimitOptions o)
    {
        return new TokenBucketRateLimiterOptions
        {
            TokenLimit = o.TokenLimit,
            TokensPerPeriod = o.TokensPerPeriod,
            ReplenishmentPeriod = o.ReplenishmentPeriod,
            QueueLimit = 0,
            AutoReplenishment = true
        };
    }

    /// <summary>The <c>RateLimited</c> error of a refused lease, with its wait (at least one second).</summary>
    private A2AException Rejection(RateLimitLease lease)
    {
        var wait = lease.TryGetMetadata(MetadataName.RetryAfter, out var after)
            ? after
            : _options.ReplenishmentPeriod;
        if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
        return A2AException.RateLimited(wait);
    }

    private ValueTask RejectAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var error = Rejection(context.Lease);

        // A writer in the endpoint's own metadata comes first: the endpoint knows its format best.
        if (http.GetEndpoint()?.Metadata.GetMetadata<IA2ARejectionWriter>()?.TryWriteAsync(http, error) is { } own)
            return new ValueTask(own);
        foreach (var writer in writers)
            if (writer.TryWriteAsync(http, error) is { } written)
                return new ValueTask(written);

        HttpPlumbing.SetRetryAfter(http, error);
        var isJsonRpc = http.GetEndpoint()?.DisplayName
            ?.StartsWith(JsonRpcEndpoint.DisplayName, StringComparison.Ordinal) == true;
        return new ValueTask(isJsonRpc
            ? HttpPlumbing.WriteJsonAsync(http, JsonRpcResponse.Failure(null, error), A2AProtocol.JsonMediaType, 429)
            : HttpPlumbing.WriteJsonAsync(http, RestEndpoint.StatusBody(error), A2AProtocol.MediaType, 429));
    }
}

/// <summary>
///     Writes an A2A error that is raised before the endpoint runs (the <c>"a2a"</c> rate limit rejection) in a binding's
///     own
///     format. The JSON-RPC and HTTP+JSON formats are built in; a binding package (for example the gRPC binding) registers
///     one
///     for its endpoints with
///     <c>services.TryAddEnumerable(ServiceDescriptor.Singleton&lt;IA2ARejectionWriter, MyWriter&gt;())</c>, or puts one
///     in the metadata of its own endpoints (<c>.WithMetadata(writer)</c>), which is asked first.
///     The registered writers are asked in order; the first one that writes wins.
/// </summary>
public interface IA2ARejectionWriter
{
    /// <summary>
    ///     Writes <paramref name="error" /> (with its <see cref="A2AException.RetryAfter" />) and returns the write, or
    ///     returns null
    ///     when <paramref name="http" /> is not a request of this writer's binding.
    /// </summary>
    /// <param name="http">The rejected request.</param>
    /// <param name="error">The A2A error to send (<see cref="A2AErrorCode" /> RateLimited for the rate limit).</param>
    Task? TryWriteAsync(HttpContext http, A2AException error);
}

/// <summary>
///     The middleware's limiter of one partition: takes a permit from the tenant+principal bucket and from the shared
///     per-principal bucket, both held by the policy. Either one empty rejects the request. It owns no bucket, so the
///     middleware's idle cleanup may drop it at any time without losing a bucket's state; it reports itself idle a while
///     after its last request. Queueing is off, so the async path is the same as the sync one.
/// </summary>
internal sealed class TenantAndPrincipalLimiter(A2ARateLimiterPolicy policy, string key, string principal)
    : RateLimiter
{
    private long _lastUsed = Stopwatch.GetTimestamp();

    public override TimeSpan? IdleDuration => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastUsed));

    public override RateLimiterStatistics? GetStatistics()
    {
        return policy.Statistics(key);
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        Interlocked.Exchange(ref _lastUsed, Stopwatch.GetTimestamp());
        return policy.Acquire(key, principal, permitCount);
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        return new ValueTask<RateLimitLease>(AttemptAcquireCore(permitCount));
    }
}