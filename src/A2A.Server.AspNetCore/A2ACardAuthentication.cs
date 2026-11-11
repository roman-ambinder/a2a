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
    ///         declare the scheme there.
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
        services.AddSingleton(new CardSchemeDeclaration(cardSchemeName, scheme, [.. scopes ?? []]));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<A2AServerOptions>, CardSchemeDeclarations>());
        AddDefaultScheme(services, authenticationScheme);
        return builder;
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
    }

    /// <summary>
    ///     Puts <see cref="DefaultPolicyName" /> on the endpoints that have no authorization metadata once all their
    ///     conventions ran, when a scheme is declared. Every <c>MapA2A*</c> method calls it.
    /// </summary>
    internal static void ApplyDefault<TBuilder>(IEndpointRouteBuilder endpoints, TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        if (!endpoints.ServiceProvider.GetServices<DefaultScheme>().Any()) return;
        builder.Finally(endpoint =>
        {
            if (endpoint.Metadata.Any(m => m is IAuthorizeData or IAllowAnonymous)) return;
            endpoint.Metadata.Add(new AuthorizeAttribute(DefaultPolicyName));
        });
    }

    internal sealed record DefaultScheme(string Name);

    private sealed record CardSchemeDeclaration(string CardName, SecurityScheme Scheme, List<string> Scopes);

    private sealed class DefaultPolicy(IEnumerable<DefaultScheme> schemes) : IConfigureOptions<AuthorizationOptions>
    {
        public void Configure(AuthorizationOptions options)
        {
            options.AddPolicy(DefaultPolicyName, p => p
                .AddAuthenticationSchemes([.. schemes.Select(s => s.Name).Distinct(StringComparer.Ordinal)])
                .RequireAuthenticatedUser());
        }
    }

    /// <summary>Declares each <see cref="CardSchemeDeclaration" /> on the host's Agent Card.</summary>
    private sealed class CardSchemeDeclarations(IEnumerable<CardSchemeDeclaration> declarations)
        : IPostConfigureOptions<A2AServerOptions>
    {
        public void PostConfigure(string? name, A2AServerOptions options)
        {
            var card = options.AgentCard;
            foreach (var d in declarations)
            {
                card.SecuritySchemes ??= new Dictionary<string, SecurityScheme>(StringComparer.Ordinal);
                card.SecuritySchemes.TryAdd(d.CardName, d.Scheme);
                card.SecurityRequirements ??= [];
                if (!card.SecurityRequirements.Any(r => r.Schemes.Count == 1 && r.Schemes.ContainsKey(d.CardName)))
                {
                    var scopes = new StringList();
                    scopes.List.AddRange(d.Scopes);
                    card.SecurityRequirements.Add(new SecurityRequirement { Schemes = { [d.CardName] = scopes } });
                }
            }
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
    ///     checked here: check them in your policies. A request that fails gets the ASP.NET Core answer (challenge or
    ///     403), before the A2A layer runs.
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
        if (card is null || await IsMetAsync(card, http, requirement.Schemes).ConfigureAwait(false))
            context.Succeed(requirement);
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

    private static async Task<AgentCard?> CardAsync(HttpContext http)
    {
        if (http.RequestServices.GetService<IAgentCardProvider>() is not { } cards) return null;
        var tenant = http.RequestServices.GetService<ITenantResolver>()?.Resolve(http) ?? "";
        return await cards.GetAsync(new AgentRequestScope(tenant, HttpPlumbing.RouteValues(http)), http.RequestAborted)
            .ConfigureAwait(false);
    }
}
