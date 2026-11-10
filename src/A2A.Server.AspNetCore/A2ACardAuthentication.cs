using A2A.Server.Agents;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Server.AspNetCore;

/// <summary>
///     Requires that a request is authenticated the way the Agent Card says (spec §7.4, §13.3: agents MUST authenticate
///     requests using one of the schemes declared in <c>securitySchemes</c> and <c>securityRequirements</c>).
/// </summary>
public static class A2ACardAuthenticationExtensions
{
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
