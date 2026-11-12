namespace A2A.Client.Credentials;

/// <summary>
///     Sends a token from your own source as <c>Authorization: Bearer</c>, for a card that asks for an HTTP bearer scheme,
///     an OAuth 2.0 scheme or an OpenID Connect scheme. Use it when something else gets the token (a vault, a token
///     broker, your app's sign-in); for the OAuth 2.0 client credentials flow, use
///     <see cref="OAuth2ClientCredentialsProvider" />.
/// </summary>
/// <remarks>
///     The source is called on every request, retries included, so a rotated token is picked up; cache inside it if a
///     fetch is costly. There is deliberately no constructor that takes a fixed token: a token that never changes is rarely
///     right in a long-lived client.
/// </remarks>
public sealed class BearerTokenCredentialProvider : IA2ACredentialProvider
{
    private readonly Func<CancellationToken, ValueTask<string>> _getToken;

    /// <summary>Creates the provider over <paramref name="getToken" />, which returns the current token.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="getToken" /> is null.</exception>
    public BearerTokenCredentialProvider(Func<CancellationToken, ValueTask<string>> getToken)
    {
        _getToken = getToken ?? throw new ArgumentNullException(nameof(getToken));
    }

    /// <inheritdoc />
    public bool CanHandle(A2ACredentialContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Scheme switch
        {
            HttpAuthScheme { Value.Scheme: var s } => string.Equals(s, "bearer", StringComparison.OrdinalIgnoreCase),
            OAuth2Scheme or OpenIdConnectScheme => true,
            _ => false
        };
    }

    /// <inheritdoc />
    /// <exception cref="A2AException">Unauthenticated: the source returned an empty token, or one with control characters.</exception>
    public async ValueTask ApplyAsync(HttpRequestMessage request, A2ACredentialContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var token = await _getToken(cancellationToken).ConfigureAwait(false);
        // Control characters (CR and LF among them) would let a token inject headers.
        if (string.IsNullOrEmpty(token) || token.Any(char.IsControl))
            throw new A2AException(A2AErrorCode.Unauthenticated,
                "The bearer token source returned an empty token or one with control characters.");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }
}
