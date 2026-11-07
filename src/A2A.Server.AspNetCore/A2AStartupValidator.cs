using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>
/// Registration marker and endpoint registry. <c>MapA2A</c> throws when it is missing, so the startup check cannot be
/// skipped by mistake. Each <c>Map*</c> call records the route builder's data sources here. The validator reads them
/// directly: the <c>EndpointDataSource</c> in DI is only filled when the web host builds its pipeline, which happens
/// after hosted services start.
/// </summary>
internal sealed class A2AEndpointRegistry
{
    private readonly List<ICollection<EndpointDataSource>> _sources = [];

    public void Add(ICollection<EndpointDataSource> source)
    {
        lock (_sources)
        {
            if (!_sources.Any(s => ReferenceEquals(s, source))) _sources.Add(source);
        }
    }

    public bool HasSources
    {
        get { lock (_sources) return _sources.Count > 0; }
    }

    public IReadOnlyList<Endpoint> Endpoints()
    {
        lock (_sources) return _sources.SelectMany(s => s.ToList()).SelectMany(d => d.Endpoints).ToList();
    }
}

/// <summary>
/// Fail-closed startup check. Every A2A endpoint (display name starting <c>"A2A "</c>, except the Agent Card endpoints)
/// must carry authorization metadata and no <see cref="IAllowAnonymous"/> metadata, unless
/// <see cref="A2AServerOptions.AllowAnonymous"/> is true. When no endpoint is registered yet at start (Startup-style hosting
/// maps endpoints while the web host starts), the check runs again on <c>ApplicationStarted</c> and stops the app on failure.
/// <see cref="A2ARequestHandler"/> also rejects anonymous callers on its own as a backstop. The same check fails the start when
/// A2A endpoints use the rate limit policy but <c>app.UseA2A()</c> (the rate limiter middleware) was not added.
/// </summary>
internal sealed class A2AStartupValidator(A2AEndpointRegistry registry, IOptions<A2AServerOptions> options,
    IHostApplicationLifetime lifetime, ILogger<A2AStartupValidator> logger, A2ARateLimitMarker? rateLimit = null) : IHostedService
{
    internal const string NoPolicyMessage =
        "A2A endpoints have no authorization policy. Call RequireAuthorization() on MapA2A(...) or set A2AServerOptions.AllowAnonymous = true.";

    internal const string AnonymousWarning =
        "A2A endpoints allow anonymous callers. All anonymous callers share one owner and can read each other's tasks.";

    internal const string NoRateLimiterMessage =
        "Call app.UseA2A() (it adds the rate limiter middleware) or set A2AServerOptions.EnableRateLimiting = false.";

    private const string Prefix = "A2A ";
    private const string AgentCard = "A2A Agent Card";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.AllowAnonymous) logger.LogWarning(AnonymousWarning);

        if (!registry.HasSources)
        {
            lifetime.ApplicationStarted.Register(() =>
            {
                try { Check(); }
                catch (InvalidOperationException ex)
                {
                    logger.LogCritical(ex, "Stopping the application: {Message}", ex.Message);
                    lifetime.StopApplication();
                }
            });
            return Task.CompletedTask;
        }

        Check();
        return Task.CompletedTask;
    }

    private void Check()
    {
        var needsRateLimiter = false;
        foreach (var endpoint in registry.Endpoints())
        {
            var name = endpoint.DisplayName;
            if (name is null || !name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (name.StartsWith(AgentCard, StringComparison.Ordinal)) continue; // card and JWKS stay anonymous
            if (!options.Value.AllowAnonymous
                && (endpoint.Metadata.GetMetadata<IAuthorizeData>() is null || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null))
                throw new InvalidOperationException(NoPolicyMessage);
            if (endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == A2ARateLimitingExtensions.PolicyName)
                needsRateLimiter = true;
        }
        if (needsRateLimiter && rateLimit is { IsMiddlewareAdded: false })
            throw new InvalidOperationException(NoRateLimiterMessage);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
