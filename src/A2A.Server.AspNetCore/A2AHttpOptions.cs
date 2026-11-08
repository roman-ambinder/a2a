using A2A.Signing;

namespace A2A.Server.AspNetCore;

/// <summary>HTTP-specific settings.</summary>
public sealed class A2AHttpOptions
{
    /// <summary>When set, the served Agent Card is signed with this key and its public key is published at <see cref="JwksPath"/>.</summary>
    public AgentCardSigningKey? SigningKey { get; set; }

    /// <summary>Path where the public key set of <see cref="SigningKey"/> is served. Default <c>/.well-known/jwks.json</c>.</summary>
    public string JwksPath { get; set; } = "/.well-known/jwks.json";

    /// <summary>Cache-Control max-age for the Agent Card. Default 5 minutes.</summary>
    public TimeSpan AgentCardMaxAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The public origin (and optional path prefix) of the agent, for example <c>https://agent.example.com</c>. Used to complete an
    /// Agent Card that lists no <see cref="AgentCard.SupportedInterfaces"/>: each mapped binding is listed at this URL plus its
    /// mapped pattern. When null, the first address the server listens on is used, or <c>http://localhost</c> when it reports none
    /// (set this behind a proxy or load balancer, where the listen address is not the public one).
    /// </summary>
    /// <exception cref="ArgumentException">The value is not an absolute <c>http</c> or <c>https</c> URL, or has a query or fragment (thrown by the setter).</exception>
    public Uri? PublicBaseUrl
    {
        get => _publicBaseUrl;
        set
        {
            if (value is not null && (!value.IsAbsoluteUri || (value.Scheme != Uri.UriSchemeHttp && value.Scheme != Uri.UriSchemeHttps)
                || value.Query.Length > 0 || value.Fragment.Length > 0))
                throw new ArgumentException("PublicBaseUrl must be an absolute http or https URL without a query or fragment.", nameof(value));
            _publicBaseUrl = value;
        }
    }

    private Uri? _publicBaseUrl;
}
