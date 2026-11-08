using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using A2A.Text;

namespace A2A.Client.Credentials;

/// <summary>How the client authenticates to the token endpoint (RFC 6749 section 2.3.1).</summary>
public enum OAuth2ClientAuthentication
{
    /// <summary>HTTP Basic authentication with the URL-encoded client id and secret.</summary>
    ClientSecretBasic,

    /// <summary>The client id and secret in the form body.</summary>
    ClientSecretPost,
}

/// <summary>Settings for <see cref="OAuth2ClientCredentialsProvider"/>. The provider copies them at construction.</summary>
public sealed class OAuth2ClientCredentialsOptions
{
    /// <summary>The OAuth2 client id. Required.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>The OAuth2 client secret. Required.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// The token endpoint. When null, the provider uses the <c>tokenUrl</c> of the card's client credentials flow, but only
    /// when it is on the card's origin.
    /// </summary>
    public Uri? TokenEndpoint { get; set; }

    /// <summary>Scopes to request. When null or empty, the scopes of the card's security requirement are used.</summary>
    public IList<string>? Scopes { get; set; }

    /// <summary>How the client authenticates to the token endpoint. Defaults to HTTP Basic.</summary>
    public OAuth2ClientAuthentication ClientAuthentication { get; set; } = OAuth2ClientAuthentication.ClientSecretBasic;

    /// <summary>Extra form parameters for the token request, for example <c>audience</c> or <c>resource</c>.</summary>
    public IDictionary<string, string> AdditionalParameters { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>When set, the provider only handles the card scheme with this name.</summary>
    public string? SchemeName { get; set; }

    /// <summary>How long before expiry a new token is fetched. Capped at half the token lifetime. Defaults to 60 seconds.</summary>
    public TimeSpan RefreshBeforeExpiry { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The lifetime assumed when the server sends no <c>expires_in</c>. Defaults to 5 minutes.</summary>
    public TimeSpan DefaultTokenLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The time limit for one token request. Defaults to 30 seconds.</summary>
    public TimeSpan TokenRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>When true (the default), the token endpoint must use HTTPS. Loopback addresses are always allowed.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// The handler for token requests. Defaults to a non-redirecting <see cref="SocketsHttpHandler"/> that the provider owns.
    /// A handler you pass stays yours: the provider does not dispose it.
    /// </summary>
    public HttpMessageHandler? TokenHandler { get; set; }

    /// <summary>The clock, for tests. Defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

/// <summary>
/// Gets OAuth2 client credentials access tokens and adds them as <c>Authorization: Bearer</c>. Tokens are cached per token
/// endpoint, card origin and scope set. Concurrent callers share one token request.
/// </summary>
public sealed class OAuth2ClientCredentialsProvider : IA2ACredentialProvider, IDisposable
{
    private const int MaxSlots = 256;
    private static readonly string[] ReservedParameters = ["grant_type", "scope", "client_id", "client_secret"];

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly Uri? _tokenEndpoint;
    private readonly string[]? _scopes;
    private readonly OAuth2ClientAuthentication _clientAuthentication;
    private readonly KeyValuePair<string, string>[] _additional;
    private readonly string? _schemeName;
    private readonly TimeSpan _refreshBeforeExpiry;
    private readonly TimeSpan _defaultLifetime;
    private readonly TimeSpan _requestTimeout;
    private readonly bool _requireHttps;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly HttpMessageHandler? _ownedHandler;
    private readonly ConcurrentDictionary<(string Endpoint, string Origin, string Scope), Slot> _slots = new();

    /// <summary>Creates the provider.</summary>
    /// <exception cref="ArgumentException">An option is invalid, or <see cref="OAuth2ClientCredentialsOptions.TokenHandler"/> follows redirects.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A time span option is out of range.</exception>
    public OAuth2ClientCredentialsProvider(OAuth2ClientCredentialsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(options.ClientId))
            throw new ArgumentException("ClientId is required.", nameof(options));
        if (string.IsNullOrEmpty(options.ClientSecret))
            throw new ArgumentException("ClientSecret is required.", nameof(options));
        if (options.TokenEndpoint is not null)
        {
            var problem = CheckEndpoint(options.TokenEndpoint, options.RequireHttps);
            if (problem is not null) throw new ArgumentException($"TokenEndpoint {problem}", nameof(options));
        }
        foreach (var key in options.AdditionalParameters.Keys)
            if (Array.IndexOf(ReservedParameters, key) >= 0)
                throw new ArgumentException($"AdditionalParameters cannot contain '{key}'.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RefreshBeforeExpiry, TimeSpan.Zero, nameof(options.RefreshBeforeExpiry));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.DefaultTokenLifetime, TimeSpan.Zero, nameof(options.DefaultTokenLifetime));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.TokenRequestTimeout, TimeSpan.Zero, nameof(options.TokenRequestTimeout));
        ArgumentNullException.ThrowIfNull(options.TimeProvider);

        HttpMessageHandler handler;
        if (options.TokenHandler is { } custom)
        {
            A2ACredentialHandler.EnsureNoAutoRedirect(custom);
            handler = custom;
        }
        else
        {
            handler = _ownedHandler = new SocketsHttpHandler { AllowAutoRedirect = false };
        }

        _clientId = options.ClientId;
        _clientSecret = options.ClientSecret;
        _tokenEndpoint = options.TokenEndpoint;
        _scopes = options.Scopes is { Count: > 0 } ? [.. options.Scopes] : null;
        _clientAuthentication = options.ClientAuthentication;
        _additional = [.. options.AdditionalParameters];
        _schemeName = options.SchemeName;
        _refreshBeforeExpiry = options.RefreshBeforeExpiry;
        _defaultLifetime = options.DefaultTokenLifetime;
        _requestTimeout = options.TokenRequestTimeout;
        _requireHttps = options.RequireHttps;
        _time = options.TimeProvider;
        _http = new HttpClient(handler, disposeHandler: false)
        {
            MaxResponseContentBufferSize = 64 * 1024,
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <inheritdoc />
    public bool CanHandle(A2ACredentialContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_schemeName is not null && !string.Equals(context.SchemeName, _schemeName, StringComparison.Ordinal)) return false;
        return context.Scheme switch
        {
            OAuth2Scheme { Value.Flows.ClientCredentials: { } flow } =>
                _tokenEndpoint is not null || CardEndpoint(flow.TokenUrl, context.CardOrigin) is not null,
            HttpAuthScheme h => _tokenEndpoint is not null
                && string.Equals(h.Value.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase),
            OpenIdConnectScheme => _tokenEndpoint is not null,
            _ => false,
        };
    }

    /// <inheritdoc />
    public async ValueTask ApplyAsync(HttpRequestMessage request, A2ACredentialContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = _tokenEndpoint;
        if (endpoint is null && context.Scheme is OAuth2Scheme { Value.Flows.ClientCredentials: { } flow })
            endpoint = CardEndpoint(flow.TokenUrl, context.CardOrigin);
        if (endpoint is null)
            throw new A2AException(A2AErrorCode.Unauthenticated,
                $"No token endpoint for scheme '{TextSanitizer.Sanitize(context.SchemeName, TextSanitizer.MaxHeaderValueLength)}'. Set TokenEndpoint.");

        var scopes = string.Join(' ', (_scopes ?? [.. context.Scopes]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var key = (endpoint.AbsoluteUri, context.CardOrigin.AbsoluteUri, scopes);
        var token = await GetTokenAsync(key, endpoint, scopes, cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>Releases the HTTP client and the default handler. A handler passed in <see cref="OAuth2ClientCredentialsOptions.TokenHandler"/> is left alone.</summary>
    public void Dispose()
    {
        _http.Dispose();
        _ownedHandler?.Dispose();
    }

    private Uri? CardEndpoint(string tokenUrl, Uri cardOrigin) =>
        Uri.TryCreate(tokenUrl, UriKind.Absolute, out var uri)
        && A2ACredentialBinding.IsSameOrigin(uri, cardOrigin)
        && CheckEndpoint(uri, _requireHttps) is null
            ? uri
            : null;

    private static string? CheckEndpoint(Uri uri, bool requireHttps)
    {
        if (!uri.IsAbsoluteUri) return "must be an absolute URL.";
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return "must be an http or https URL.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "must not contain user info.";
        if (!string.IsNullOrEmpty(uri.Fragment)) return "must not contain a fragment.";
        if (requireHttps && uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback) return "must use HTTPS.";
        return null;
    }

    internal sealed class Slot
    {
        public readonly object Gate = new();
        public bool Evicted;
        public CachedToken? Token;
        public Task<CachedToken>? InFlight;
    }

    internal sealed record CachedToken(string Value, DateTimeOffset RefreshAt, DateTimeOffset ExpiresAt);

    internal int SlotCount => _slots.Count;

    internal Slot GetSlot((string Endpoint, string Origin, string Scope) key)
    {
        if (_slots.Count >= MaxSlots) EvictIdleSlots();
        return _slots.GetOrAdd(key, static _ => new Slot());
    }

    internal void EvictIdleSlots()
    {
        foreach (var (k, s) in _slots)
        {
            lock (s.Gate)
            {
                if (s.InFlight is not null) continue;
                s.Evicted = true;
                _slots.TryRemove(new KeyValuePair<(string, string, string), Slot>(k, s));
            }
        }
    }

    private async ValueTask<string> GetTokenAsync((string Endpoint, string Origin, string Scope) key, Uri endpoint,
        string scope, CancellationToken cancellationToken)
    {
        Slot slot;
        Task<CachedToken> fetch;
        CachedToken? current;
        while (true)
        {
            slot = GetSlot(key);
            lock (slot.Gate)
            {
                if (slot.Evicted) continue; // removed by an eviction scan since GetSlot: take the replacement
                current = slot.Token;
                if (current is not null && Now < current.RefreshAt) return current.Value;
                fetch = slot.InFlight ??= Task.Run(() => FetchAndStoreAsync(slot, endpoint, scope));
                break;
            }
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

    private async Task<CachedToken> FetchAndStoreAsync(Slot slot, Uri endpoint, string scope)
    {
        try
        {
            using var cts = new CancellationTokenSource(_requestTimeout, _time);
            var token = await RequestTokenAsync(endpoint, scope, cts.Token).ConfigureAwait(false);
            lock (slot.Gate)
            {
                slot.Token = token;
                slot.InFlight = null;
            }
            return token;
        }
        catch (Exception ex)
        {
            lock (slot.Gate) slot.InFlight = null;
            if (ex is A2AException) throw;
            var where = endpoint.GetLeftPart(UriPartial.Path);
            var reason = ex is OperationCanceledException
                ? $"timed out after {_requestTimeout.TotalSeconds:0.##} seconds"
                : $"failed ({ex.GetType().Name})";
            throw new A2AException(A2AErrorCode.Unauthenticated, $"The token request to {where} {reason}.", ex);
        }
    }

    private async Task<CachedToken> RequestTokenAsync(Uri endpoint, string scope, CancellationToken ct)
    {
        var issuedAt = Now;
        var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };
        if (scope.Length > 0) form.Add(new("scope", scope));
        form.AddRange(_additional);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_clientAuthentication == OAuth2ClientAuthentication.ClientSecretBasic)
        {
            var raw = WebUtility.UrlEncode(_clientId) + ":" + WebUtility.UrlEncode(_clientSecret);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }
        else
        {
            form.Add(new("client_id", _clientId));
            form.Add(new("client_secret", _clientSecret));
        }
        request.Content = new FormUrlEncodedContent(form);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new A2AException(A2AErrorCode.Unauthenticated, ErrorMessage(endpoint, (int)response.StatusCode, body));

        return Parse(body, issuedAt);
    }

    private string ErrorMessage(Uri endpoint, int status, string body)
    {
        var sb = new StringBuilder($"The token endpoint {endpoint.GetLeftPart(UriPartial.Path)} answered HTTP {status}.");
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "error", "error_description" })
                    if (doc.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } text)
                        sb.Append(' ').Append(name).Append(": ").Append(Clean(text));
            }
        }
        catch (JsonException) { }
        return sb.ToString();
    }

    private string Clean(string text) =>
        TextSanitizer.Sanitize(text.Replace(_clientSecret, "[redacted]", StringComparison.Ordinal), TextSanitizer.MaxHeaderValueLength);

    private CachedToken Parse(string body, DateTimeOffset issuedAt)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException ex) { throw Bad("the response is not valid JSON", ex); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Bad("the response is not a JSON object");
            if (!root.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String
                || at.GetString() is not { Length: > 0 } token)
                throw Bad("the response has no access_token");
            if (token.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
                throw Bad("the access_token contains control or white space characters");
            if (root.TryGetProperty("token_type", out var tt)
                && (tt.ValueKind != JsonValueKind.String || !string.Equals(tt.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)))
                throw Bad("the token_type is not Bearer");

            var lifetime = _defaultLifetime;
            if (root.TryGetProperty("expires_in", out var ei) && ei.ValueKind != JsonValueKind.Null)
            {
                double seconds;
                if (ei.ValueKind == JsonValueKind.Number) seconds = ei.GetDouble();
                else if (ei.ValueKind == JsonValueKind.String
                    && double.TryParse(ei.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    seconds = parsed;
                else throw Bad("expires_in is not a number");
                if (double.IsNaN(seconds) || seconds < 0) throw Bad("expires_in is negative");
                lifetime = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromDays(3650).TotalSeconds));
            }

            var skew = _refreshBeforeExpiry < lifetime / 2 ? _refreshBeforeExpiry : lifetime / 2;
            var expiresAt = issuedAt + lifetime;
            return new CachedToken(token, expiresAt - skew, expiresAt);
        }

        static A2AException Bad(string why, Exception? inner = null) =>
            new(A2AErrorCode.Unauthenticated, $"The token endpoint response was rejected: {why}.", inner);
    }
}
