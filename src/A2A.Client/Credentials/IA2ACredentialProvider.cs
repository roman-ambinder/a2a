namespace A2A.Client.Credentials;

/// <summary>
///     Supplies a credential for one security scheme of an Agent Card. The SDK picks the provider once, at client
///     creation,
///     and calls <see cref="ApplyAsync" /> on every request to the card's origin.
/// </summary>
public interface IA2ACredentialProvider
{
    /// <summary>Returns true when this provider can satisfy the scheme in <paramref name="context" />.</summary>
    bool CanHandle(A2ACredentialContext context);

    /// <summary>
    ///     Adds the credential to <paramref name="request" />. Must not change <see cref="HttpRequestMessage.RequestUri" />.
    ///     Called on every attempt, so retries get a fresh credential.
    /// </summary>
    ValueTask ApplyAsync(HttpRequestMessage request, A2ACredentialContext context, CancellationToken cancellationToken);
}

/// <summary>The security scheme a provider is asked to satisfy, and where the card came from.</summary>
public sealed class A2ACredentialContext
{
    /// <summary>Creates a context.</summary>
    public A2ACredentialContext(string schemeName, SecurityScheme scheme, IReadOnlyList<string> scopes, Uri cardOrigin)
    {
        ArgumentNullException.ThrowIfNull(schemeName);
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(cardOrigin);
        SchemeName = schemeName;
        Scheme = scheme;
        Scopes = scopes;
        CardOrigin = cardOrigin;
    }

    /// <summary>The scheme's name in the card's <c>securitySchemes</c>.</summary>
    public string SchemeName { get; }

    /// <summary>The scheme as the card declares it.</summary>
    public SecurityScheme Scheme { get; }

    /// <summary>The scopes the selected requirement names for this scheme.</summary>
    public IReadOnlyList<string> Scopes { get; }

    /// <summary>The origin the card was resolved from: scheme, host and port, no path.</summary>
    public Uri CardOrigin { get; }
}