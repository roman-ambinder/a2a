namespace A2A.Delegation;

/// <summary>
/// Swaps one token for another, as in OAuth 2.0 Token Exchange (RFC 8693). A server uses it to get a narrower token
/// for a downstream agent without handing over the caller's own token.
/// </summary>
public interface ITokenExchanger
{
    /// <summary>Exchanges the subject token in <paramref name="request"/> for a new token.</summary>
    /// <exception cref="TokenExchangeException">The exchange was refused.</exception>
    /// <exception cref="HttpRequestException">A network failure (an implementation that uses HTTP may throw it).</exception>
    /// <exception cref="OperationCanceledException">The call was cancelled or timed out.</exception>
    ValueTask<TokenExchangeResult> ExchangeAsync(TokenExchangeRequest request, CancellationToken cancellationToken);
}

/// <summary>Token type identifiers from RFC 8693 section 3.</summary>
public static class TokenTypes
{
    /// <summary>An OAuth 2.0 access token.</summary>
    public const string AccessToken = "urn:ietf:params:oauth:token-type:access_token";

    /// <summary>A JSON Web Token.</summary>
    public const string Jwt = "urn:ietf:params:oauth:token-type:jwt";
}

/// <summary>A token exchange request (RFC 8693 section 2.1). <see cref="ToString"/> never shows a token.</summary>
public sealed class TokenExchangeRequest
{
    /// <summary>The token to exchange.</summary>
    public required string SubjectToken { get; init; }

    /// <summary>Type of <see cref="SubjectToken"/>. Default: <see cref="TokenTypes.AccessToken"/>.</summary>
    public string SubjectTokenType { get; init; } = TokenTypes.AccessToken;

    /// <summary>Logical name of the target service. Not sent when null.</summary>
    public string? Audience { get; init; }

    /// <summary>URI of the target service. Not sent when null.</summary>
    public string? Resource { get; init; }

    /// <summary>Scopes wanted for the new token. Not sent when null or empty.</summary>
    public IReadOnlyList<string>? Scopes { get; init; }

    /// <summary>Type of token wanted. Not sent when null.</summary>
    public string? RequestedTokenType { get; init; }

    /// <summary>Token that identifies the acting party. Not sent when null.</summary>
    public string? ActorToken { get; init; }

    /// <summary>Type of <see cref="ActorToken"/>. Not sent when null.</summary>
    public string? ActorTokenType { get; init; }

    /// <summary>Returns the type name only.</summary>
    public override string ToString() => nameof(TokenExchangeRequest);
}

/// <summary>A token exchange response (RFC 8693 section 2.2). <see cref="ToString"/> never shows the token.</summary>
public sealed class TokenExchangeResult
{
    /// <summary>The issued token.</summary>
    public required string AccessToken { get; init; }

    /// <summary>How to use the token, for example <c>Bearer</c>. Default: <c>Bearer</c>.</summary>
    public string TokenType { get; init; } = "Bearer";

    /// <summary>Type of the issued token. Null when the server did not say.</summary>
    public string? IssuedTokenType { get; init; }

    /// <summary>Lifetime of the token. Null when the server did not say.</summary>
    public TimeSpan? ExpiresIn { get; init; }

    /// <summary>Space-separated scopes of the token. Null when the server did not say.</summary>
    public string? Scope { get; init; }

    /// <summary>Returns the type name only.</summary>
    public override string ToString() => nameof(TokenExchangeResult);
}

/// <summary>A token exchange failed. The message and <see cref="Error"/> never contain a token.</summary>
public sealed class TokenExchangeException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Fixed text without tokens.</param>
    /// <param name="error">The RFC 6749 <c>error</c> code from the server, if any.</param>
    /// <param name="inner">Cause. Do not pass an exception that quotes a response body.</param>
    public TokenExchangeException(string message, string? error = null, Exception? inner = null)
        : base(message, inner)
    {
        Error = error;
    }

    /// <summary>The RFC 6749 <c>error</c> code from the server (for example <c>invalid_target</c>). Null when absent or not valid.</summary>
    public string? Error { get; }
}
