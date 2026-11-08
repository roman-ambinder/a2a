using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using A2A.Server.Push;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Text.RegularExpressions;

namespace A2A.Server.AspNetCore;

/// <summary>Dependency injection extensions for the ASP.NET Core side of the A2A server.</summary>
public static class A2AServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ASP.NET Core side of the A2A server: the <see cref="Operations.A2AOperationTable"/> the endpoints dispatch
    /// through, the default <see cref="ITenantResolver"/> (<see cref="RouteTenantResolver"/>),
    /// the startup check that fails the app when A2A endpoints have no authorization policy (see
    /// <see cref="A2AServerOptions.AllowAnonymous"/>), and the default rate limit policy (see <see cref="A2ARateLimitingExtensions.AddA2ARateLimiting"/>; the host
    /// adds the middleware with <c>app.UseA2A()</c>). Call it once next to <c>AddA2AServer(...)</c>. <c>MapA2A</c> throws
    /// <see cref="InvalidOperationException"/> if it was not called, so the check cannot be skipped by mistake.
    /// </summary>
    public static IServiceCollection AddA2AServerAspNetCore(this IServiceCollection services)
    {
        services.TryAddSingleton<A2AEndpointRegistry>();
        services.Replace(ServiceDescriptor.Singleton<IAgentCardProvider, AgentCardCompleter>());
        services.TryAddSingleton<Operations.A2AOperationTable>();
        services.TryAddSingleton<ITenantResolver, RouteTenantResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, A2AStartupValidator>());
        services.AddA2ARateLimiting();
        return services;
    }

    /// <summary>
    /// One-call registration of the A2A server: <c>AddA2AServer()</c>, then <see cref="AddA2AServerAspNetCore"/>, then
    /// <paramref name="configure"/> with the builder (agent, card, stores, filters). Returns the service collection, like
    /// <see cref="AddA2AServerAspNetCore"/>; the builder is only available inside <paramref name="configure"/>. Hosts that need
    /// <see cref="A2AServerOptions"/> set at registration use the two-step form.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    public static IServiceCollection AddA2A(this IServiceCollection services, Action<A2AServerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = services.AddA2AServer();
        services.AddA2AServerAspNetCore();
        configure(builder);
        return services;
    }

    /// <summary>Configures HTTP-level settings (caller id, SSE keep-alive, card signing).</summary>
    public static IServiceCollection ConfigureA2AHttp(this IServiceCollection services, Action<A2AHttpOptions> configure) =>
        services.Configure(configure);

    /// <summary>
    /// Stores push notification secrets (token, credentials) with ASP.NET Core Data Protection instead of the default
    /// per-process key. Configure the key ring (persistence, application name) with <c>AddDataProtection()</c>.
    /// </summary>
    public static IServiceCollection AddA2ADataProtectionPushSecrets(this IServiceCollection services)
    {
        services.AddDataProtection();
        services.Replace(ServiceDescriptor.Singleton<IPushSecretProtector, DataProtectionPushSecretProtector>());
        return services;
    }
}

/// <summary>Extensions that map the A2A endpoints and the Agent Card into an ASP.NET Core app.</summary>
public static partial class A2AEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps both bindings under one base URL: JSON-RPC at <c>POST {pattern}</c> and HTTP+JSON at <c>{pattern}/message:send</c>,
    /// <c>{pattern}/tasks/...</c>, etc. Declare both in the Agent Card with the same URL. Every endpoint is inside the
    /// returned group, so call <c>RequireAuthorization()</c> (or rate limiting, CORS) on it, or set
    /// <see cref="A2AEndpointOptions.AuthorizationPolicy"/>. If no endpoint authorization is set and
    /// <see cref="A2AServerOptions.AllowAnonymous"/> is false, the app fails at startup. Unless
    /// <see cref="A2AEndpointOptions.MapAgentCard"/> is false, it also maps the public Agent Card (and JWKS) as
    /// <see cref="MapAgentCard"/> does, on <paramref name="endpoints"/> and outside the returned group, so authorization on the
    /// group never applies to the card. The card is mapped once per route builder, however often this is called. When
    /// <paramref name="endpoints"/> is a <see cref="RouteGroupBuilder"/> (for example <c>app.MapGroup("/api")</c>) the card is not
    /// mapped, because its path is relative to the origin: call <c>app.MapAgentCard()</c> on the application root.
    /// </summary>
    /// <remarks>
    /// Tenants: with a <c>{tenant}</c> segment in <paramref name="pattern"/> (for example <c>"/{tenant}/a2a"</c>) the route
    /// value sets the tenant. With a plain pattern, every route also has a <c>{pattern}/{tenant}</c> variant
    /// (<c>POST {pattern}/{tenant}</c> for JSON-RPC). Requires <see cref="A2AServiceCollectionExtensions.AddA2AServerAspNetCore"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><c>services.AddA2AServerAspNetCore()</c> was not called.</exception>
    public static RouteGroupBuilder MapA2A(this IEndpointRouteBuilder endpoints, string pattern = "/",
        Action<A2AEndpointOptions>? configure = null)
    {
        RequireRegistration(endpoints);
        var options = new A2AEndpointOptions();
        configure?.Invoke(options);
        var group = endpoints.MapGroup(pattern);
        var tenantInPattern = HasTenantSegment(pattern);
        MapJsonRpc(endpoints, group, tenantInPattern);
        ActivatorUtilities.CreateInstance<RestEndpoint>(endpoints.ServiceProvider).Map(group, tenantInPattern);
        Record(endpoints, ProtocolBindings.JsonRpc, pattern);
        Record(endpoints, ProtocolBindings.HttpJson, pattern);
        ApplyRateLimiting(endpoints, group);
        if (options.AuthorizationPolicy is { } policy) group.RequireAuthorization(policy);
        if (options.MapAgentCard && endpoints is not RouteGroupBuilder) MapAgentCard(endpoints);
        return group;
    }

    /// <summary>
    /// Maps only the JSON-RPC binding at <c>POST {pattern}</c> (and <c>POST {pattern}/{tenant}</c> unless the pattern has
    /// a <c>{tenant}</c> segment). Requires <see cref="A2AServiceCollectionExtensions.AddA2AServerAspNetCore"/>.
    /// </summary>
    public static RouteGroupBuilder MapA2AJsonRpc(this IEndpointRouteBuilder endpoints, string pattern = "/")
    {
        RequireRegistration(endpoints);
        var group = endpoints.MapGroup(pattern);
        MapJsonRpc(endpoints, group, HasTenantSegment(pattern));
        Record(endpoints, ProtocolBindings.JsonRpc, pattern);
        ApplyRateLimiting(endpoints, group);
        return group;
    }

    /// <summary>
    /// Maps only the HTTP+JSON binding under <paramref name="prefix"/>. Requires
    /// <see cref="A2AServiceCollectionExtensions.AddA2AServerAspNetCore"/>.
    /// </summary>
    public static RouteGroupBuilder MapA2AHttpJson(this IEndpointRouteBuilder endpoints, string prefix = "/")
    {
        RequireRegistration(endpoints);
        var group = endpoints.MapGroup(prefix);
        ActivatorUtilities.CreateInstance<RestEndpoint>(endpoints.ServiceProvider).Map(group, HasTenantSegment(prefix));
        Record(endpoints, ProtocolBindings.HttpJson, prefix);
        ApplyRateLimiting(endpoints, group);
        return group;
    }

    private static void MapJsonRpc(IEndpointRouteBuilder endpoints, RouteGroupBuilder group, bool tenantInPattern)
    {
        var rpc = ActivatorUtilities.CreateInstance<JsonRpcEndpoint>(endpoints.ServiceProvider);
        group.MapPost("", rpc.HandleAsync).WithDisplayName(JsonRpcEndpoint.DisplayName);
        if (!tenantInPattern) group.MapPost("/{tenant}", rpc.HandleAsync).WithDisplayName(JsonRpcEndpoint.DisplayName + " (tenant)");
    }

    /// <summary>Applies the <c>"a2a"</c> rate limit policy when it is registered and <see cref="A2AServerOptions.EnableRateLimiting"/> is true.</summary>
    private static void ApplyRateLimiting(IEndpointRouteBuilder endpoints, RouteGroupBuilder group)
    {
        var services = endpoints.ServiceProvider;
        if (services.GetService<A2ARateLimitMarker>() is null) return;
        if (!services.GetRequiredService<Microsoft.Extensions.Options.IOptions<A2AServerOptions>>().Value.EnableRateLimiting) return;
        group.RequireRateLimiting(A2ARateLimitingExtensions.PolicyName);
    }

    /// <summary>
    /// Puts endpoints of another binding (for example gRPC) under the same fail-closed startup check as <see cref="MapA2A"/>:
    /// they must carry an authorization policy unless <see cref="A2AServerOptions.AllowAnonymous"/> is true. Their display
    /// names get the prefix <c>"A2A {bindingName} "</c>. Requires <see cref="A2AServiceCollectionExtensions.AddA2AServerAspNetCore"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>services.AddA2AServerAspNetCore()</c> was not called.</exception>
    public static TBuilder WithA2AStartupCheck<TBuilder>(this TBuilder builder, IEndpointRouteBuilder endpoints, string bindingName)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingName);
        RequireRegistration(endpoints);
        builder.Add(b => b.DisplayName = $"A2A {bindingName} {b.DisplayName}");
        return builder;
    }

    /// <summary>
    /// Tells the Agent Card completion that <paramref name="binding"/> (a <see cref="ProtocolBindings"/> value, for example
    /// <see cref="ProtocolBindings.Grpc"/>) is mapped at <paramref name="pattern"/>, so an Agent Card without
    /// <see cref="AgentCard.SupportedInterfaces"/> lists it. For binding packages: <c>MapA2A</c>, <c>MapA2AJsonRpc</c>,
    /// <c>MapA2AHttpJson</c> and <c>MapA2AGrpc</c> call it; application code does not need to. Patterns with a <c>{</c> (a tenant
    /// template) are not listed, and nothing is recorded when <paramref name="endpoints"/> is a <see cref="RouteGroupBuilder"/>,
    /// because the group's prefix is not known here.
    /// </summary>
    /// <param name="endpoints">The route builder the binding is mapped on.</param>
    /// <param name="binding">The protocol binding name.</param>
    /// <param name="pattern">The mapped path pattern; use <c>"/"</c> for a binding served at the origin.</param>
    /// <exception cref="InvalidOperationException"><c>services.AddA2AServerAspNetCore()</c> was not called.</exception>
    public static void RegisterA2ABinding(this IEndpointRouteBuilder endpoints, string binding, string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding);
        ArgumentNullException.ThrowIfNull(pattern);
        RequireRegistration(endpoints);
        Record(endpoints, binding, pattern);
    }

    private static void Record(IEndpointRouteBuilder endpoints, string binding, string pattern)
    {
        if (endpoints is RouteGroupBuilder || pattern.Contains('{', StringComparison.Ordinal)) return;
        endpoints.ServiceProvider.GetRequiredService<A2AEndpointRegistry>().AddBinding(binding, pattern);
    }

    private static bool HasTenantSegment(string pattern) => TenantSegment().IsMatch(pattern);

    [GeneratedRegex(@"\{\*?tenant[}:=?]")]
    private static partial Regex TenantSegment();

    private static void RequireRegistration(IEndpointRouteBuilder endpoints)
    {
        var registry = endpoints.ServiceProvider.GetService<A2AEndpointRegistry>()
            ?? throw new InvalidOperationException(
                "Call services.AddA2AServerAspNetCore() before MapA2A(...). It registers the startup check that refuses to run A2A endpoints without an authorization policy.");
        registry.Add(endpoints.DataSources);
    }

    /// <summary>
    /// Serves the public Agent Card at <c>/.well-known/agent-card.json</c> (anonymous, cacheable). When a signing key is
    /// configured, the card is signed and the public key is served at <see cref="A2AHttpOptions.JwksPath"/>.
    /// <see cref="MapA2A"/> already does this; call it for hosts that map the card without <c>MapA2A</c>.
    /// </summary>
    /// <returns>
    /// A convention builder for the card endpoints. It never double-maps: when the card is already mapped on
    /// <paramref name="endpoints"/> (by an earlier <see cref="MapA2A"/> or <c>MapAgentCard</c>), nothing is mapped and the result
    /// is a no-op builder whose conventions are ignored.
    /// </returns>
    /// <exception cref="InvalidOperationException"><paramref name="endpoints"/> is a <see cref="RouteGroupBuilder"/>.</exception>
    public static IEndpointConventionBuilder MapAgentCard(this IEndpointRouteBuilder endpoints)
    {
        if (endpoints is RouteGroupBuilder)
            throw new InvalidOperationException("Map the Agent Card on the application root (app.MapAgentCard()); the /.well-known path is relative to the origin.");
        if (endpoints.ServiceProvider.GetService<A2AEndpointRegistry>() is { } registry && !registry.TryMarkCardMapped(endpoints.DataSources))
            return NoOpConventionBuilder.Instance;
        var card = ActivatorUtilities.CreateInstance<AgentCardEndpoint>(endpoints.ServiceProvider);
        var jwksPath = endpoints.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<A2AHttpOptions>>().Value.JwksPath;
        var group = endpoints.MapGroup("");
        group.MapGet(A2AProtocol.WellKnownAgentCardPath, card.GetCardAsync).WithDisplayName("A2A Agent Card");
        group.MapGet(jwksPath, card.GetJwksAsync).WithDisplayName("A2A Agent Card JWKS");
        group.AllowAnonymous();
        return group;
    }

    private sealed class NoOpConventionBuilder : IEndpointConventionBuilder
    {
        public static readonly NoOpConventionBuilder Instance = new();
        public void Add(Action<EndpointBuilder> convention) { }
    }
}
