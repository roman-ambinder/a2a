using System.Text.RegularExpressions;
using A2A.Server.AspNetCore.Operations;
using A2A.Server.Push;
using A2A.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>Dependency injection extensions for the ASP.NET Core side of the A2A server.</summary>
public static class A2AServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the ASP.NET Core side of the A2A server: the <see cref="Operations.A2AOperationTable" /> the endpoints
    ///     dispatch
    ///     through, the default <see cref="ITenantResolver" /> (<see cref="RouteTenantResolver" />),
    ///     the startup check that fails the app when A2A endpoints have no authorization policy (see
    ///     <see cref="A2AServerOptions.AllowAnonymous" />), and the default rate limit policy (see
    ///     <see cref="ConfigureRateLimiting" />; no middleware is needed). Call it once next to <c>AddA2AServer(...)</c>. <c>MapA2A</c> throws
    ///     <see cref="InvalidOperationException" /> if it was not called, so the check cannot be skipped by mistake.
    ///     It replaces the <see cref="IAgentCardProvider" /> registration (with the one that completes an empty card):
    ///     register a
    ///     custom provider after this call.
    /// </summary>
    public static IServiceCollection AddA2AServerAspNetCore(this IServiceCollection services)
    {
        services.TryAddSingleton<A2AEndpointRegistry>();
        services.Replace(ServiceDescriptor.Singleton<IAgentCardProvider, AgentCardCompleter>());
        services.TryAddSingleton<A2AOperationTable>();
        services.TryAddSingleton<ITenantResolver, RouteTenantResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, A2AStartupValidator>());
        services.AddA2ARateLimiting();
        return services;
    }

    /// <summary>
    ///     Registers the A2A server with its ASP.NET Core endpoints and returns the builder: the agent, the card, options
    ///     (<see cref="A2AServerBuilder.Configure" />, <see cref="ConfigureHttp" />, <see cref="ConfigureRateLimiting" />),
    ///     stores, filters and authentication all go on it. <paramref name="configure" /> runs on the builder at once; you
    ///     can also chain calls on the result. Call it once.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="services" /> is null.</exception>
    public static A2AServerBuilder AddA2A(this IServiceCollection services, Action<A2AServerBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var builder = services.AddA2AServer();
        services.AddA2AServerAspNetCore();
        configure?.Invoke(builder);
        return builder;
    }

    /// <summary>
    ///     Configures HTTP-level settings: the public base URL, Agent Card signing and caching, SSE keep-alive. Can be called
    ///     more than once.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static A2AServerBuilder ConfigureHttp(this A2AServerBuilder builder, Action<A2AHttpOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.Configure(configure);
        return builder;
    }

    /// <summary>
    ///     Configures the per-caller rate limit of the A2A endpoints (on by default: 20 requests burst, 60 per minute), or
    ///     turns it off with <see cref="A2ARateLimitOptions.Enabled" /> = false. The bucket is per tenant and caller: the
    ///     principal, or for an anonymous caller its remote IP address. A rejected request gets HTTP 429 with a
    ///     <c>Retry-After</c> header and the <c>RATE_LIMITED</c> error, in the format of the binding.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The limit is taken after authentication and before the authorization decision, so a request with wrong
    ///         credentials counts too: it is checked by the authorization middleware's result handler. An endpoint that has
    ///         no authorization policy (anonymous callers allowed) is checked when the endpoint runs. A request is counted
    ///         once. A host that replaces <see cref="Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler" /> after this call keeps the
    ///         limit, but requests that fail authorization are then not counted.
    ///     </para>
    ///     <para>
    ///         Behind a reverse proxy or load balancer every connection comes from the proxy, so all anonymous callers would
    ///         share the proxy's bucket. Enable the ForwardedHeaders middleware (<c>app.UseForwardedHeaders()</c> with the
    ///         proxy listed in <c>ForwardedHeadersOptions.KnownProxies</c> or <c>KnownNetworks</c>), so the remote address is
    ///         the client's.
    ///     </para>
    ///     To replace the policy, register your own as a keyed service:
    ///     <c>services.AddKeyedSingleton&lt;IRateLimiterPolicy&lt;string&gt;, MyPolicy&gt;(A2ARateLimitingExtensions.PolicyName)</c>.
    ///     This is separate from the ASP.NET Core rate limiter middleware: a host that uses <c>UseRateLimiter()</c> for its
    ///     own endpoints does not count A2A requests twice. To drop rate limiting, set
    ///     <see cref="A2ARateLimitOptions.Enabled" /> to false.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static A2AServerBuilder ConfigureRateLimiting(this A2AServerBuilder builder,
        Action<A2ARateLimitOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.AddA2ARateLimiting(configure);
        return builder;
    }

    /// <summary>
    ///     Stores push notification secrets (token, credentials) with ASP.NET Core Data Protection instead of the default
    ///     per-process key. Configure the key ring (persistence, application name) with <c>AddDataProtection()</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is null.</exception>
    public static A2AServerBuilder AddDataProtectionPushSecrets(this A2AServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddA2ADataProtectionPushSecrets();
        return builder;
    }

    internal static IServiceCollection ConfigureA2AHttp(this IServiceCollection services,
        Action<A2AHttpOptions> configure)
    {
        return services.Configure(configure);
    }

    internal static IServiceCollection AddA2ADataProtectionPushSecrets(this IServiceCollection services)
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
    ///     Maps both bindings under one base URL: JSON-RPC at <c>POST {pattern}</c> and HTTP+JSON at
    ///     <c>{pattern}/message:send</c>,
    ///     <c>{pattern}/tasks/...</c>, etc. Declare both in the Agent Card with the same URL. Every endpoint is inside the
    ///     returned group, so call <c>RequireAuthorization()</c> (or rate limiting, CORS) on it, or set
    ///     <see cref="A2AEndpointOptions.AuthorizationPolicy" />. If no endpoint authorization is set and
    ///     <see cref="A2AServerOptions.AllowAnonymous" /> is false, the app fails at startup. Unless
    ///     <see cref="A2AEndpointOptions.MapAgentCard" /> is false, it also maps the public Agent Card (and JWKS) as
    ///     <see cref="MapAgentCard" /> does, on <paramref name="endpoints" /> and outside the returned group, so authorization
    ///     on the
    ///     group never applies to the card. The card is mapped once per route builder, however often this is called. When
    ///     <paramref name="endpoints" /> is a <see cref="RouteGroupBuilder" /> (for example <c>app.MapGroup("/api")</c>) the
    ///     card is not
    ///     mapped, because its path is relative to the origin: call <c>app.MapAgentCard()</c> on the application root.
    /// </summary>
    /// <remarks>
    ///     Tenants: with a <c>{tenant}</c> segment in <paramref name="pattern" /> (for example <c>"/{tenant}/a2a"</c>) the
    ///     route
    ///     value sets the tenant. With a plain pattern, every route also has a <c>{pattern}/{tenant}</c> variant
    ///     (<c>POST {pattern}/{tenant}</c> for JSON-RPC). Requires
    ///     <see cref="A2AServiceCollectionExtensions.AddA2AServerAspNetCore" />.
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
        var adapter = options.ProtocolAdapter;
        MapJsonRpc(endpoints, group, tenantInPattern, adapter);
        ActivatorUtilities.CreateInstance<RestEndpoint>(endpoints.ServiceProvider).Map(group, tenantInPattern);
        if (adapter is not null)
        {
            adapter.MapRoutes(endpoints.ServiceProvider, group, tenantInPattern);
            if (adapter.CardBodyTransform is { } transform)
                endpoints.ServiceProvider.GetRequiredService<A2AEndpointRegistry>().CardBodyTransform = transform;
        }

        Record(endpoints, ProtocolBindings.JsonRpc, pattern);
        Record(endpoints, ProtocolBindings.HttpJson, pattern);
        ApplyRateLimiting(endpoints, group);
        if (options.AuthorizationPolicy is { } policy) group.RequireAuthorization(policy);
        if (options.MapAgentCard && endpoints is not RouteGroupBuilder) endpoints.MapAgentCard();
        return group;
    }

    /// <summary>
    ///     Maps only the JSON-RPC binding at <c>POST {pattern}</c> (and <c>POST {pattern}/{tenant}</c> unless the pattern has
    ///     a <c>{tenant}</c> segment). Requires <see cref="A2AServiceCollectionExtensions.AddA2AServerAspNetCore" />.
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
    ///     Maps only the HTTP+JSON binding under <paramref name="prefix" />. Requires
    ///     <see cref="A2AServiceCollectionExtensions.AddA2AServerAspNetCore" />.
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

    /// <summary>
    ///     Maps <c>GET {pattern}/artifacts/{artifactTenant}/{taskId}/{objectId}</c>, which serves the bytes behind the
    ///     artifact links
    ///     made when an <see cref="Storage.ArtifactObjectStore" /> is registered. The caller needs the same credentials as for
    ///     A2A calls: the
    ///     read is authorized as <c>GetTask</c> and the task is loaded with the caller's key, so another owner or tenant, a
    ///     deleted
    ///     task or a wrong id all answer 404. Responses are <c>Content-Disposition: attachment</c> with <c>nosniff</c> and
    ///     <c>Cache-Control: private, no-store</c>. With <see cref="ArtifactDelivery.Redirect" /> the answer is a 302 to a
    ///     presigned URL
    ///     when the store has one; otherwise the bytes stream through the server, which honours a single <c>Range</c>. Like
    ///     <see cref="MapA2A" />, the route is covered by the startup authorization check and the <c>"a2a"</c> rate limit
    ///     policy, so call
    ///     <c>RequireAuthorization()</c> on the returned group. Map it at the same <paramref name="pattern" /> as
    ///     <see cref="MapA2A" />,
    ///     and give the agent card the origin that the links use, so clients send credentials. Requires
    ///     <see cref="A2AServiceCollectionExtensions.AddA2AServerAspNetCore" />.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>services.AddA2AServerAspNetCore()</c> was not called.</exception>
    public static RouteGroupBuilder MapA2AArtifacts(this IEndpointRouteBuilder endpoints, string pattern = "/")
    {
        RequireRegistration(endpoints);
        var group = endpoints.MapGroup(pattern);
        var artifacts = ActivatorUtilities.CreateInstance<ArtifactEndpoint>(endpoints.ServiceProvider);
        group.MapGet("/artifacts/{artifactTenant}/{taskId}/{objectId}", artifacts.GetAsync)
            .WithDisplayName(ArtifactEndpoint.DisplayName);
        ApplyRateLimiting(endpoints, group);
        return group;
    }

    /// <summary>
    ///     Maps <c>POST {pattern}/uploads/{tenant or _}</c> and <c>GET {pattern}/uploads/{tenant or _}/{objectId}</c>, which
    ///     let a client
    ///     upload a large file to the <see cref="Storage.ArtifactObjectStore" /> first and then send it in a message as an
    ///     ordinary <c>url</c>
    ///     part (see <see cref="ArtifactUploadOptions" />). The POST body is the raw file, streamed to the store and never
    ///     buffered, and
    ///     may be larger than <see cref="A2ALimits.MaxRequestBodyBytes" />; <c>Content-Type</c> is the media type and
    ///     <c>Content-Disposition: attachment; filename*=...</c> the optional file name. The answer is 201 with the JSON of
    ///     the
    ///     <c>url</c> part (<c>sha256</c> and <c>sizeBytes</c> in its metadata) and a <c>Location</c> header with the link.
    ///     The owner reads the
    ///     file back with the GET; every miss, also another owner's file, is 404. The caller needs the same credentials as for
    ///     A2A
    ///     calls and must be authenticated, also when <see cref="A2AServerOptions.AllowAnonymous" /> is set. The operations
    ///     run through
    ///     the pipeline as <c>a2a.files.upload</c> and <c>a2a.files.read</c>, so a custom
    ///     <see cref="Authorization.A2AAuthorizationHandler" />
    ///     must allow those two names. Over <see cref="ArtifactUploadOptions.MaxUploadBytes" /> the answer is 413; over the
    ///     caller's quota,
    ///     or with too many uploads running, 429 with <c>Retry-After</c>. Like <see cref="MapA2A" />, the routes are covered
    ///     by the startup
    ///     authorization check and the <c>"a2a"</c> rate limit policy, so call <c>RequireAuthorization()</c> on the returned
    ///     group. Map it at the
    ///     same <paramref name="pattern" /> as <see cref="MapA2A" />, and give the agent card the origin that the links use,
    ///     so clients send
    ///     credentials. Requires <see cref="A2AServiceCollectionExtensions.AddA2AServerAspNetCore" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     <c>services.AddA2AServerAspNetCore()</c> was not called, <see cref="ArtifactUploadOptions.Enabled" /> is false, no
    ///     <see cref="Storage.ArtifactObjectStore" /> is registered, or an upload option is out of range.
    /// </exception>
    public static RouteGroupBuilder MapA2AUploads(this IEndpointRouteBuilder endpoints, string pattern = "/")
    {
        RequireRegistration(endpoints);
        var services = endpoints.ServiceProvider;
        var options = services.GetRequiredService<IOptions<A2AServerOptions>>().Value;
        if (!options.ArtifactStorage.Uploads.Enabled)
            throw new InvalidOperationException(
                "MapA2AUploads needs A2AServerOptions.ArtifactStorage.Uploads.Enabled = true.");
        if (services.GetService<ArtifactObjectStore>() is null)
            throw new InvalidOperationException(
                "MapA2AUploads needs an ArtifactObjectStore: register one (the S3 or Azure Blob store, or InMemoryArtifactObjectStore for tests).");
        // The handler checks the upload options when it is built (InvalidOperationException for a value out of range), so resolve it now.
        services.GetRequiredService<A2ARequestHandler>();
        var group = endpoints.MapGroup(pattern);
        var uploads = ActivatorUtilities.CreateInstance<UploadEndpoint>(services);
        group.MapPost("/uploads/{uploadTenant}", uploads.PostAsync).WithDisplayName(UploadEndpoint.DisplayName);
        group.MapGet("/uploads/{uploadTenant}/{objectId}", uploads.GetAsync)
            .WithDisplayName(UploadEndpoint.DisplayName + " (read)");
        ApplyRateLimiting(endpoints, group);
        return group;
    }

    private static void MapJsonRpc(IEndpointRouteBuilder endpoints, RouteGroupBuilder group, bool tenantInPattern,
        IA2AProtocolAdapter? adapter = null)
    {
        var rpc = ActivatorUtilities.CreateInstance<JsonRpcEndpoint>(endpoints.ServiceProvider);
        RequestDelegate handle = rpc.HandleAsync;
        if (adapter is not null) handle = adapter.WrapJsonRpc(endpoints.ServiceProvider, handle);
        group.MapPost("", handle).WithDisplayName(JsonRpcEndpoint.DisplayName);
        if (!tenantInPattern)
            group.MapPost("/{tenant}", handle).WithDisplayName(JsonRpcEndpoint.DisplayName + " (tenant)");
    }

    /// <summary>
    ///     Puts the endpoints under the per-caller rate limit when it is registered and
    ///     <see cref="A2ARateLimitOptions.Enabled" /> is true: the authorization result handler takes it for endpoints with
    ///     an authorization policy, and the endpoint itself for the others.
    /// </summary>
    private static void ApplyRateLimiting<TBuilder>(IEndpointRouteBuilder endpoints, TBuilder group)
        where TBuilder : IEndpointConventionBuilder
    {
        // Every Map* method passes here, so this is also where declared card schemes protect endpoints by default.
        A2ACardAuthenticationExtensions.ApplyDefault(endpoints, group);
        var services = endpoints.ServiceProvider;
        if (services.GetService<A2ARateLimitMarker>() is null) return;
        if (!services.GetRequiredService<IOptions<A2ARateLimitOptions>>().Value.Enabled) return;
        group.WithMetadata(A2ARateLimitMetadata.Instance);
        group.Add(b =>
        {
            if (b.RequestDelegate is { } inner) b.RequestDelegate = A2ARateLimitGate.Wrap(inner);
        });
    }

    /// <summary>
    ///     Puts endpoints of another binding under the same checks as <see cref="MapA2A" />: the fail-closed startup check
    ///     (they
    ///     must carry an authorization policy unless <see cref="A2AServerOptions.AllowAnonymous" /> is true) and the
    ///     per-caller rate limit (register an <see cref="IA2ARejectionWriter" /> to answer its rejections in the binding's own
    ///     format).
    ///     Their display names get the prefix <c>"A2A {bindingName} "</c>. Requires
    ///     <see cref="A2AServiceCollectionExtensions.AddA2AServerAspNetCore" />. The built-in gRPC binding
    ///     (<c>MapA2AGrpc</c>) is built on it; do not call it again on that binding's endpoints.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>services.AddA2AServerAspNetCore()</c> was not called.</exception>
    public static TBuilder WithA2AStartupCheck<TBuilder>(this TBuilder builder, IEndpointRouteBuilder endpoints,
        string bindingName)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingName);
        RequireRegistration(endpoints);
        builder.Add(b => b.DisplayName = $"A2A {bindingName} {b.DisplayName}");
        ApplyRateLimiting(endpoints, builder);
        return builder;
    }

    /// <summary>
    ///     Tells the Agent Card completion that <paramref name="binding" /> (a <see cref="ProtocolBindings" /> value, for
    ///     example
    ///     <see cref="ProtocolBindings.Grpc" />) is mapped at <paramref name="pattern" />, so an Agent Card without
    ///     <see cref="AgentCard.SupportedInterfaces" /> lists it. For binding packages: <c>MapA2A</c>, <c>MapA2AJsonRpc</c>,
    ///     <c>MapA2AHttpJson</c> and <c>MapA2AGrpc</c> call it; application code does not need to. Patterns with a <c>{</c> (a
    ///     tenant
    ///     template) are not listed, and nothing is recorded when <paramref name="endpoints" /> is a
    ///     <see cref="RouteGroupBuilder" />,
    ///     because the group's prefix is not known here.
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
        if (endpoints is RouteGroupBuilder) return;
        var registry = endpoints.ServiceProvider.GetRequiredService<A2AEndpointRegistry>();
        if (pattern.Contains('{', StringComparison.Ordinal)) registry.AddTemplateBinding(binding, pattern);
        else registry.AddBinding(binding, pattern);
    }

    private static bool HasTenantSegment(string pattern)
    {
        return TenantSegment().IsMatch(pattern);
    }

    [GeneratedRegex(@"\{\*?tenant[}:=?]")]
    private static partial Regex TenantSegment();

    /// <summary>
    ///     Records the route builder's data sources for the startup check. Throws when <c>AddA2AServerAspNetCore()</c> was not
    ///     called.
    ///     Every A2A mapping method (also the gRPC binding's) calls it.
    /// </summary>
    internal static void RequireRegistration(IEndpointRouteBuilder endpoints)
    {
        var registry = endpoints.ServiceProvider.GetService<A2AEndpointRegistry>()
                       ?? throw new InvalidOperationException(
                           "Call services.AddA2AServerAspNetCore() before MapA2A(...). It registers the startup check that refuses to run A2A endpoints without an authorization policy.");
        registry.Add(endpoints.DataSources);
    }

    /// <summary>
    ///     Serves the public Agent Card at <c>/.well-known/agent-card.json</c> and each tenant's card at
    ///     <c>/{tenant}/.well-known/agent-card.json</c> (anonymous, cacheable). The card comes from the
    ///     <see cref="IAgentCardProvider" />
    ///     for the tenant the <see cref="ITenantResolver" /> finds (with a <see cref="Agents.TenantCardProvider" />, each
    ///     tenant's
    ///     completed card; without one, the host's card for every tenant); a tenant with no card is HTTP 404. When a signing
    ///     key is
    ///     configured (the provider's for the tenant, else <see cref="A2AHttpOptions.SigningKey" />), the card is signed and
    ///     the
    ///     public key is served at <see cref="A2AHttpOptions.JwksPath" />, and at <c>/{tenant}</c> followed by that path for a
    ///     tenant.
    ///     <see cref="MapA2A" /> already does this; call it for hosts that map the card without <c>MapA2A</c>.
    /// </summary>
    /// <returns>
    ///     A convention builder for the card endpoints. It never double-maps: when the card is already mapped on
    ///     <paramref name="endpoints" /> (by an earlier <see cref="MapA2A" /> or <c>MapAgentCard</c>), nothing is mapped and
    ///     the result
    ///     is a no-op builder whose conventions are ignored.
    /// </returns>
    /// <exception cref="InvalidOperationException"><paramref name="endpoints" /> is a <see cref="RouteGroupBuilder" />.</exception>
    public static IEndpointConventionBuilder MapAgentCard(this IEndpointRouteBuilder endpoints)
    {
        if (endpoints is RouteGroupBuilder)
            throw new InvalidOperationException(
                "Map the Agent Card on the application root (app.MapAgentCard()); the /.well-known path is relative to the origin.");
        if (endpoints.ServiceProvider.GetService<A2AEndpointRegistry>() is { } registry &&
            !registry.TryMarkCardMapped(endpoints.DataSources))
            return NoOpConventionBuilder.Instance;
        var card = ActivatorUtilities.CreateInstance<AgentCardEndpoint>(endpoints.ServiceProvider);
        var jwksPath = endpoints.ServiceProvider.GetRequiredService<IOptions<A2AHttpOptions>>().Value.JwksPath;
        var group = endpoints.MapGroup("");
        group.MapGet(A2AProtocol.WellKnownAgentCardPath, card.GetCardAsync).WithDisplayName("A2A Agent Card");
        group.MapGet(jwksPath, card.GetJwksAsync).WithDisplayName("A2A Agent Card JWKS");
        group.MapGet("/{tenant}" + A2AProtocol.WellKnownAgentCardPath, card.GetCardAsync)
            .WithDisplayName("A2A Agent Card (tenant)");
        group.MapGet("/{tenant}/" + jwksPath.TrimStart('/'), card.GetJwksAsync)
            .WithDisplayName("A2A Agent Card JWKS (tenant)");
        group.AllowAnonymous();
        return group;
    }

    private sealed class NoOpConventionBuilder : IEndpointConventionBuilder
    {
        public static readonly NoOpConventionBuilder Instance = new();

        public void Add(Action<EndpointBuilder> convention)
        {
        }
    }
}