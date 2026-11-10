using System.Net.Http.Headers;
using A2A.Client.Credentials;
using A2A.Text;
using Azure.Core;

namespace A2A.Client.Identity.Azure;

/// <summary>Settings for <see cref="AzureTokenCredentialProvider" />. The provider copies them at construction.</summary>
public sealed class AzureTokenCredentialOptions
{
    /// <summary>
    ///     The scopes to ask Entra for, usually one: the agent's application id URI and <c>/.default</c>, for example
    ///     <c>api://my-agent/.default</c>. Required. The scope picks the audience of the token, so the card never chooses it:
    ///     card scopes are not sent.
    /// </summary>
    public IList<string> Scopes { get; } = new List<string>();

    /// <summary>The Entra tenant to ask for the token. When null, the credential's own tenant is used.</summary>
    public string? TenantId { get; set; }

    /// <summary>When set, the provider only handles the card scheme with this name.</summary>
    public string? SchemeName { get; set; }

    /// <summary>
    ///     The agent this provider sends tokens to. Only its scheme, host and port count. When null, the provider binds to
    ///     the first card origin it accepts. Either way it refuses every other origin, so a token for one agent never reaches
    ///     another agent, which could replay it.
    /// </summary>
    public Uri? AgentOrigin { get; set; }

    /// <summary>How long before expiry a new token is fetched. Capped at half the remaining lifetime. Defaults to 5 minutes.</summary>
    public TimeSpan RefreshBeforeExpiry { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The time limit for one token request to the credential. Defaults to 30 seconds.</summary>
    public TimeSpan TokenRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The clock, for tests. Defaults to <see cref="System.TimeProvider.System" />.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

/// <summary>
///     Gets tokens from an Azure.Core <see cref="TokenCredential" /> (a managed identity, workload identity,
///     <c>DefaultAzureCredential</c>, ...) and adds them as <c>Authorization: Bearer</c>. It handles a card's OAuth2,
///     OpenID Connect and HTTP Bearer schemes. Use one provider per agent: it binds to one card origin. The token is cached
///     and refreshed before it expires. Concurrent callers share one token request.
/// </summary>
public sealed class AzureTokenCredentialProvider : IA2ACredentialProvider
{
    private readonly TokenCredential _credential;
    private readonly object _gate = new();
    private readonly TimeSpan _refreshBeforeExpiry;
    private readonly TimeSpan _requestTimeout;
    private readonly string? _schemeName;
    private readonly string[] _scopes;
    private readonly string? _tenantId;
    private readonly TimeProvider _time;
    private Uri? _boundOrigin;
    private Task<CachedToken>? _inFlight;
    private CachedToken? _token;

    /// <summary>Creates the provider.</summary>
    /// <param name="credential">The Azure credential, for example <c>new ManagedIdentityCredential()</c> from Azure.Identity.</param>
    /// <param name="options">The scopes and other settings.</param>
    /// <exception cref="ArgumentException">
    ///     <see cref="AzureTokenCredentialOptions.Scopes" /> is empty or holds an empty
    ///     scope, or <see cref="AzureTokenCredentialOptions.AgentOrigin" /> is not an absolute URL.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A time span option is out of range.</exception>
    public AzureTokenCredentialProvider(TokenCredential credential, AzureTokenCredentialOptions options)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Scopes.Count == 0)
            throw new ArgumentException("Scopes is required, for example api://my-agent/.default.", nameof(options));
        if (options.Scopes.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Scopes cannot contain an empty scope.", nameof(options));
        if (options.AgentOrigin is { IsAbsoluteUri: false })
            throw new ArgumentException("AgentOrigin must be an absolute URL.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RefreshBeforeExpiry, TimeSpan.Zero,
            nameof(options.RefreshBeforeExpiry));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.TokenRequestTimeout, TimeSpan.Zero,
            nameof(options.TokenRequestTimeout));
        ArgumentNullException.ThrowIfNull(options.TimeProvider);

        _credential = credential;
        _scopes = [.. options.Scopes];
        _tenantId = options.TenantId;
        _schemeName = options.SchemeName;
        _boundOrigin = options.AgentOrigin;
        _refreshBeforeExpiry = options.RefreshBeforeExpiry;
        _requestTimeout = options.TokenRequestTimeout;
        _time = options.TimeProvider;
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <inheritdoc />
    public bool CanHandle(A2ACredentialContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_schemeName is not null &&
            !string.Equals(context.SchemeName, _schemeName, StringComparison.Ordinal)) return false;
        var handles = context.Scheme switch
        {
            OAuth2Scheme or OpenIdConnectScheme => true,
            HttpAuthScheme h => string.Equals(h.Value.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        return handles && TryBindOrigin(context.CardOrigin);
    }

    /// <inheritdoc />
    public async ValueTask ApplyAsync(HttpRequestMessage request, A2ACredentialContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryBindOrigin(context.CardOrigin))
            throw new A2AException(A2AErrorCode.Unauthenticated,
                $"This provider is bound to another agent, so it will not send its token to {TextSanitizer.ForMessage(context.CardOrigin.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped))}. Use one provider per agent.");
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>Binds the provider to the first origin it sees. True when <paramref name="origin" /> is the bound origin.</summary>
    private bool TryBindOrigin(Uri origin)
    {
        var bound = Interlocked.CompareExchange(ref _boundOrigin, origin, null) ?? origin;
        return A2ACredentialBinding.IsSameOrigin(bound, origin);
    }

    private async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        CachedToken? current;
        Task<CachedToken> fetch;
        lock (_gate)
        {
            current = _token;
            if (current is not null && Now < current.RefreshAt) return current.Value;
            fetch = _inFlight ??= Task.Run(FetchAndStoreAsync);
        }

        try
        {
            return (await fetch.WaitAsync(cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (A2AException) when (current is not null && Now < current.ExpiresAt)
        {
            return current.Value;
        }
    }

    private async Task<CachedToken> FetchAndStoreAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(_requestTimeout, _time);
            var issuedAt = Now;
            AccessToken accessToken;
            try
            {
                accessToken = await _credential
                    .GetTokenAsync(new TokenRequestContext(_scopes, tenantId: _tenantId), cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
            {
                throw new A2AException(A2AErrorCode.Unauthenticated,
                    $"The Azure credential timed out after {_requestTimeout.TotalSeconds:0.##} seconds.", ex);
            }
            catch (Exception ex) when (ex is not A2AException)
            {
                throw new A2AException(A2AErrorCode.Unauthenticated,
                    $"The Azure credential failed ({ex.GetType().Name}): {TextSanitizer.ForMessage(ex.Message)}", ex);
            }

            var token = Check(accessToken, issuedAt);
            lock (_gate)
            {
                _token = token;
                _inFlight = null;
            }

            return token;
        }
        catch
        {
            lock (_gate)
            {
                _inFlight = null;
            }

            throw;
        }
    }

    private CachedToken Check(AccessToken accessToken, DateTimeOffset issuedAt)
    {
        if (accessToken.Token is not { Length: > 0 } value)
            throw Bad("the token is empty");
        if (value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw Bad("the token contains control or white space characters");
        if (accessToken.TokenType is { Length: > 0 } type &&
            !string.Equals(type, "Bearer", StringComparison.OrdinalIgnoreCase))
            throw Bad("the token type is not Bearer");
        var expiresAt = accessToken.ExpiresOn;
        if (expiresAt <= issuedAt) throw Bad("the token has already expired");

        var remaining = expiresAt - issuedAt;
        var skew = _refreshBeforeExpiry < remaining / 2 ? _refreshBeforeExpiry : remaining / 2;
        var refreshAt = expiresAt - skew;
        if (accessToken.RefreshOn is { } refreshOn && refreshOn > issuedAt && refreshOn < refreshAt)
            refreshAt = refreshOn;
        return new CachedToken(value, refreshAt, expiresAt);

        static A2AException Bad(string why)
        {
            return new A2AException(A2AErrorCode.Unauthenticated, $"The Azure credential's token was rejected: {why}.");
        }
    }

    // A class, not a record: a record's ToString would print the token.
    private sealed class CachedToken(string value, DateTimeOffset refreshAt, DateTimeOffset expiresAt)
    {
        public string Value { get; } = value;
        public DateTimeOffset RefreshAt { get; } = refreshAt;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
    }
}
