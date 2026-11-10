using A2A.Client.Credentials;

namespace A2A.Cli;

/// <summary>
///     Sends a fixed token as <c>Authorization: Bearer</c> for an HTTP bearer scheme, an OAuth2 scheme or an OpenID
///     Connect scheme.
///     The SDK has no such provider because a token that never expires is rarely right in a long-lived client. A one-shot
///     tool is the exception.
/// </summary>
internal sealed class StaticBearerCredentialProvider : IA2ACredentialProvider
{
    private readonly string _token;

    public StaticBearerCredentialProvider(string token)
    {
        if (!IsValid(token))
            throw new ArgumentException("The token is empty or has control characters.", nameof(token));
        _token = token;
    }

    public bool CanHandle(A2ACredentialContext context)
    {
        return context.Scheme switch
        {
            HttpAuthScheme { Value.Scheme: var s } => string.Equals(s, "bearer", StringComparison.OrdinalIgnoreCase),
            OAuth2Scheme or OpenIdConnectScheme => true,
            _ => false
        };
    }

    public ValueTask ApplyAsync(HttpRequestMessage request, A2ACredentialContext context,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = null;
        if (!request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _token))
            throw new A2AException(A2AErrorCode.Unauthenticated, "The token could not be added to the request.");
        return ValueTask.CompletedTask;
    }

    /// <summary>A token is valid when it is not empty and has no control characters (this also rules out CR and LF).</summary>
    public static bool IsValid(string? token)
    {
        return !string.IsNullOrEmpty(token) && !token.Any(char.IsControl);
    }
}