using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using A2A.Client.Credentials;
using A2A.Delegation;

namespace A2A.Client.Delegation;

/// <summary>Settings for <see cref="OAuthTokenExchanger"/>.</summary>
public sealed class OAuthTokenExchangerOptions
{
    /// <summary>The token endpoint. Must be HTTPS, or a loopback address.</summary>
    public required Uri TokenEndpoint { get; init; }

    /// <summary>Client id, sent with <see cref="ClientSecret"/> as HTTP Basic (RFC 6749 section 2.3.1). Not sent when null.</summary>
    public string? ClientId { get; init; }

    /// <summary>Client secret. Not sent when null.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>Last hook before the request is sent, for example to add a client assertion. It runs after the SDK sets its headers.</summary>
    public Func<HttpRequestMessage, CancellationToken, ValueTask>? ConfigureRequest { get; init; }
}

/// <summary>
/// OAuth 2.0 Token Exchange (RFC 8693) over HTTP. It needs an HTTPS endpoint (loopback is allowed for tests), reads at most 64 KiB
/// of response, and never puts a token or a server message into an exception.
/// </summary>
/// <remarks>
/// <para>
/// The request carries the client secret, the subject token and the actor token. A redirect (a 307 or 308 makes an
/// <see cref="HttpClient"/> that follows redirects post the form body again) would send them to the new location. So this
/// class does not take an <see cref="HttpClient"/>. It builds its own, over a handler that never follows redirects: a
/// default handler, or the <see cref="HttpMessageHandler"/> you pass, which is checked with
/// <see cref="A2ACredentialHandler.EnsureNoAutoRedirect"/>.
/// </para>
/// <para>
/// The SDK can check only <see cref="SocketsHttpHandler"/> and <see cref="HttpClientHandler"/> (also under
/// <see cref="DelegatingHandler"/>s). A custom terminal handler must not follow redirects itself. With
/// <c>IHttpClientFactory</c>, pass <c>IHttpMessageHandlerFactory.CreateHandler(name)</c> and set
/// <c>AllowAutoRedirect = false</c> on the named client's primary handler.
/// </para>
/// <para>
/// A 3xx response is refused and its <c>Location</c> is not read. As a second line of defense, the exchanger throws when the
/// response says the request went to another URI.
/// </para>
/// </remarks>
public sealed class OAuthTokenExchanger : ITokenExchanger, IDisposable
{
    private const int MaxResponseBytes = 64 * 1024;
    private const int MaxErrorLength = 128;
    private const string GrantType = "urn:ietf:params:oauth:grant-type:token-exchange";

    // Shared by every exchanger that does not bring its own handler. It has no state per caller (no cookies, no credentials).
#if NETSTANDARD2_0
    // No SocketsHttpHandler on netstandard2.0.
    private static readonly Lazy<HttpClientHandler> DefaultHandler = new(static () => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    });
#else
    private static readonly Lazy<SocketsHttpHandler> DefaultHandler = new(static () => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    });
#endif

    private readonly HttpClient _httpClient;
    private readonly OAuthTokenExchangerOptions _options;

    /// <summary>Creates the exchanger over a default handler that does not follow redirects.</summary>
    /// <exception cref="ArgumentException">The endpoint is not HTTPS and not loopback.</exception>
    public OAuthTokenExchanger(OAuthTokenExchangerOptions options) : this(DefaultHandler.Value, options)
    {
    }

    /// <summary>
    /// Creates the exchanger over <paramref name="handler"/>. The exchanger does not dispose the handler.
    /// </summary>
    /// <param name="handler">The primary handler. Must not follow redirects; see the remarks of the class.</param>
    /// <param name="options">The settings.</param>
    /// <exception cref="ArgumentException">The endpoint is not HTTPS and not loopback, or <paramref name="handler"/> follows redirects.</exception>
    public OAuthTokenExchanger(HttpMessageHandler handler, OAuthTokenExchangerOptions options)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        A2ACredentialHandler.EnsureNoAutoRedirect(handler);
        var endpoint = options.TokenEndpoint;
        if (!endpoint.IsAbsoluteUri
            || (endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)))
        {
            throw new ArgumentException("The token endpoint must be an HTTPS URI (HTTP is allowed only for loopback).", nameof(options));
        }

        _httpClient = new HttpClient(handler, disposeHandler: false) { MaxResponseContentBufferSize = MaxResponseBytes };
        _options = options;
    }

    /// <summary>Releases the internal <see cref="HttpClient"/>. The handler is not disposed: it is shared or yours.</summary>
    public void Dispose() => _httpClient.Dispose();

    /// <inheritdoc />
    public async ValueTask<TokenExchangeResult> ExchangeAsync(TokenExchangeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("grant_type", GrantType),
            new("subject_token", request.SubjectToken),
            new("subject_token_type", request.SubjectTokenType),
        };
        Add(fields, "audience", request.Audience);
        Add(fields, "resource", request.Resource);
        if (request.Scopes is { Count: > 0 })
        {
            fields.Add(new("scope", string.Join(' ', request.Scopes)));
        }

        Add(fields, "requested_token_type", request.RequestedTokenType);
        Add(fields, "actor_token", request.ActorToken);
        Add(fields, "actor_token_type", request.ActorTokenType);

        using var message = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_options.ClientId is not null && _options.ClientSecret is not null)
        {
            var credentials = FormEncode(_options.ClientId) + ":" + FormEncode(_options.ClientSecret);
            message.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }

        if (_options.ConfigureRequest is { } configure)
        {
            await configure(message, cancellationToken).ConfigureAwait(false);
            if (message.RequestUri != _options.TokenEndpoint)
            {
                throw new InvalidOperationException("ConfigureRequest changed the request URI. It must not: the form holds secrets for the token endpoint only.");
            }
        }

        using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        var finalUri = response.RequestMessage?.RequestUri;
        if (response.StatusCode is >= HttpStatusCode.Ambiguous and < HttpStatusCode.BadRequest
            || (finalUri is not null && finalUri != _options.TokenEndpoint))
        {
            throw new TokenExchangeException("The token endpoint redirected the request. Redirects are refused.");
        }

        var body = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // No inner exception: its message can quote the body.
            throw new TokenExchangeException(response.IsSuccessStatusCode
                ? "The token endpoint returned a response that is not valid JSON."
                : $"The token exchange failed with HTTP status {(int)response.StatusCode}.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (!response.IsSuccessStatusCode)
            {
                throw new TokenExchangeException(
                    $"The token exchange failed with HTTP status {(int)response.StatusCode}.",
                    ReadErrorCode(root));
            }

            return ReadResult(root);
        }
    }

    private static TokenExchangeResult ReadResult(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new TokenExchangeException("The token endpoint response is not a JSON object.");
        }

        var accessToken = GetString(root, "access_token");
        if (accessToken is null || accessToken.Length == 0)
        {
            throw new TokenExchangeException("The token endpoint response has no access_token.");
        }

        var tokenType = GetString(root, "token_type");
        if (tokenType is null || tokenType.Length == 0)
        {
            throw new TokenExchangeException("The token endpoint response has no token_type.");
        }

        TimeSpan? expiresIn = null;
        if (root.TryGetProperty("expires_in", out var exp)
            && exp.ValueKind == JsonValueKind.Number
            && exp.TryGetDouble(out var seconds)
            && seconds >= 0
            && seconds <= TimeSpan.MaxValue.TotalSeconds)
        {
            expiresIn = TimeSpan.FromSeconds(seconds);
        }

        return new TokenExchangeResult
        {
            AccessToken = accessToken,
            TokenType = tokenType,
            IssuedTokenType = GetString(root, "issued_token_type"),
            ExpiresIn = expiresIn,
            Scope = GetString(root, "scope"),
        };
    }

    private static string? GetString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // RFC 6749 section 5.2: error uses %x20-21 / %x23-5B / %x5D-7E. Anything else means we do not trust it.
    private static string? ReadErrorCode(JsonElement root)
    {
        var error = GetString(root, "error");
        if (error is null || error.Length == 0)
        {
            return null;
        }

        if (error.Length > MaxErrorLength)
        {
            error = error[..MaxErrorLength];
        }

        foreach (var c in error)
        {
            if (!(c is >= (char)0x20 and <= (char)0x21 || c is >= (char)0x23 and <= (char)0x5B || c is >= (char)0x5D and <= (char)0x7E))
            {
                return null;
            }
        }

        return error;
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new TokenExchangeException("The token endpoint response is too large.");
        }

#if NETSTANDARD2_0
        // Stream has no DisposeAsync on netstandard2.0.
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#endif
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxResponseBytes)
            {
                throw new TokenExchangeException("The token endpoint response is too large.");
            }
        }

        return buffer.ToArray();
    }

    private static void Add(List<KeyValuePair<string, string>> fields, string name, string? value)
    {
        if (value is not null)
        {
            fields.Add(new(name, value));
        }
    }

    // RFC 6749 section 2.3.1: form-url-encode each value before base64.
    private static string FormEncode(string value) => Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);
}
