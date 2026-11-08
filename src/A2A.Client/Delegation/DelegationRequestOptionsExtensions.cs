using A2A.Client.Credentials;
using A2A.Delegation;

namespace A2A.Client.Delegation;

/// <summary>Helpers that put an exchanged token on a client call.</summary>
public static class DelegationRequestOptionsExtensions
{
    /// <summary>
    /// Sets the <c>Authorization</c> header of this call to the token from a token exchange, and only for requests to the
    /// origin of <paramref name="agentUrl"/>.
    /// </summary>
    /// <param name="options">The options of one call.</param>
    /// <param name="token">The exchanged token. Its type must be <c>Bearer</c>.</param>
    /// <param name="agentUrl">
    /// The URL of the agent the token was issued for: the interface URL of its Agent Card, or any URL on the same origin.
    /// Compared with <see cref="A2ACredentialBinding.IsSameOrigin"/> (scheme, host and port).
    /// </param>
    /// <remarks>
    /// <para>
    /// The check runs in <see cref="A2ARequestOptions.ConfigureRequest"/>, once per attempt, after the other headers are set.
    /// A request to another origin throws an <see cref="A2AException"/> before anything is sent. This stops the options
    /// from being used on the wrong client. An existing <see cref="A2ARequestOptions.ConfigureRequest"/> runs first.
    /// </para>
    /// <para>
    /// It cannot stop a redirect: the check sees only the first request. The client's <see cref="HttpClient"/> must not follow
    /// redirects. <c>A2AClientFactory.CreateAsync</c> with credential providers and <c>AddA2AClient</c> set that up. For an
    /// <see cref="HttpClient"/> you build yourself, use a handler with <c>AllowAutoRedirect = false</c>
    /// (check it with <see cref="A2ACredentialHandler.EnsureNoAutoRedirect"/>). A handler that follows redirects drops
    /// <c>Authorization</c> on a redirect to another host, but keeps other headers.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The token type is not <c>Bearer</c>, or the token has a space or control character.</exception>
    /// <exception cref="ArgumentException"><paramref name="agentUrl"/> is not an absolute HTTP or HTTPS URL.</exception>
    public static A2ARequestOptions UseBearerToken(this A2ARequestOptions options, TokenExchangeResult token, Uri agentUrl)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(agentUrl);
        if (!agentUrl.IsAbsoluteUri || (agentUrl.Scheme != Uri.UriSchemeHttps && agentUrl.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("The agent URL must be an absolute HTTP or HTTPS URL.", nameof(agentUrl));
        }

        if (!string.Equals(token.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The token type is not Bearer, so it cannot go in an Authorization: Bearer header.");
        }

        foreach (var c in token.AccessToken)
        {
            if (char.IsControl(c) || c == ' ')
            {
                // Never quote the token.
                throw new InvalidOperationException("The access token has a space or control character, so it cannot go in an Authorization header.");
            }
        }

        var previous = options.ConfigureRequest;
        options.ConfigureRequest = async (request, cancellationToken) =>
        {
            if (previous is not null)
            {
                await previous(request, cancellationToken).ConfigureAwait(false);
            }

            var uri = request.RequestUri;
            if (uri is not { IsAbsoluteUri: true } || !A2ACredentialBinding.IsSameOrigin(uri, agentUrl))
            {
                throw new A2AException(A2AErrorCode.InvalidAgentResponse,
                    $"Refusing to send the token to {(uri is { IsAbsoluteUri: true } ? A2ACredentialBinding.OriginText(uri) : "(no absolute URI)")}: not the origin {A2ACredentialBinding.OriginText(agentUrl)} it was issued for.");
            }
        };
        options.Headers["Authorization"] = "Bearer " + token.AccessToken;
        return options;
    }
}
