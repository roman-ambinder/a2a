using System.Threading.RateLimiting;
using A2A.JsonRpc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
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

    /// <summary>Turns rate limiting on or off. Default true. When false, <c>MapA2A</c> adds no rate limit.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Registers the per-caller rate limit of the A2A endpoints.</summary>
public static class A2ARateLimitingExtensions
{
    /// <summary>
    ///     Service key of the rate limit policy: register your own <see cref="IRateLimiterPolicy{TPartitionKey}" /> of
    ///     <see cref="string" /> under this key to replace the default.
    /// </summary>
    public const string PolicyName = "a2a";

    /// <summary>
    ///     Registers the per-caller rate limit of the A2A endpoints: a token bucket per tenant and caller (20 requests burst,
    ///     60 per minute by default). The caller is the principal, or for an anonymous caller its remote IP address
    ///     (<c>HttpContext.Connection.RemoteIpAddress</c>). <c>MapA2A</c> applies it unless
    ///     <see cref="A2ARateLimitOptions.Enabled" /> is false; no middleware is needed. A rejected request gets HTTP 429 with
    ///     a <c>Retry-After</c> header and the <c>RATE_LIMITED</c> error, in the format of the binding. <c>AddA2A</c> calls
    ///     this with the defaults; change them with <c>ConfigureRateLimiting</c> on the A2A builder, which calls this.
    /// </summary>
    internal static IServiceCollection AddA2ARateLimiting(this IServiceCollection services,
        Action<A2ARateLimitOptions>? configure = null)
    {
        if (configure is not null) services.Configure(configure);
        if (services.Any(d => d.ServiceType == typeof(A2ARateLimitMarker))) return services;
        services.AddSingleton<A2ARateLimitMarker>();
        services.TryAddSingleton<A2ARateLimiterPolicy>();
        services.TryAddSingleton<A2ARateLimitGate>();
        DecorateAuthorizationResultHandler(services);
        return services;
    }

    /// <summary>
    ///     Wraps the authorization middleware's result handler (the default one when none is registered yet), so the limit
    ///     is taken after authentication and before the authorization decision is acted on.
    /// </summary>
    private static void DecorateAuthorizationResultHandler(IServiceCollection services)
    {
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IAuthorizationMiddlewareResultHandler));
        if (existing is not null) services.Remove(existing);
        services.AddSingleton<IAuthorizationMiddlewareResultHandler>(sp =>
            new A2ARateLimitAuthorizationResultHandler(Create(sp, existing), sp.GetRequiredService<A2ARateLimitGate>()));
    }

    private static IAuthorizationMiddlewareResultHandler Create(IServiceProvider sp, ServiceDescriptor? descriptor)
    {
        return descriptor switch
        {
            null => new AuthorizationMiddlewareResultHandler(),
            { ImplementationInstance: IAuthorizationMiddlewareResultHandler instance } => instance,
            { ImplementationFactory: { } factory } => (IAuthorizationMiddlewareResultHandler)factory(sp),
            { ImplementationType: { } type } =>
                (IAuthorizationMiddlewareResultHandler)ActivatorUtilities.CreateInstance(sp, type),
            _ => new AuthorizationMiddlewareResultHandler()
        };
    }
}

/// <summary>Records that the rate limit is registered (<see cref="A2ARateLimitingExtensions.AddA2ARateLimiting" />).</summary>
internal sealed class A2ARateLimitMarker;

/// <summary>Endpoint metadata: this A2A endpoint is under the rate limit.</summary>
internal sealed class A2ARateLimitMetadata
{
    public static readonly A2ARateLimitMetadata Instance = new();
}

/// <summary>
///     Takes one permit per request from the policy's partition: the host's keyed policy
///     (<see cref="A2ARateLimitingExtensions.PolicyName" />) or the default <see cref="A2ARateLimiterPolicy" />. A request is
///     counted once, whether the authorization result handler or the endpoint asks first.
/// </summary>
internal sealed class A2ARateLimitGate : IDisposable
{
    private static readonly object CountedKey = new();
    private readonly PartitionedRateLimiter<HttpContext> _limiter;
    private readonly IRateLimiterPolicy<string> _policy;

    public A2ARateLimitGate(IServiceProvider services)
    {
        _policy = services.GetKeyedService<IRateLimiterPolicy<string>>(A2ARateLimitingExtensions.PolicyName)
                  ?? services.GetRequiredService<A2ARateLimiterPolicy>();
        _limiter = PartitionedRateLimiter.Create<HttpContext, string>(_policy.GetPartition);
    }

    public void Dispose()
    {
        _limiter.Dispose();
    }

    /// <summary>
    ///     True when the request may go on. False when it was rejected: the response (429 in the binding's format) is
    ///     written.
    /// </summary>
    public async ValueTask<bool> TryEnterAsync(HttpContext http)
    {
        if (http.Items.ContainsKey(CountedKey)) return true;
        http.Items[CountedKey] = true;
        var lease = await _limiter.AcquireAsync(http, 1, http.RequestAborted).ConfigureAwait(false);
        if (lease.IsAcquired)
        {
            http.Response.RegisterForDispose(lease);
            return true;
        }

        using (lease)
        {
            http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            if (_policy.OnRejected is { } onRejected)
                await onRejected(new OnRejectedContext { HttpContext = http, Lease = lease }, http.RequestAborted)
                    .ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Wraps an A2A endpoint so it takes the limit when the authorization result handler did not.</summary>
    public static RequestDelegate Wrap(RequestDelegate inner)
    {
        return async http =>
        {
            var gate = http.RequestServices.GetRequiredService<A2ARateLimitGate>();
            if (await gate.TryEnterAsync(http).ConfigureAwait(false)) await inner(http).ConfigureAwait(false);
        };
    }
}

/// <summary>
///     Takes the limit for A2A endpoints after authentication and before the authorization result is acted on, so callers
///     with wrong credentials are limited too. Other endpoints pass straight through.
/// </summary>
internal sealed class A2ARateLimitAuthorizationResultHandler(
    IAuthorizationMiddlewareResultHandler inner,
    A2ARateLimitGate gate) : IAuthorizationMiddlewareResultHandler
{
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<A2ARateLimitMetadata>() is not null
            && !await gate.TryEnterAsync(context).ConfigureAwait(false))
            return;
        await inner.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
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

    public void Dispose()
    {
        _perPrincipal.Dispose();
    }

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

        var caller = ServerCallContext.LimitPartitionFor(principal, httpContext.Connection.RemoteIpAddress?.ToString());
        // The limiter runs before authorization and the tenant comes from the route, so it is attacker text for an anonymous
        // caller: POST /a2a/<random> would create a partition (a bucket with a timer) per name. Anonymous callers therefore
        // get no tenant in the key. Cost: anonymous callers from one address share one bucket across tenants. The key has
        // no '|', so it cannot collide with an authenticated key ("{tenant}|user:{id}").
        var key = principal == ServerCallContext.AnonymousPrincipal ? caller : $"{tenant}|{caller}";
        return RateLimitPartition.Get(key,
            _ => new TenantAndPrincipalLimiter(new TokenBucketRateLimiter(Bucket(_options)), _perPrincipal, caller));
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

    private ValueTask RejectAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var wait = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after)
            ? after
            : _options.ReplenishmentPeriod;
        if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
        var error = A2AException.RateLimited(wait);

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
///     Takes a permit from the tenant+principal bucket and from the shared per-principal bucket. Either one empty rejects
///     the
///     request. Queueing is off, so the async path is the same as the sync one.
/// </summary>
internal sealed class TenantAndPrincipalLimiter(
    RateLimiter tenantBucket,
    PartitionedRateLimiter<string> perPrincipal,
    string principal) : RateLimiter
{
    public override TimeSpan? IdleDuration => tenantBucket.IdleDuration;

    public override RateLimiterStatistics? GetStatistics()
    {
        return tenantBucket.GetStatistics();
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        var first = tenantBucket.AttemptAcquire(permitCount);
        if (!first.IsAcquired) return first;
        var second = perPrincipal.AttemptAcquire(principal, permitCount);
        if (second.IsAcquired) return first;
        first.Dispose();
        return second;
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        return new ValueTask<RateLimitLease>(AttemptAcquireCore(permitCount));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tenantBucket.Dispose();
        base.Dispose(disposing);
    }

    // The partitioned limiter's idle cleanup calls DisposeAsync, which runs Dispose(false). Without this the bucket and its
    // replenishment timer would never be released.
    protected override async ValueTask DisposeAsyncCore()
    {
        await tenantBucket.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }
}