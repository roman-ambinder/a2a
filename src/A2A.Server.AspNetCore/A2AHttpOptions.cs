using A2A.Signing;

namespace A2A.Server.AspNetCore;

/// <summary>HTTP-specific settings.</summary>
public sealed class A2AHttpOptions
{
    private Uri? _publicBaseUrl;

    /// <summary>
    ///     When set, the served Agent Card is signed with this key and its public key is published at
    ///     <see cref="JwksPath" />.
    /// </summary>
    public AgentCardSigningKey? SigningKey { get; set; }

    /// <summary>
    ///     When true, a card that has values its signature would not cover (empty lists and objects that the
    ///     canonicalization removes, see <see cref="AgentCardCanonicalizer.PrunedPaths" />) is refused instead of signed
    ///     around them: serving it fails with an <see cref="InvalidOperationException" /> that names their paths. Only
    ///     signed cards are checked. Default false, which signs around them and logs warning 800.
    /// </summary>
    public bool RefusePrunedCardValues { get; set; }

    /// <summary>
    ///     ASP.NET Core authentication scheme names (ordinal, case-sensitive, as registered) whose authenticated callers may
    ///     have their <c>Authorization: Bearer</c> token kept for the token exchanger
    ///     (<see cref="A2A.Server.A2AServerBuilder.AddTokenExchanger(A2A.Delegation.ITokenExchanger)" />), although they are
    ///     not bearer schemes. Empty by default: only a JWT bearer scheme (<c>AddJwtBearer</c>), or the scheme declared for
    ///     a bearer, OAuth 2.0 or OpenID Connect card scheme (<c>AddBearerAuthentication</c>, <c>AddCardAuthentication</c>),
    ///     validating the header as the caller's keeps it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         For hosts where another scheme authenticates callers but the bearer header must still be exchanged, for
    ///         example a gateway scheme that already validated the token upstream, or an API-key scheme deployed behind
    ///         one. The listed scheme must have authenticated this request's caller (its result succeeded and its user is
    ///         the caller); the token is still the request's own single <c>Authorization: Bearer</c> header value.
    ///     </para>
    ///     <para>
    ///         Security trade-off: for a listed scheme the SDK does not check the token. It trusts that scheme (or the
    ///         gateway in front of it) to have validated the header as the caller's token. If it did not, the agent
    ///         exchanges a token nobody checked: a caller with a valid API key could send someone else's bearer token and
    ///         have the agent act with it. The app does not start when a listed scheme is not registered.
    ///     </para>
    /// </remarks>
    public ISet<string> TrustedBearerSchemes { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Path where the public key set of <see cref="SigningKey" /> is served. Default <c>/.well-known/jwks.json</c>.</summary>
    public string JwksPath { get; set; } = "/.well-known/jwks.json";

    /// <summary>Cache-Control max-age for the Agent Card. Default 5 minutes.</summary>
    public TimeSpan AgentCardMaxAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     The public origin (and optional path prefix) of the agent, for example <c>https://agent.example.com</c>. Used to
    ///     complete an
    ///     Agent Card that lists no <see cref="AgentCard.SupportedInterfaces" />: each mapped binding is listed at this URL
    ///     plus its
    ///     mapped pattern. When null, the first address the server listens on is used, or <c>http://localhost</c> when it
    ///     reports none
    ///     (set this behind a proxy or load balancer, where the listen address is not the public one).
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     The value is not an absolute <c>http</c> or <c>https</c> URL, or has a query or
    ///     fragment (thrown by the setter).
    /// </exception>
    public Uri? PublicBaseUrl
    {
        get => _publicBaseUrl;
        set
        {
            if (value is not null &&
                (!value.IsAbsoluteUri || (value.Scheme != Uri.UriSchemeHttp && value.Scheme != Uri.UriSchemeHttps)
                                      || value.Query.Length > 0 || value.Fragment.Length > 0))
                throw new ArgumentException(
                    "PublicBaseUrl must be an absolute http or https URL without a query or fragment.", nameof(value));
            _publicBaseUrl = value;
        }
    }
}