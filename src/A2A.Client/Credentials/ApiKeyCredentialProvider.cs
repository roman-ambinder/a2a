namespace A2A.Client.Credentials;

/// <summary>
/// Adds an API key to a request, as a header or a cookie, under the name the card's <c>apiKey</c> scheme declares.
/// Query-string keys are not supported. Header names that could change request framing or routing are refused.
/// </summary>
public sealed class ApiKeyCredentialProvider : IA2ACredentialProvider
{
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Transfer-Encoding", "Connection", "Upgrade", "Cookie", "TE", "Trailer", "Proxy-Authorization",
        // Content headers that do not start with "Content-": they cannot go on a request, so every call would fail.
        "Allow", "Expires", "Last-Modified",
    };

    private readonly Func<CancellationToken, ValueTask<string>> _source;
    private readonly string? _schemeName;

    /// <summary>Creates a provider with a fixed key.</summary>
    /// <param name="apiKey">The key. Validated when applied, not here.</param>
    /// <param name="schemeName">When set, the provider only handles the card scheme with this name.</param>
    public ApiKeyCredentialProvider(string apiKey, string? schemeName = null)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        _source = _ => ValueTask.FromResult(apiKey);
        _schemeName = schemeName;
    }

    /// <summary>Creates a provider that asks <paramref name="apiKey"/> for the key on every request.</summary>
    /// <param name="apiKey">Returns the key. Called once per apply.</param>
    /// <param name="schemeName">When set, the provider only handles the card scheme with this name.</param>
    public ApiKeyCredentialProvider(Func<CancellationToken, ValueTask<string>> apiKey, string? schemeName = null)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        _source = apiKey;
        _schemeName = schemeName;
    }

    /// <inheritdoc />
    public bool CanHandle(A2ACredentialContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_schemeName is not null && !string.Equals(_schemeName, context.SchemeName, StringComparison.Ordinal)) return false;
        if (context.Scheme is not ApiKeyScheme { Value: var v }) return false;
        var location = v.Location;
        var isHeader = string.Equals(location, "header", StringComparison.OrdinalIgnoreCase);
        var isCookie = string.Equals(location, "cookie", StringComparison.OrdinalIgnoreCase);
        if (!isHeader && !isCookie) return false;
        if (!IsToken(v.Name)) return false;
        return !isHeader || !IsSensitiveHeader(v.Name);
    }

    /// <inheritdoc />
    public async ValueTask ApplyAsync(HttpRequestMessage request, A2ACredentialContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!CanHandle(context) || context.Scheme is not ApiKeyScheme { Value: var v })
            throw new A2AException(A2AErrorCode.Unauthenticated, "The security scheme is not one this API key provider can apply.");

        var isCookie = string.Equals(v.Location, "cookie", StringComparison.OrdinalIgnoreCase);
        var key = await _source(cancellationToken).ConfigureAwait(false);
        if (!IsValidKey(key, isCookie))
            throw new A2AException(A2AErrorCode.Unauthenticated, "The API key is empty or contains characters that are not allowed.");

        if (isCookie)
        {
            var prefix = v.Name + "=";
            if (request.Headers.TryGetValues("Cookie", out var existing))
            {
                var all = existing.ToList();
                var kept = all.Where(c => !c.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                if (kept.Count != all.Count)
                {
                    request.Headers.Remove("Cookie");
                    foreach (var c in kept) request.Headers.TryAddWithoutValidation("Cookie", c);
                }
            }

            if (!request.Headers.TryAddWithoutValidation("Cookie", prefix + key))
                throw new A2AException(A2AErrorCode.Unauthenticated, "The API key could not be added as a cookie.");
            return;
        }

        request.Headers.Remove(v.Name);
        if (!request.Headers.TryAddWithoutValidation(v.Name, key))
            throw new A2AException(A2AErrorCode.Unauthenticated, $"The API key could not be added to header '{v.Name}'.");
    }

    private static bool IsValidKey(string? key, bool cookie)
    {
        if (string.IsNullOrEmpty(key)) return false;
        foreach (var c in key)
        {
            if (char.IsControl(c)) return false;
            if (cookie && (c == ';' || c == ',' || char.IsWhiteSpace(c))) return false;
        }
        return true;
    }

    private static bool IsSensitiveHeader(string name) =>
        SensitiveHeaders.Contains(name)
        || name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("A2A-", StringComparison.OrdinalIgnoreCase);

    private static bool IsToken(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var c in name)
        {
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                || "!#$%&'*+-.^_`|~".Contains(c);
            if (!ok) return false;
        }
        return true;
    }
}
