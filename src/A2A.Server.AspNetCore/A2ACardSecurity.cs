using A2A.Server.Agents;

namespace A2A.Server.AspNetCore;

/// <summary>
///     The authentication schemes this host declares, ready to put on any Agent Card: those from
///     <see cref="A2ACardAuthenticationExtensions.AddCardAuthentication" />,
///     <see cref="A2ACardAuthenticationExtensions.AddBearerAuthentication" /> and
///     <see cref="A2AApiKeyAuthenticationExtensions.AddApiKeyAuthentication" />. The host's own card
///     (<see cref="A2AServerOptions.AgentCard" />) gets them already; a <see cref="TenantCardProvider" /> injects this
///     service and calls <see cref="Apply" /> on the cards it builds, so each tenant's card says the same.
/// </summary>
/// <remarks>
///     Registered (singleton) by each of those methods. Without any of them it is not registered.
/// </remarks>
public sealed class A2ACardSecurity
{
    private readonly IReadOnlyList<ICardSchemeDeclaration> _declarations;

    internal A2ACardSecurity(IEnumerable<ICardSchemeDeclaration> declarations)
    {
        _declarations = [.. declarations];
    }

    /// <summary>
    ///     Adds each declared scheme to <paramref name="card" />: to <c>securitySchemes</c> under its card name, and as one
    ///     more requirement (one more way in) in <c>securityRequirements</c>, in the order they were declared.
    /// </summary>
    /// <param name="card">The card to change. It is changed in place.</param>
    /// <returns><paramref name="card" />.</returns>
    /// <remarks>
    ///     A scheme the card already has under that name is kept, and a requirement naming only that scheme is not added
    ///     twice, so calling it again on the same card changes nothing. Requirements the card already has stay as they are.
    /// </remarks>
    public AgentCard Apply(AgentCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        foreach (var d in _declarations)
        {
            var name = d.CardName;
            card.SecuritySchemes ??= new Dictionary<string, SecurityScheme>(StringComparer.Ordinal);
            card.SecuritySchemes.TryAdd(name, d.Scheme);
            card.SecurityRequirements ??= [];
            if (card.SecurityRequirements.Any(r => r.Schemes.Count == 1 && r.Schemes.ContainsKey(name))) continue;
            var scopes = new StringList();
            scopes.List.AddRange(d.Scopes);
            card.SecurityRequirements.Add(new SecurityRequirement { Schemes = { [name] = scopes } });
        }

        return card;
    }
}

/// <summary>One scheme to declare on Agent Cards.</summary>
internal interface ICardSchemeDeclaration
{
    string CardName { get; }
    SecurityScheme Scheme { get; }
    IReadOnlyList<string> Scopes { get; }
}
