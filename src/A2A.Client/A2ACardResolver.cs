using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Json;
using A2A.Signing;

namespace A2A.Client;

/// <summary>What to do with Agent Card signatures.</summary>
public enum SignaturePolicy
{
    /// <summary>Do not verify.</summary>
    Ignore,

    /// <summary>Verify when signatures are present; accept unsigned cards. Reject cards whose signatures all fail.</summary>
    VerifyIfPresent,

    /// <summary>Reject unsigned cards and cards whose signatures fail.</summary>
    Require,
}

public sealed class A2ACardResolverOptions
{
    public SignaturePolicy SignaturePolicy { get; set; } = SignaturePolicy.Ignore;

    /// <summary>Required unless <see cref="SignaturePolicy"/> is <see cref="SignaturePolicy.Ignore"/>.</summary>
    public AgentCardVerifier? Verifier { get; set; }

    /// <summary>Cache lifetime when the server sends no Cache-Control max-age. Default 5 minutes.</summary>
    public TimeSpan DefaultCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Reject non-HTTPS card URLs except loopback. Default true.</summary>
    public bool RequireHttps { get; set; } = true;
}

/// <summary>The resolved card plus how it was checked.</summary>
public sealed record ResolvedAgentCard(AgentCard Card, string RawJson, Uri Source, AgentCardVerificationResult? Verification);

/// <summary>
/// Fetches Agent Cards from <c>/.well-known/agent-card.json</c> (spec §8.2) with HTTP caching (ETag / max-age, §8.6)
/// and optional signature verification (§8.4).
/// </summary>
public sealed class A2ACardResolver
{
    private readonly HttpClient _http;
    private readonly A2ACardResolverOptions _options;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public A2ACardResolver(HttpClient httpClient, A2ACardResolverOptions? options = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new A2ACardResolverOptions();
        if (_options.SignaturePolicy != SignaturePolicy.Ignore && _options.Verifier is null)
            throw new ArgumentException("A verifier is required when signature verification is enabled.", nameof(options));
    }

    /// <summary>Accepts an agent base URL (well-known path is appended) or a full card URL.</summary>
    public static Uri GetCardUri(Uri agentUrl)
    {
        if (agentUrl.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return agentUrl;
        return new Uri(agentUrl, A2AProtocol.WellKnownAgentCardPath);
    }

    public async Task<AgentCard> GetAgentCardAsync(Uri agentUrl, CancellationToken cancellationToken = default) =>
        (await ResolveAsync(agentUrl, cancellationToken).ConfigureAwait(false)).Card;

    public async Task<ResolvedAgentCard> ResolveAsync(Uri agentUrl, CancellationToken cancellationToken = default)
    {
        var uri = GetCardUri(agentUrl);
        if (_options.RequireHttps && uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            throw new A2AException(A2AErrorCode.InvalidRequest, $"Agent Card URL must use HTTPS: {uri}");

        var key = uri.AbsoluteUri;
        _cache.TryGetValue(key, out var cached);
        if (cached is not null && cached.Expires > DateTimeOffset.UtcNow) return cached.Result;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(A2AProtocol.JsonMediaType));
        if (cached?.ETag is not null) request.Headers.IfNoneMatch.Add(cached.ETag);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            var refreshed = cached with { Expires = Expiry(response) };
            _cache[key] = refreshed;
            return refreshed.Result;
        }
        if (!response.IsSuccessStatusCode)
            throw new A2AException(A2AErrorCode.Internal, $"Failed to fetch Agent Card from {uri}: HTTP {(int)response.StatusCode}") { HttpStatus = (int)response.StatusCode };

        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        AgentCard card;
        try { card = A2AJson.Deserialize<AgentCard>(raw); }
        catch (JsonException ex) { throw new A2AException(A2AErrorCode.InvalidAgentResponse, $"Malformed Agent Card: {ex.Message}", ex); }

        AgentCardVerificationResult? verification = null;
        if (_options.SignaturePolicy != SignaturePolicy.Ignore)
        {
            verification = await _options.Verifier!.VerifyAsync(raw, uri, cancellationToken).ConfigureAwait(false);
            var reject = verification.Status switch
            {
                AgentCardVerificationStatus.Valid => false,
                AgentCardVerificationStatus.Unsigned => _options.SignaturePolicy == SignaturePolicy.Require,
                _ => true,
            };
            if (reject)
                throw new AgentCardVerificationException(verification);
            if (verification.Status == AgentCardVerificationStatus.Valid)
                card = FromSignedContent(raw);
        }

        var result = new ResolvedAgentCard(card, raw, uri, verification);
        _cache[key] = new CacheEntry(result, response.Headers.ETag, Expiry(response));
        return result;
    }

    // Builds the model only from what the signature covers, so nothing outside the signed content reaches the caller.
    // Signatures are carried over for information; they are not part of the signed content.
    private static AgentCard FromSignedContent(string raw)
    {
        var received = JsonNode.Parse(raw)!.AsObject();
        var signed = AgentCardCanonicalizer.SignedContent(received);
        if (received["signatures"] is { } signatures) signed["signatures"] = signatures.DeepClone();
        return signed.Deserialize<AgentCard>(A2AJson.Options)!;
    }

    private DateTimeOffset Expiry(HttpResponseMessage response)
    {
        var cc = response.Headers.CacheControl;
        if (cc is { NoStore: true } or { NoCache: true }) return DateTimeOffset.MinValue;
        return DateTimeOffset.UtcNow + (cc?.MaxAge ?? _options.DefaultCacheDuration);
    }

    private sealed record CacheEntry(ResolvedAgentCard Result, EntityTagHeaderValue? ETag, DateTimeOffset Expires);
}

/// <summary>The Agent Card failed the configured signature policy.</summary>
public sealed class AgentCardVerificationException(AgentCardVerificationResult result)
    : A2AException(A2AErrorCode.InvalidAgentResponse,
        $"Agent Card signature check failed ({result.Status}): {string.Join("; ", result.Errors)}")
{
    public AgentCardVerificationResult Result { get; } = result;
}
