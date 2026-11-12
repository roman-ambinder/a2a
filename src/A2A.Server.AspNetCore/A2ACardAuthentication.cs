using A2A.Server.Agents;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>
///     Requires that a request is authenticated the way the Agent Card says (spec §7.4, §13.3: agents MUST authenticate
///     requests using one of the schemes declared in <c>securitySchemes</c> and <c>securityRequirements</c>).
/// </summary>
public static class A2ACardAuthenticationExtensions
{
    /// <summary>
    ///     The policy every A2A endpoint without authorization of its own gets once a scheme is declared with
    ///     <see cref="AddCardAuthentication" />, <see cref="AddBearerAuthentication" /> or
    ///     <see cref="A2AApiKeyAuthenticationExtensions.AddApiKeyAuthentication" />: the caller is authenticated by any one
    ///     of the declared schemes.
    /// </summary>
    public const string DefaultPolicyName = "A2ACardSchemes";

    /// <summary>
    ///     Declares how callers authenticate, in one place: <paramref name="scheme" /> goes on the Agent Card under
    ///     <paramref name="cardSchemeName" /> (in <c>securitySchemes</c>, and as one more way in under
    ///     <c>securityRequirements</c>), and it is proved by the ASP.NET Core authentication scheme
    ///     <paramref name="authenticationScheme" />, which you register as usual (for example <c>AddJwtBearer()</c>).
    ///     Every A2A endpoint without authorization of its own then requires a caller authenticated by one of the declared
    ///     schemes (<see cref="DefaultPolicyName" />).
    /// </summary>
    /// <param name="builder">The A2A builder.</param>
    /// <param name="cardSchemeName">The key in the card's <c>securitySchemes</c>, for example <c>bearer</c>.</param>
    /// <param name="scheme">What the card says, for example <c>SecurityScheme.Bearer()</c> or an OAuth 2.0 scheme.</param>
    /// <param name="authenticationScheme">The ASP.NET Core authentication scheme that checks it.</param>
    /// <param name="scopes">
    ///     The scopes the card's requirement lists for this scheme, or null for none. They belong to OAuth 2.0 and OpenID
    ///     Connect schemes; leave them out for other schemes. Not checked here: check them in your policies.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         Endpoints that already carry authorization (<c>RequireAuthorization(...)</c>, <c>AuthorizationPolicy</c>,
    ///         <c>AllowAnonymous</c>) keep it; the card still lists the scheme. The app refuses to start when
    ///         <paramref name="authenticationScheme" /> is not registered. A scheme the card already has under
    ///         <paramref name="cardSchemeName" /> is kept. Cards from a <see cref="TenantCardProvider" /> are not changed:
    ///         call <see cref="A2ACardSecurity.Apply" /> on them.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException">A name is blank, or <paramref name="cardSchemeName" /> is already declared.</exception>
    public static A2AServerBuilder AddCardAuthentication(this A2AServerBuilder builder, string cardSchemeName,
        SecurityScheme scheme, string authenticationScheme, IEnumerable<string>? scopes = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(cardSchemeName);
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme);
        var services = builder.Services;
        if (services.Any(d => d.ImplementationInstance is CardSchemeDeclaration c && c.CardName == cardSchemeName))
            throw new ArgumentException($"The card scheme '{cardSchemeName}' is already declared.", nameof(cardSchemeName));
        services.AddSingleton<ICardSchemeDeclaration>(new CardSchemeDeclaration(cardSchemeName, scheme, [.. scopes ?? []]));
        if (CarriesBearerToken(scheme) &&
            !services.Any(d => d.ImplementationInstance is BearerScheme b && b.Name == authenticationScheme))
            services.AddSingleton(new BearerScheme(authenticationScheme));
        AddDefaultScheme(services, authenticationScheme);
        return builder;
    }

    /// <summary>
    ///     True for card schemes whose credential is an <c>Authorization: Bearer</c> token: HTTP bearer, OAuth 2.0 and
    ///     OpenID Connect.
    /// </summary>
    private static bool CarriesBearerToken(SecurityScheme scheme)
    {
        return scheme switch
        {
            HttpAuthScheme http => string.Equals(http.Value.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase),
            OAuth2Scheme or OpenIdConnectScheme => true,
            _ => false
        };
    }

    /// <summary>
    ///     <see cref="AddCardAuthentication" /> for bearer tokens (JWT): declares an HTTP <c>bearer</c> scheme on the card,
    ///     proved by <paramref name="authenticationScheme" /> (default <c>Bearer</c>, the name <c>AddJwtBearer()</c> uses).
    /// </summary>
    /// <param name="builder">The A2A builder.</param>
    /// <param name="authenticationScheme">The ASP.NET Core authentication scheme. Default <c>Bearer</c>.</param>
    /// <param name="cardSchemeName">The key in the card's <c>securitySchemes</c>. Default <c>bearer</c>.</param>
    /// <param name="bearerFormat">The token format the card names. Default <c>JWT</c>.</param>
    /// <param name="scopes">
    ///     The scopes the card's requirement lists, or null for none (the usual case for a plain bearer scheme). Not
    ///     checked here.
    /// </param>
    public static A2AServerBuilder AddBearerAuthentication(this A2AServerBuilder builder,
        string authenticationScheme = "Bearer", string cardSchemeName = "bearer", string? bearerFormat = "JWT",
        IEnumerable<string>? scopes = null)
    {
        return builder.AddCardAuthentication(cardSchemeName, SecurityScheme.Bearer(bearerFormat), authenticationScheme,
            scopes);
    }

    /// <summary>
    ///     Adds <paramref name="authenticationScheme" /> to <see cref="DefaultPolicyName" />, and the startup check that it
    ///     is registered.
    /// </summary>
    internal static void AddDefaultScheme(IServiceCollection services, string authenticationScheme)
    {
        if (!services.Any(d => d.ImplementationInstance is DefaultScheme s && s.Name == authenticationScheme))
            services.AddSingleton(new DefaultScheme(authenticationScheme));
        services.AddAuthorization();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<AuthorizationOptions>, DefaultPolicy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DefaultSchemeCheck>());
        services.TryAddSingleton(sp => new A2ACardSecurity(sp.GetServices<ICardSchemeDeclaration>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<A2AServerOptions>, HostCardSecurity>());
    }

    /// <summary>
    ///     Puts the builder's caller policy on the endpoints that have no authorization metadata once all their conventions
    ///     ran: <see cref="DefaultPolicyName" /> when a scheme is declared, and every policy named by
    ///     <see cref="A2ACallerPolicyExtensions.RequirePolicy" />. Every <c>MapA2A*</c> method calls it.
    /// </summary>
    internal static void ApplyDefault<TBuilder>(IEndpointRouteBuilder endpoints, TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        var services = endpoints.ServiceProvider;
        var schemes = services.GetServices<DefaultScheme>().Any();
        var policies = services.GetServices<A2ACallerPolicyExtensions.RequiredPolicy>().Select(p => p.Name)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (!schemes && policies.Length == 0) return;
        builder.Finally(endpoint =>
        {
            if (endpoint.Metadata.Any(m => m is IAuthorizeData or IAllowAnonymous)) return;
            if (schemes) endpoint.Metadata.Add(new AuthorizeAttribute(DefaultPolicyName));
            foreach (var policy in policies) endpoint.Metadata.Add(new AuthorizeAttribute(policy));
        });
    }

    internal sealed record DefaultScheme(string Name);

    /// <summary>
    ///     An ASP.NET Core scheme declared for a card scheme that carries a bearer token: when it validated the request's
    ///     token, that token may go to the token exchanger (see <c>HttpPlumbing.BearerToken</c>).
    /// </summary>
    internal sealed record BearerScheme(string Name);

    private sealed record CardSchemeDeclaration(string CardName, SecurityScheme Scheme, IReadOnlyList<string> Scopes)
        : ICardSchemeDeclaration;

    private sealed class DefaultPolicy(IEnumerable<DefaultScheme> schemes) : IConfigureOptions<AuthorizationOptions>
    {
        public void Configure(AuthorizationOptions options)
        {
            options.AddPolicy(DefaultPolicyName, p => p
                .AddAuthenticationSchemes([.. schemes.Select(s => s.Name).Distinct(StringComparer.Ordinal)])
                .RequireAuthenticatedUser());
        }
    }

    /// <summary>Declares every scheme on the host's Agent Card.</summary>
    private sealed class HostCardSecurity(A2ACardSecurity security) : IPostConfigureOptions<A2AServerOptions>
    {
        public void PostConfigure(string? name, A2AServerOptions options)
        {
            security.Apply(options.AgentCard);
        }
    }

    /// <summary>Fails the app at startup when a declared scheme has no ASP.NET Core authentication scheme behind it.</summary>
    private sealed class DefaultSchemeCheck(IEnumerable<DefaultScheme> schemes, IServiceProvider services)
        : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            // Without AddAuthentication() there is no scheme provider at all: the same answer as a missing scheme.
            var provider = services.GetService<IAuthenticationSchemeProvider>();
            foreach (var name in schemes.Select(s => s.Name).Distinct(StringComparer.Ordinal))
                if (provider is null || await provider.GetSchemeAsync(name).ConfigureAwait(false) is null)
                    throw new InvalidOperationException(
                        $"The A2A card declares a scheme that the ASP.NET Core authentication scheme '{name}' proves, but no such scheme is registered. Register it, for example builder.Services.AddAuthentication().AddJwtBearer() for 'Bearer'.");
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///     Adds an authorization requirement to the endpoints: the request must satisfy one of the card's
    ///     <c>securityRequirements</c>, each card scheme being proved by the ASP.NET Core authentication scheme that
    ///     <paramref name="schemes" /> maps it to. It adds to the endpoints' other authorization (it does not replace
    ///     <c>RequireAuthorization()</c>), and it uses the card of the request's tenant.
    /// </summary>
    /// <param name="builder">The endpoints, for example the result of <c>MapA2A</c> or <c>MapA2AGrpc</c>.</param>
    /// <param name="schemes">
    ///     Card security scheme name (the key in <c>AgentCard.securitySchemes</c>) → ASP.NET Core authentication scheme
    ///     name, for example <c>{ ["bearer"] = JwtBearerDefaults.AuthenticationScheme }</c>.
    /// </param>
    /// <remarks>
    ///     A requirement is met when every scheme it names authenticated the request; the card is met when any one
    ///     requirement is. A card without requirements, or with an empty requirement, allows anonymous callers, as the
    ///     client reads it. A card scheme that is not in <paramref name="schemes" /> can never be met. Scopes are not
    ///     checked here: check them in your policies. A request that fails for the tenant of its route gets the ASP.NET
    ///     Core answer (challenge or 403), before the A2A layer runs. When the route names no tenant (gRPC, or <c>/a2a</c>
    ///     without <c>{tenant}</c>) and the request body names one, the call is checked again against that tenant's card
    ///     once the tenant is bound, and refused with <see cref="A2AErrorCode.Unauthenticated" /> if it fails. A tenant
    ///     without a card (no agent serves it) has nothing to check: the A2A layer answers it.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="schemes" /> is empty or has a blank name.</exception>
    public static TBuilder RequireAgentCardSchemes<TBuilder>(this TBuilder builder,
        IReadOnlyDictionary<string, string> schemes) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(schemes);
        if (schemes.Count == 0) throw new ArgumentException("Map at least one card scheme.", nameof(schemes));
        foreach (var (card, asp) in schemes)
            if (string.IsNullOrWhiteSpace(card) || string.IsNullOrWhiteSpace(asp))
                throw new ArgumentException("Scheme names must not be blank.", nameof(schemes));
        var policy = new AuthorizationPolicyBuilder()
            .AddRequirements(new AgentCardSchemeRequirement(new Dictionary<string, string>(schemes, StringComparer.Ordinal)))
            .Build();
        builder.RequireAuthorization(policy);
        return builder;
    }
}

/// <summary>
///     The requirement <see cref="A2ACardAuthenticationExtensions.RequireAgentCardSchemes{TBuilder}" /> adds. It is its
///     own handler, so nothing else has to be registered.
/// </summary>
internal sealed class AgentCardSchemeRequirement(IReadOnlyDictionary<string, string> schemes)
    : AuthorizationHandler<AgentCardSchemeRequirement>, IAuthorizationRequirement
{
    public IReadOnlyDictionary<string, string> Schemes { get; } = schemes;

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        AgentCardSchemeRequirement requirement)
    {
        if (context.Resource is not HttpContext http) return; // not an endpoint request: not met
        var card = await CardAsync(http).ConfigureAwait(false);
        // No card for this tenant: nothing to check against. The A2A layer answers an unknown tenant itself.
        if (card is not null && !await IsMetAsync(card, http, requirement.Schemes).ConfigureAwait(false)) return;
        // Checked before the body was read: a call whose body names another tenant is checked again (see Recheck).
        if (http.Items[PassedKey] is not List<AgentCardSchemeRequirement> passed)
            http.Items[PassedKey] = passed = [];
        passed.Add(requirement);
        context.Succeed(requirement);
    }

    /// <summary>The <see cref="HttpContext.Items" /> key of the requirements this request passed.</summary>
    private static readonly object PassedKey = new();

    /// <summary>
    ///     The <see cref="ServerCallContext.TenantAuthentication" /> of a request that passed this requirement, or null when
    ///     it passed none: the same check again, against the card of the tenant the call runs in, when that is not the
    ///     tenant checked here (when the route names no tenant, a JSON-RPC, HTTP+JSON or gRPC body may name it).
    /// </summary>
    internal static Func<ServerCallContext, CancellationToken, ValueTask<bool>>? Recheck(HttpContext http)
    {
        if (!http.Items.TryGetValue(PassedKey, out var value) || value is not List<AgentCardSchemeRequirement> passed)
            return null;
        var checkedTenant = http.Items.TryGetValue(HttpPlumbing.CardCheckedTenantKey, out var t) ? t as string ?? "" : "";
        return (call, ct) => string.Equals(call.Tenant, checkedTenant, StringComparison.Ordinal)
            ? ValueTask.FromResult(true)
            : RecheckAsync(http, passed, call, ct);
    }

    private static async ValueTask<bool> RecheckAsync(HttpContext http, List<AgentCardSchemeRequirement> passed,
        ServerCallContext call, CancellationToken ct)
    {
        if (http.RequestServices.GetService<IAgentCardProvider>() is not { } cards) return true;
        var card = await cards.GetAsync(new AgentRequestScope(call.Tenant, call.RouteValues), ct).ConfigureAwait(false);
        // A tenant no agent serves has no card: as above, the A2A layer answers it.
        if (card is null) return true;
        foreach (var requirement in passed)
            if (!await IsMetAsync(card, http, requirement.Schemes).ConfigureAwait(false))
                return false;
        return true;
    }

    internal static async Task<bool> IsMetAsync(AgentCard card, HttpContext http,
        IReadOnlyDictionary<string, string> map)
    {
        var requirements = card.SecurityRequirements;
        if (requirements is null || requirements.Count == 0) return true;
        var proved = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var requirement in requirements)
        {
            if (requirement?.Schemes is not { } names) continue; // a null entry is unsatisfiable
            var met = true;
            foreach (var name in names.Keys)
                if (!await ProvedAsync(name).ConfigureAwait(false))
                {
                    met = false;
                    break;
                }

            if (met) return true; // an empty requirement is met: anonymous is allowed
        }

        return false;

        async Task<bool> ProvedAsync(string cardScheme)
        {
            if (proved.TryGetValue(cardScheme, out var known)) return known;
            var ok = card.SecuritySchemes?.ContainsKey(cardScheme) == true
                     && map.TryGetValue(cardScheme, out var aspScheme)
                     && (await http.AuthenticateAsync(aspScheme).ConfigureAwait(false)).Succeeded;
            proved[cardScheme] = ok;
            return ok;
        }
    }

    /// <summary>
    ///     The card of the tenant this request runs in: the tenant resolver's, or else the tenant of an artifact or upload
    ///     route. The tenant checked is recorded on the request (<see cref="HttpPlumbing.CardCheckedTenantKey" />). A call
    ///     whose route named the tenant is bound to it, so a different tenant in the request body is refused. A call whose
    ///     route named none may take the tenant its body names, and is then checked again against that tenant's card
    ///     (<see cref="Recheck" />).
    /// </summary>
    private static async Task<AgentCard?> CardAsync(HttpContext http)
    {
        var tenant = http.RequestServices.GetService<ITenantResolver>()?.Resolve(http) ?? "";
        if (tenant.Length == 0) tenant = HttpPlumbing.ObjectRouteTenant(http);
        http.Items[HttpPlumbing.CardCheckedTenantKey] = tenant;
        if (http.RequestServices.GetService<IAgentCardProvider>() is not { } cards) return null;
        return await cards.GetAsync(new AgentRequestScope(tenant, HttpPlumbing.RouteValues(http)), http.RequestAborted)
            .ConfigureAwait(false);
    }
}
