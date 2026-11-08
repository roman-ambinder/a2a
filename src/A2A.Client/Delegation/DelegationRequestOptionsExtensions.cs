using A2A.Delegation;

namespace A2A.Client.Delegation;

/// <summary>Helpers that put an exchanged token on a client call.</summary>
public static class DelegationRequestOptionsExtensions
{
    /// <summary>Sets the <c>Authorization</c> header of this call to the token from a token exchange.</summary>
    /// <exception cref="InvalidOperationException">The token type is not <c>Bearer</c>.</exception>
    public static A2ARequestOptions UseBearerToken(this A2ARequestOptions options, TokenExchangeResult token)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(token);
        if (!string.Equals(token.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The token type is not Bearer, so it cannot go in an Authorization: Bearer header.");
        }

        options.Headers["Authorization"] = "Bearer " + token.AccessToken;
        return options;
    }
}
