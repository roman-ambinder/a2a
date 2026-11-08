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

public static class A2AServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ASP.NET Core side of the A2A server: the default <see cref="ITenantResolver"/> (<see cref="RouteTenantResolver"/>)
    /// the startup check that fails the app when A2A endpoints have no authorization policy (see
    /// <see cref="A2AServerOptions.AllowAnonymous"/>), and the default rate limit policy (see <see cref="A2ARateLimitingExtensions.AddA2ARateLimiting"/>; the host
    /// adds the middleware with <c>app.UseA2A()</c>). Call it once next to <c>AddA2AServer(...)</c>. <c>MapA2A</c> throws
    /// <see cref="InvalidOperationException"/> if it was not called, so the check cannot be skipped by mistake.
    /// </summary>
    public static IServiceCollection AddA2AServerAspNetCore(this IServiceCollection services)
    {
        services.TryAddSingleton<A2AEndpointRegistry>();
        services.TryAddSingleton<ITenantResolver, RouteTenantResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, A2AStartupValidator>());
        services.AddA2ARateLimiting();
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

public static partial class A2AEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps both bindings under one base URL: JSON-RPC at <c>POST {pattern}</c> and HTTP+JSON at <c>{pattern}/message:send</c>,
    /// <c>{pattern}/tasks/...</c>, etc. Declare both in the Agent Card with the same URL. Every endpoint is inside the
    /// returned group, so call <c>RequireAuthorization()</c> (or rate limiting, CORS) on it. If no endpoint authorization
    /// is set and <see cref="A2AServerOptions.AllowAnonymous"/> is false, the app fails at startup. The Agent Card itself is
    /// mapped separately with <see cref="MapAgentCard"/>.
    /// </summary>
    /// <remarks>
    /// Tenants: with a <c>{tenant}</c> segment in <paramref name="pattern"/> (for example <c>"/{tenant}/a2a"</c>) the route
    /// value sets the tenant. With a plain pattern, every route also has a <c>{pattern}/{tenant}</c> variant
    /// (<c>POST {pattern}/{tenant}</c> for JSON-RPC). Requires <see cref="A2AServiceCollectionExtensions.AddA2AServerAspNetCore"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><c>services.AddA2AServerAspNetCore()</c> was not called.</exception>
    public static RouteGroupBuilder MapA2A(this IEndpointRouteBuilder endpoints, string pattern = "/")
    {
        RequireRegistration(endpoints);
        var group = endpoints.MapGroup(pattern);
        var tenantInPattern = HasTenantSegment(pattern);
        MapJsonRpc(endpoints, group, tenantInPattern);
        ActivatorUtilities.CreateInstance<RestEndpoint>(endpoints.ServiceProvider).Map(group, tenantInPattern);
        ApplyRateLimiting(endpoints, group);
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
    internal static void ApplyRateLimiting<TBuilder>(IEndpointRouteBuilder endpoints, TBuilder group) where TBuilder : IEndpointConventionBuilder
    {
        var services = endpoints.ServiceProvider;
        if (services.GetService<A2ARateLimitMarker>() is null) return;
        if (!services.GetRequiredService<Microsoft.Extensions.Options.IOptions<A2AServerOptions>>().Value.EnableRateLimiting) return;
        group.RequireRateLimiting(A2ARateLimitingExtensions.PolicyName);
    }

    /// <summary>
    /// Puts endpoints of another binding under the same checks as <see cref="MapA2A"/>: the fail-closed startup check (they
    /// must carry an authorization policy unless <see cref="A2AServerOptions.AllowAnonymous"/> is true) and the <c>"a2a"</c>
    /// rate limit policy (so the host must call <c>app.UseA2A()</c> unless <see cref="A2AServerOptions.EnableRateLimiting"/>
    /// is false). Their display names get the prefix <c>"A2A {bindingName} "</c>. Requires
    /// <see cref="A2AServiceCollectionExtensions.AddA2AServerAspNetCore"/>. The built-in gRPC binding
    /// (<c>MapA2AGrpc</c>) already applies these checks; do not call this on it.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>services.AddA2AServerAspNetCore()</c> was not called.</exception>
    public static TBuilder WithA2AStartupCheck<TBuilder>(this TBuilder builder, IEndpointRouteBuilder endpoints, string bindingName)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingName);
        RequireRegistration(endpoints);
        builder.Add(b => b.DisplayName = $"A2A {bindingName} {b.DisplayName}");
        ApplyRateLimiting(endpoints, builder);
        return builder;
    }

    private static bool HasTenantSegment(string pattern) => TenantSegment().IsMatch(pattern);

    [GeneratedRegex(@"\{\*?tenant[}:=?]")]
    private static partial Regex TenantSegment();

    /// <summary>
    /// Records the route builder's data sources for the startup check. Throws when <c>AddA2AServerAspNetCore()</c> was not called.
    /// Every A2A mapping method (also the gRPC binding's) calls it.
    /// </summary>
    internal static void RequireRegistration(IEndpointRouteBuilder endpoints)
    {
        var registry = endpoints.ServiceProvider.GetService<A2AEndpointRegistry>()
            ?? throw new InvalidOperationException(
                "Call services.AddA2AServerAspNetCore() before MapA2A(...). It registers the startup check that refuses to run A2A endpoints without an authorization policy.");
        registry.Add(endpoints.DataSources);
    }

    /// <summary>
    /// Serves the public Agent Card at <c>/.well-known/agent-card.json</c> and each tenant's card at
    /// <c>/{tenant}/.well-known/agent-card.json</c> (anonymous, cacheable). The card comes from the
    /// <see cref="Agents.AgentCardProvider"/> for the tenant the <see cref="ITenantResolver"/> finds (without a provider, the
    /// configured card for every tenant); a tenant with no card is HTTP 404. When a signing key is configured (the provider's
    /// for the tenant, else <see cref="A2AHttpOptions.SigningKey"/>), the card is signed and the public key is served at
    /// <see cref="A2AHttpOptions.JwksPath"/>, and at <c>/{tenant}</c> followed by that path for a tenant.
    /// </summary>
    public static IEndpointConventionBuilder MapAgentCard(this IEndpointRouteBuilder endpoints)
    {
        var card = ActivatorUtilities.CreateInstance<AgentCardEndpoint>(endpoints.ServiceProvider);
        var jwksPath = endpoints.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<A2AHttpOptions>>().Value.JwksPath;
        var group = endpoints.MapGroup("");
        group.MapGet(A2AProtocol.WellKnownAgentCardPath, card.GetCardAsync).WithDisplayName("A2A Agent Card");
        group.MapGet(jwksPath, card.GetJwksAsync).WithDisplayName("A2A Agent Card JWKS");
        group.MapGet("/{tenant}" + A2AProtocol.WellKnownAgentCardPath, card.GetCardAsync).WithDisplayName("A2A Agent Card (tenant)");
        group.MapGet("/{tenant}/" + jwksPath.TrimStart('/'), card.GetJwksAsync).WithDisplayName("A2A Agent Card JWKS (tenant)");
        group.AllowAnonymous();
        return group;
    }
}
