using A2A.Server.Pipeline;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Rewrites the served Agent Card body (set by a protocol adapter). Null: the card is served as encoded.</summary>
    public Func<ReadOnlyMemory<byte>, byte[]>? CardBodyTransform { get; set; }

    public void Add(ICollection<EndpointDataSource> source)
    {
        lock (_sources)
        {
            if (!_sources.Any(s => ReferenceEquals(s, source))) _sources.Add(source);
        }
    }

    private readonly List<(string Binding, string Pattern)> _bindings = [];

    /// <summary>Records that <paramref name="binding"/> (a <see cref="ProtocolBindings"/> value) is mapped at <paramref name="pattern"/>.</summary>
    public void AddBinding(string binding, string pattern)
    {
        lock (_bindings)
        {
            if (!_bindings.Contains((binding, pattern))) _bindings.Add((binding, pattern));
        }
    }

    /// <summary>The recorded <c>(Binding, Pattern)</c> pairs, in mapping order.</summary>
    public IReadOnlyList<(string Binding, string Pattern)> Bindings()
    {
        lock (_bindings) return _bindings.ToList();
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ICollection<EndpointDataSource>, object> _cardMapped = new();

    /// <summary>True the first time it is called for <paramref name="source"/> (the route builder's data sources); false after.</summary>
    public bool TryMarkCardMapped(ICollection<EndpointDataSource> source)
    {
        lock (_cardMapped) return _cardMapped.TryAdd(source, true);
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
/// A2A endpoints use the rate limit policy but <c>app.UseA2A()</c> (the rate limiter middleware) was not added, and when the
/// Agent Card has no name (<c>AddAgentCard(...)</c> was not called).
/// </summary>
internal sealed class A2AStartupValidator(A2AEndpointRegistry registry, IOptions<A2AServerOptions> options,
    IHostApplicationLifetime lifetime, ILogger<A2AStartupValidator> logger, IServiceProvider services,
    IEnumerable<A2AExtensionOperation> extensions, A2ARateLimitMarker? rateLimit = null) : IHostedService
{
    internal const string NoPolicyMessage =
        "A2A endpoints have no authorization policy. Call app.MapA2A(...).RequireAuthorization() or MapA2A(pattern, o => o.AuthorizationPolicy = \"...\"), or set A2AServerOptions.AllowAnonymous = true.";

    internal const string AnonymousWarning =
        "A2A endpoints allow anonymous callers. All anonymous callers share one owner and can read each other's tasks.";

    internal const string NoRateLimiterMessage =
        "Call app.UseA2A() before app.UseAuthorization(), or set A2ARateLimitOptions.Enabled = false (services.AddA2ARateLimiting(o => o.Enabled = false)).";

    internal const string NoCardNameMessage =
        "The Agent Card has no name. Call AddAgentCard(...) on the A2A builder or set A2AServerOptions.AgentCard.";

    private const string Prefix = "A2A ";
    private const string AgentCard = "A2A Agent Card";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        CheckCardName();
        await CheckExtensionPoliciesAsync().ConfigureAwait(false);
        if (options.Value.AllowAnonymous) AspNetCoreLog.AnonymousCallersAllowed(logger);

        if (!registry.HasSources)
        {
            lifetime.ApplicationStarted.Register(() =>
            {
                try { Check(); }
                catch (InvalidOperationException ex)
                {
                    AspNetCoreLog.StoppingApplication(logger, ex, ex.Message);
                    lifetime.StopApplication();
                }
            });
            return;
        }

        Check();
    }

    /// <summary>
    /// Fails when the Agent Card has no name (a forgotten <c>AddAgentCard(...)</c>), unless a custom <see cref="IAgentCardProvider"/>
    /// that does not build on <see cref="AgentCardProvider"/> supplies the card, or a <see cref="Agents.TenantCardProvider"/>
    /// supplies each tenant's card.
    /// </summary>
    private void CheckCardName()
    {
        if (services.GetService<IAgentCardProvider>() is not (null or AgentCardProvider)) return;
        if (services.GetService<Agents.TenantCardProvider>() is not null) return;
        if (string.IsNullOrWhiteSpace(options.Value.AgentCard?.Name)) throw new InvalidOperationException(NoCardNameMessage);
    }

    /// <summary>Fails when an extension operation names an authorization policy the app cannot evaluate (fail closed).</summary>
    private async Task CheckExtensionPoliciesAsync()
    {
        foreach (var extension in extensions)
        {
            if (extension.AuthorizationPolicy is not { } policy) continue;
            var provider = services.GetService<IAuthorizationPolicyProvider>();
            if (services.GetService<IAuthorizationService>() is null || provider is null)
                throw new InvalidOperationException(
                    $"Extension operation '{extension.Method}' names authorization policy '{policy}', but no IAuthorizationService is registered. Call services.AddAuthorization().");
            if (await provider.GetPolicyAsync(policy).ConfigureAwait(false) is null)
                throw new InvalidOperationException(
                    $"Extension operation '{extension.Method}' names authorization policy '{policy}', which is not registered.");
        }
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
