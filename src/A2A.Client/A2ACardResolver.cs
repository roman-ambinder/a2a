using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Client.Cards;
using A2A.Json;
using A2A.Signing;
using A2A.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.Client;

public sealed class A2ACardResolverOptions
{
    /// <summary>
    /// Which keys card signatures must chain to (spec §7.5). With nothing configured, cards are returned with their status
    /// (<see cref="AgentCardVerificationStatus.Unsigned"/> or <see cref="AgentCardVerificationStatus.Unverified"/>); once
    /// trusted keys or <c>jku</c> hosts are configured, only <see cref="AgentCardVerificationStatus.Verified"/> cards resolve
    /// unless <see cref="CardTrustPolicy.RequireSignature"/> is set to <see langword="false"/>.
    /// <see cref="AgentCardVerificationStatus.Failed"/> always stops resolution.
    /// </summary>
    public CardTrustPolicy Trust { get; set; } = new();

    /// <summary>What to do when an origin's card content changes. Default <see cref="Cards.PinPolicy.Notify"/>.</summary>
    public PinPolicy PinPolicy { get; set; } = PinPolicy.Notify;

    /// <summary>Where card pins are kept. Default: a new <see cref="InMemoryCardPinStore"/>.</summary>
    public ICardPinStore PinStore { get; set; } = new InMemoryCardPinStore();

    /// <summary>Length cap of the sanitized <see cref="UntrustedText"/> values of <see cref="ResolvedAgentCard"/>. Default 2000.</summary>
    public int MaxCardTextLength { get; set; } = UntrustedText.DefaultMaxLength;

    /// <summary>Cache lifetime when the server sends no Cache-Control max-age. Default 5 minutes.</summary>
    public TimeSpan DefaultCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Reject non-HTTPS card URLs except loopback. Default true.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// Time limit for one card resolution (fetch, signature check, pin check). Default 100 seconds. Needed because the
    /// HttpClient of <c>AddA2AClient</c> has an infinite timeout. <see cref="Timeout.InfiniteTimeSpan"/> disables it.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>Test seam: client for <c>jku</c> fetches instead of the SSRF-guarded default.</summary>
    internal HttpClient? JwksHttpClient { get; set; }
}

/// <summary>
/// The resolved card plus how it was checked. <see cref="Card"/> keeps plain strings for wire use; show or prompt with the
/// <see cref="UntrustedText"/> views. Peer identity is <see cref="Identity"/>; the card's <c>name</c> is display only.
/// </summary>
public sealed record ResolvedAgentCard(AgentCard Card, string RawJson, Uri Source, AgentCardVerificationResult Verification)
{
    /// <summary>The card description.</summary>
    public UntrustedText Description { get; init; }

    /// <summary>The card's skills, in card order.</summary>
    public IReadOnlyList<ResolvedSkill> Skills { get; init; } = [];

    /// <summary>The card origin, and the <c>kid</c> that verified it (<see langword="null"/> unless <see cref="AgentCardVerificationStatus.Verified"/>).</summary>
    public (string Origin, string? KeyId) Identity { get; init; }
}

/// <summary>Untrusted text of one card skill.</summary>
public sealed record ResolvedSkill(UntrustedText Name, UntrustedText Description, IReadOnlyList<UntrustedText> Examples);

/// <summary>Raised when an origin serves a card whose canonical content differs from the pinned one.</summary>
public sealed class CardChangedEventArgs(string origin, string oldHash, string newHash) : EventArgs
{
    public string Origin { get; } = origin;
    public string OldHash { get; } = oldHash;
    public string NewHash { get; } = newHash;
}

/// <summary>
/// Fetches Agent Cards from <c>/.well-known/agent-card.json</c> (spec §8.2) with HTTP caching (ETag / max-age, §8.6),
/// signature verification against a <see cref="CardTrustPolicy"/> (§8.4, §7.5) and per-origin card pinning.
/// </summary>
public sealed class A2ACardResolver
{
    // Spec: metrics live under the "A2A" meter.
    private static readonly Meter Meter = new("A2A");
    private static readonly Counter<long> CardChangedCounter =
        Meter.CreateCounter<long>("a2a.card.changed", description: "Agent Cards whose content differs from the pinned card of their origin.");
    private static readonly Counter<long> CardUnsignedCounter =
        Meter.CreateCounter<long>("a2a.card.unsigned", description: "Unsigned Agent Cards returned because signatures are not required.");

    private readonly HttpClient _http;
    private readonly A2ACardResolverOptions _options;
    private readonly AgentCardVerifier _verifier;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public A2ACardResolver(HttpClient httpClient, A2ACardResolverOptions? options = null, ILogger? logger = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new A2ACardResolverOptions();
        ArgumentNullException.ThrowIfNull(_options.Trust, "options.Trust");
        ArgumentNullException.ThrowIfNull(_options.PinStore, "options.PinStore");
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxCardTextLength, 1, "options.MaxCardTextLength");
        _verifier = new AgentCardVerifier(_options.Trust, _options.JwksHttpClient);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised when a card's content differs from the pinned card of its origin (unless <see cref="PinPolicy.Off"/>).</summary>
    public event EventHandler<CardChangedEventArgs>? CardChanged;

    /// <summary>Accepts an agent base URL (well-known path is appended) or a full card URL.</summary>
    public static Uri GetCardUri(Uri agentUrl)
    {
        if (agentUrl.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return agentUrl;
        return new Uri(agentUrl, A2AProtocol.WellKnownAgentCardPath);
    }

    public async Task<AgentCard> GetAgentCardAsync(Uri agentUrl, CancellationToken cancellationToken = default) =>
        (await ResolveAsync(agentUrl, cancellationToken).ConfigureAwait(false)).Card;

    public async Task<ResolvedAgentCard> ResolveAsync(Uri agentUrl, CancellationToken callerToken = default)
    {
        var uri = GetCardUri(agentUrl);
        if (_options.RequireHttps && uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            throw new A2AException(A2AErrorCode.InvalidRequest, $"Agent Card URL must use HTTPS: {uri}");

        var key = uri.AbsoluteUri;
        _cache.TryGetValue(key, out var cached);
        if (cached is not null && cached.Expires > DateTimeOffset.UtcNow) return cached.Result;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        if (_options.Timeout != System.Threading.Timeout.InfiniteTimeSpan) timeoutCts.CancelAfter(_options.Timeout);
        var cancellationToken = timeoutCts.Token;

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
        var node = ParseCard(raw);
        AgentCard card;
        try { card = A2AJson.Deserialize<AgentCard>(raw); }
        catch (JsonException ex) { throw new A2AException(A2AErrorCode.InvalidAgentResponse, $"Malformed Agent Card: {TextSanitizer.ForMessage(ex.Message)}", ex); }

        var verification = await _verifier.VerifyAsync(node, uri, cancellationToken).ConfigureAwait(false);
        var reject = verification.Status switch
        {
            AgentCardVerificationStatus.Verified => false,
            AgentCardVerificationStatus.Failed => true,
            _ => _options.Trust.EffectiveRequireSignature,
        };
        if (reject) throw new AgentCardVerificationException(verification);
        if (verification.Status == AgentCardVerificationStatus.Verified) card = FromSignedContent(node);

        var origin = CardTrustPolicy.OriginOf(uri);
        if (verification.Status == AgentCardVerificationStatus.Unsigned)
            CardUnsignedCounter.Add(1, new KeyValuePair<string, object?>("origin", origin));
        var keyId = verification.Status == AgentCardVerificationStatus.Verified ? verification.KeyId : null;
        await CheckPinAsync(origin, node, keyId, cancellationToken).ConfigureAwait(false);

        var max = _options.MaxCardTextLength;
        var result = new ResolvedAgentCard(card, raw, uri, verification)
        {
            Description = new UntrustedText(card.Description, max),
            Skills = card.Skills.Select(s => new ResolvedSkill(
                new UntrustedText(s.Name, max),
                new UntrustedText(s.Description, max),
                (s.Examples ?? []).Select(e => new UntrustedText(e, max)).ToList())).ToList(),
            Identity = (origin, keyId),
        };
        _cache[key] = new CacheEntry(result, response.Headers.ETag, Expiry(response));
        return result;
    }

    // Parses the card into a fully materialized tree. JsonObject reads lazily and throws ArgumentException on a duplicate
    // member when first touched, which on .NET 8 (no AllowDuplicateProperties) would otherwise escape from verification.
    private static JsonObject ParseCard(string raw)
    {
        try
        {
            var node = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { MaxDepth = A2AJson.MaxJsonDepth }) as JsonObject
                ?? throw new JsonException("Agent Card must be a JSON object.");
            Materialize(node);
            return node;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new A2AException(A2AErrorCode.InvalidAgentResponse, $"Malformed Agent Card: {TextSanitizer.ForMessage(ex.Message)}", ex);
        }
    }

    private static void Materialize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, value) in obj) Materialize(value);
                break;
            case JsonArray array:
                foreach (var item in array) Materialize(item);
                break;
        }
    }

    // Builds the model only from what the signature covers, so nothing outside the signed content reaches the caller.
    // Signatures are carried over for information; they are not part of the signed content.
    internal static AgentCard FromSignedContent(JsonObject received)
    {
        var signed = AgentCardCanonicalizer.SignedContent(received);
        if (received["signatures"] is { } signatures) signed["signatures"] = signatures.DeepClone();
        try { return A2AJson.Deserialize<AgentCard>(signed.ToJsonString()); }
        catch (JsonException ex) { throw new A2AException(A2AErrorCode.InvalidAgentResponse, $"Malformed signed Agent Card content: {TextSanitizer.ForMessage(ex.Message)}", ex); }
    }

    private async Task CheckPinAsync(string origin, JsonObject card, string? keyId, CancellationToken cancellationToken)
    {
        if (_options.PinPolicy == PinPolicy.Off) return;

        var hash = Convert.ToHexString(SHA256.HashData(AgentCardCanonicalizer.Canonicalize(card))).ToLowerInvariant();
        var pinned = await _options.PinStore.GetAsync(origin, cancellationToken).ConfigureAwait(false);
        if (pinned is null)
        {
            await _options.PinStore.SetAsync(origin, new CardPin(hash, keyId), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (pinned.Hash == hash)
        {
            if (keyId is not null && keyId != pinned.KeyId)
            {
                _logger.LogInformation("Agent Card from {Origin} is now signed with key {KeyId} (was {PreviousKeyId}); content unchanged", origin, keyId, pinned.KeyId);
                await _options.PinStore.SetAsync(origin, pinned with { KeyId = keyId }, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        _logger.LogWarning("Agent Card from {Origin} changed: pinned {OldHash}, received {NewHash}", origin, pinned.Hash, hash);
        CardChangedCounter.Add(1, new KeyValuePair<string, object?>("origin", origin));
        CardChanged?.Invoke(this, new CardChangedEventArgs(origin, pinned.Hash, hash));

        if (_options.PinPolicy == PinPolicy.Strict) throw new AgentCardPinException(origin, pinned.Hash, hash);
        await _options.PinStore.SetAsync(origin, new CardPin(hash, keyId), cancellationToken).ConfigureAwait(false);
    }

    private DateTimeOffset Expiry(HttpResponseMessage response)
    {
        var cc = response.Headers.CacheControl;
        if (cc is { NoStore: true } or { NoCache: true }) return DateTimeOffset.MinValue;
        return DateTimeOffset.UtcNow + (cc?.MaxAge ?? _options.DefaultCacheDuration);
    }

    private sealed record CacheEntry(ResolvedAgentCard Result, EntityTagHeaderValue? ETag, DateTimeOffset Expires);
}

/// <summary>The Agent Card failed signature verification or, with signatures required, is unsigned or unverified.</summary>
public sealed class AgentCardVerificationException(AgentCardVerificationResult result)
    : A2AException(A2AErrorCode.InvalidAgentResponse, $"Agent Card signature check failed ({result.Status}): {result.Reason}")
{
    public AgentCardVerificationResult Result { get; } = result;
}

/// <summary>With <see cref="PinPolicy.Strict"/>, the origin served a card whose content differs from the pinned one.</summary>
public sealed class AgentCardPinException(string origin, string oldHash, string newHash)
    : A2AException(A2AErrorCode.InvalidAgentResponse, $"Agent Card from {origin} changed: pinned {oldHash}, received {newHash}")
{
    public string Origin { get; } = origin;
    public string OldHash { get; } = oldHash;
    public string NewHash { get; } = newHash;
}
