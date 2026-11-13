using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Client.Cards;
using A2A.Client.Peers;
using A2A.Client.Transports;
using A2A.Json;
using A2A.Signing;
using A2A.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
#if !NETSTANDARD2_0
using A2A.Security.Http;
#endif

namespace A2A.Client;

/// <summary>Settings of <see cref="A2ACardResolver" />: signature trust, card pinning and text limits.</summary>
public sealed class A2ACardResolverOptions
{
    /// <summary>
    ///     Which keys card signatures must chain to (spec §7.5). With nothing configured, cards are returned with their status
    ///     (<see cref="AgentCardVerificationStatus.Unsigned" /> or <see cref="AgentCardVerificationStatus.Unverified" />);
    ///     once
    ///     trusted keys or <c>jku</c> hosts are configured, only <see cref="AgentCardVerificationStatus.Verified" /> cards
    ///     resolve
    ///     unless <see cref="CardTrustPolicy.RequireSignature" /> is set to <see langword="false" />.
    ///     <see cref="AgentCardVerificationStatus.Failed" /> always stops resolution. Changes apply to cached cards on the
    ///     next resolve.
    /// </summary>
    public CardTrustPolicy Trust { get; set; } = new();

    /// <summary>What to do when an origin's card content changes. Default <see cref="Cards.PinPolicy.Notify" />.</summary>
    public PinPolicy PinPolicy { get; set; } = PinPolicy.Notify;

    /// <summary>Where card pins are kept. Default: a new <see cref="InMemoryCardPinStore" />.</summary>
    public ICardPinStore PinStore { get; set; } = new InMemoryCardPinStore();

    /// <summary>
    ///     Adds an external id (DID, SPIFFE) to the peer identity. Default
    ///     <see cref="CardPeerIdentityResolver.Instance" />.
    /// </summary>
    public IPeerIdentityResolver IdentityResolver { get; set; } = CardPeerIdentityResolver.Instance;

    /// <summary>
    ///     Length cap of the sanitized <see cref="UntrustedText" /> values of <see cref="ResolvedAgentCard" />. Default
    ///     2000.
    /// </summary>
    public int MaxCardTextLength { get; set; } = UntrustedText.DefaultMaxLength;

    /// <summary>Cache lifetime when the server sends no Cache-Control max-age. Default 5 minutes.</summary>
    public TimeSpan DefaultCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Reject non-HTTPS card URLs except loopback. Default true.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    ///     Time limit for one card resolution (fetch, signature check, pin check). Default 100 seconds. Needed because the
    ///     HttpClient of <c>AddA2AClient</c> has an infinite timeout. <see cref="Timeout.InfiniteTimeSpan" /> disables it.
    ///     When it passes, the resolution throws a <see cref="TaskCanceledException" /> whose
    ///     <see cref="Exception.InnerException" /> is a <see cref="TimeoutException" />, like the call timeout of the
    ///     client; a cancellation of the caller's token has none.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>Test seam: client for <c>jku</c> fetches instead of the SSRF-guarded default.</summary>
    internal HttpClient? JwksHttpClient { get; set; }

    /// <summary>
    ///     Seam for the v0.3 compat package. Runs on the parsed card before the model is read and before the signature is
    ///     checked, and returns the card to use (it may change the object or return a new one). Null (the default): no change.
    /// </summary>
    internal Func<JsonObject, JsonObject>? CardNormalizer { get; set; }

    /// <summary>A shallow copy of these options (the trust policy, pin store and other objects are shared).</summary>
    internal A2ACardResolverOptions Clone()
    {
        return (A2ACardResolverOptions)MemberwiseClone();
    }
}

/// <summary>
///     The resolved card plus how it was checked. <see cref="Card" /> keeps plain strings for wire use; show or prompt
///     with the
///     <see cref="UntrustedText" /> views. Peer identity is <see cref="Identity" />; the card's <c>name</c> is display
///     only.
///     <see cref="PeerIdentity.Assurance" /> says how strong the identity is.
/// </summary>
public sealed record ResolvedAgentCard(
    AgentCard Card,
    string RawJson,
    Uri Source,
    AgentCardVerificationResult Verification)
{
    /// <summary>The card description.</summary>
    public UntrustedText Description { get; init; }

    /// <summary>The card's skills, in card order.</summary>
    public IReadOnlyList<ResolvedSkill> Skills { get; init; } = [];

    /// <summary>
    ///     The peer identity: card origin, the <c>kid</c> that verified it (<see langword="null" /> unless
    ///     <see cref="AgentCardVerificationStatus.Verified" />)
    ///     and an optional external id.
    /// </summary>
    public PeerIdentity Identity { get; init; } = PeerIdentity.FromCard(Source, Verification);
}

/// <summary>Untrusted text of one card skill.</summary>
public sealed record ResolvedSkill(
    UntrustedText Name,
    UntrustedText Description,
    IReadOnlyList<UntrustedText> Examples);

/// <summary>Raised when an origin serves a card whose canonical content differs from the pinned one.</summary>
public sealed class CardChangedEventArgs(string origin, string oldHash, string newHash) : EventArgs
{
    /// <summary>Origin (<c>https://host[:port]</c>) that served the changed card.</summary>
    public string Origin { get; } = origin;

    /// <summary>Hash of the pinned card.</summary>
    public string OldHash { get; } = oldHash;

    /// <summary>Hash of the card that was received.</summary>
    public string NewHash { get; } = newHash;
}

/// <summary>
///     Fetches Agent Cards from <c>/.well-known/agent-card.json</c> (spec §8.2) with HTTP caching (ETag / max-age, §8.6),
///     signature verification against a <see cref="CardTrustPolicy" /> (§8.4, §7.5) and per-origin card pinning.
/// </summary>
public sealed class A2ACardResolver
{
    // Spec: metrics live under the "A2A" meter.
    private static readonly Meter Meter = new("A2A");

    private static readonly Counter<long> CardChangedCounter =
        Meter.CreateCounter<long>("a2a.card.changed",
            description: "Agent Cards whose content differs from the pinned card of their origin.");

    private static readonly Counter<long> CardUnsignedCounter =
        Meter.CreateCounter<long>("a2a.card.unsigned",
            description: "Unsigned Agent Cards returned because signatures are not required.");

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    /// <summary>Most origins remembered for the unverified-card warning, so memory stays bounded.</summary>
    private const int MaxWarnedOrigins = 1024;

    /// <summary>Origins whose unsigned or unverified card was already logged (spec 8.4.3), once each.</summary>
    private readonly ConcurrentDictionary<string, byte> _warnedUntrusted = new(StringComparer.Ordinal);

    /// <summary>Origins whose card lacked a REQUIRED field and was already logged (spec 5.7), once each.</summary>
    private readonly ConcurrentDictionary<string, byte> _warnedIncomplete = new(StringComparer.Ordinal);

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly A2ACardResolverOptions _options;
    private readonly AgentCardVerifier _verifier;

    /// <summary>Creates a resolver. <paramref name="options" /> defaults to a new <see cref="A2ACardResolverOptions" />.</summary>
    public A2ACardResolver(HttpClient httpClient, A2ACardResolverOptions? options = null, ILogger? logger = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new A2ACardResolverOptions();
        ArgumentNullException.ThrowIfNull(_options.Trust, "options.Trust");
        ArgumentNullException.ThrowIfNull(_options.PinStore, "options.PinStore");
        ArgumentNullException.ThrowIfNull(_options.IdentityResolver, "options.IdentityResolver");
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxCardTextLength, 1, "options.MaxCardTextLength");
        // jku targets are chosen by the card, so they are fetched through the SSRF guard. The policy is mutable and read live;
        // the verifier's per-origin allowlist check still prevents any fetch for a host nobody allowed.
#if NETSTANDARD2_0
        // No SSRF guard on netstandard2.0, so no jku fetch: a signature that needs one is Unverified.
        _verifier = new AgentCardVerifier(_options.Trust);
#else
        _verifier = new AgentCardVerifier(_options.Trust, new GuardedJwksFetcher(_options.JwksHttpClient));
#endif
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised when a card's content differs from the pinned card of its origin (unless <see cref="PinPolicy.Off" />).</summary>
    public event EventHandler<CardChangedEventArgs>? CardChanged;

    /// <summary>Accepts an agent base URL (well-known path is appended) or a full card URL.</summary>
    public static Uri GetCardUri(Uri agentUrl)
    {
        if (agentUrl.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return agentUrl;
        return new Uri(agentUrl, A2AProtocol.WellKnownAgentCardPath);
    }

    /// <summary>
    ///     Fetches the card at <paramref name="agentUrl" /> after the signature and pin checks. Same as
    ///     <see cref="ResolveAsync" /> but returns only the card.
    /// </summary>
    public async Task<AgentCard> GetAgentCardAsync(Uri agentUrl, CancellationToken cancellationToken = default)
    {
        return (await ResolveAsync(agentUrl, cancellationToken).ConfigureAwait(false)).Card;
    }

    /// <summary>
    ///     Fetches the card at <paramref name="agentUrl" />, checks its signatures and pin, and returns the card with its
    ///     verification result and sanitized text.
    /// </summary>
    public async Task<ResolvedAgentCard> ResolveAsync(Uri agentUrl, CancellationToken callerToken = default)
    {
        var uri = GetCardUri(agentUrl);
        if (_options.RequireHttps && uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            throw new A2AException(A2AErrorCode.InvalidRequest, $"Agent Card URL must use HTTPS: {uri}");

        var timeout = _options.Timeout;
        if (timeout == Timeout.InfiniteTimeSpan) return await ResolveCoreAsync(uri, callerToken).ConfigureAwait(false);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await ResolveCoreAsync(uri, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested &&
                                                    !callerToken.IsCancellationRequested)
        {
            // Same shape as the call timeout of the transports (and HttpClient.Timeout): a TaskCanceledException with a
            // TimeoutException inside, so it can be told from the caller's own cancellation.
            var message = $"The Agent Card request for {uri} timed out after {timeout.TotalSeconds:0.###} s.";
#if NETSTANDARD2_0
            throw new TaskCanceledException(message, new TimeoutException(null, ex));
#else
            throw new TaskCanceledException(message, new TimeoutException(null, ex), timeoutCts.Token);
#endif
        }
    }

    private async Task<ResolvedAgentCard> ResolveCoreAsync(Uri uri, CancellationToken cancellationToken)
    {
        var key = uri.AbsoluteUri;
        _cache.TryGetValue(key, out var cached);

        // The trust policy is mutable. A cached card is checked against it again, so a removed key stops working at once.
        if (cached is not null && !await StillAcceptedAsync(cached, uri, cancellationToken).ConfigureAwait(false))
        {
            _cache.TryRemove(key, out _);
            cached = null;
        }

        if (cached is not null && cached.Expires > DateTimeOffset.UtcNow) return cached.Result;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(A2AProtocol.AgentCardMediaType));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(A2AProtocol.JsonMediaType, 0.9));
        // A server that also speaks v0.3 reads a request without A2A-Version as a v0.3 client (spec 3.6.2) and may serve a
        // card for one: the official A2A .NET SDK's blended card says pushNotifications false when its push is v1-only.
        // This resolver reads v1.0 cards; a v0.3-only server ignores the header.
        request.Headers.TryAddWithoutValidation(A2AProtocol.Headers.Version, A2AProtocol.Version);
        if (cached?.ETag is not null) request.Headers.IfNoneMatch.Add(cached.ETag);
        else if (cached?.LastModified is { } lastModified) request.Headers.IfModifiedSince = lastModified;

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        // Trust is bound to the origin of the card URL. An HttpClient that follows redirects must not let a card served
        // by another origin be verified, pinned and identified as this one's. A redirect within the origin changes nothing.
        if (response.RequestMessage?.RequestUri is { IsAbsoluteUri: true } final &&
            !string.Equals(CardTrustPolicy.OriginOf(final), CardTrustPolicy.OriginOf(uri), StringComparison.Ordinal))
            throw new A2AException(A2AErrorCode.InvalidAgentResponse,
                $"The Agent Card request for {CardTrustPolicy.OriginOf(uri)} was redirected to {CardTrustPolicy.OriginOf(final)}. " +
                "A card is only accepted from the origin it was asked for: use an HttpClient whose handler sets " +
                "AllowAutoRedirect = false, or the card's final URL.");
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            var (expires, store) = Freshness(response);
            if (!store)
            {
                _cache.TryRemove(key, out _);
                return cached.Result;
            }

            var refreshed = cached with { Expires = expires };
            _cache[key] = refreshed;
            return refreshed.Result;
        }

        if (!response.IsSuccessStatusCode)
            throw new A2AException(A2AErrorCode.Internal,
                    $"Failed to fetch Agent Card from {uri}: HTTP {(int)response.StatusCode}")
                { HttpStatus = (int)response.StatusCode };

        // The card is parsed from its UTF-8 bytes (no string round trip); the text is decoded once, for ResolvedAgentCard.Raw.
        string raw;
        JsonObject node;
        AgentCard card;
        using (var body = await ResponseBody.ReadUtf8Async(response.Content, cancellationToken).ConfigureAwait(false))
        {
            raw = Encoding.UTF8.GetString(body.Span);
            node = ParseCard(body.Span);
            var normalizer = _options.CardNormalizer;
            if (normalizer is null && LegacyCardVersion(node) is { } legacy)
                throw new A2AException(A2AErrorCode.InvalidAgentResponse,
                    $"The Agent Card at {uri} is protocol version {TextSanitizer.ForMessage(legacy)}, older than 1.0. Call it " +
                    "with V03CompatClientFactory from the RomanAmbinder.A2A.Client.V0_3Compat package, which reads v0.2.x and " +
                    "v0.3 cards.");
            if (normalizer is not null)
                try
                {
                    node = normalizer(node);
                }
                catch (JsonException ex)
                {
                    throw new A2AException(A2AErrorCode.InvalidAgentResponse,
                        $"Malformed Agent Card: {TextSanitizer.ForMessage(ex.Message)}", ex);
                }

            try
            {
                card = normalizer is null
                    ? JsonSerializer.Deserialize(body.Span, A2AJson.GetTypeInfo<AgentCard>()) ??
                      throw new JsonException("Null AgentCard payload.")
                    : A2AJson.Deserialize<AgentCard>(node.ToJsonString());
            }
            catch (JsonException ex)
            {
                throw new A2AException(A2AErrorCode.InvalidAgentResponse,
                    $"Malformed Agent Card: {TextSanitizer.ForMessage(ex.Message)}", ex);
            }
        }

        var verification = await _verifier.VerifyAsync(node, uri, cancellationToken).ConfigureAwait(false);
        if (IsRejected(verification)) throw new AgentCardVerificationException(verification);
        if (verification.Status == AgentCardVerificationStatus.Verified) card = FromSignedContent(node);

        var origin = CardTrustPolicy.OriginOf(uri);
        if (verification.Status == AgentCardVerificationStatus.Unsigned)
            CardUnsignedCounter.Add(1, new KeyValuePair<string, object?>("origin", origin));
        if (verification.Status is AgentCardVerificationStatus.Unsigned or AgentCardVerificationStatus.Unverified
            && _warnedUntrusted.Count < MaxWarnedOrigins && _warnedUntrusted.TryAdd(origin, 0))
            ClientLog.CardNotVerified(_logger, origin, verification.Status,
                _options.Trust.RequireSignature == false
                    ? "CardTrustPolicy.RequireSignature is false"
                    : "no trusted keys or jku hosts are configured in CardTrustPolicy");
        if (_warnedIncomplete.Count < MaxWarnedOrigins && MissingRequired(card) is { Count: > 0 } missing
            && _warnedIncomplete.TryAdd(origin, 0))
            ClientLog.CardMissingRequiredFields(_logger, origin, string.Join(", ", missing));
        var keyId = verification.Status == AgentCardVerificationStatus.Verified ? verification.KeyId : null;
        await CheckPinAsync(origin, node, keyId, cancellationToken).ConfigureAwait(false);

        var baseline = PeerIdentity.FromCard(uri, verification);
        var identity = await _options.IdentityResolver
            .ResolveAsync(new PeerIdentityContext(card, uri, verification, baseline), cancellationToken)
            .ConfigureAwait(false);
        if (identity is null || identity.Origin != baseline.Origin || identity.KeyId != baseline.KeyId)
            throw new InvalidOperationException(
                $"{_options.IdentityResolver.GetType().Name} must keep the card origin and signing key of the baseline identity; it may only add ExternalId.");

        var max = _options.MaxCardTextLength;
        var result = new ResolvedAgentCard(card, raw, uri, verification)
        {
            Description = new UntrustedText(card.Description, max),
            Skills = card.Skills.Select(s => new ResolvedSkill(
                new UntrustedText(s.Name, max),
                new UntrustedText(s.Description, max),
                (s.Examples ?? []).Select(e => new UntrustedText(e, max)).ToList())).ToList(),
            Identity = identity
        };
        // Without a normalizer the tree is the parsed received text, which a cache hit checks again: keep it, not the text.
        var (freshUntil, keep) = Freshness(response);
        if (keep)
            _cache[key] = new CacheEntry(result, response.Headers.ETag, freshUntil)
            {
                Parsed = _options.CardNormalizer is null ? node : null,
                LastModified = response.Content.Headers.LastModified
            };
        else
            _cache.TryRemove(key, out _); // no-store: not kept, and no validator is sent next time
        return result;
    }

    /// <summary>
    ///     The version of a card older than 1.0, or null for a 1.x card: a card without <c>supportedInterfaces</c> whose
    ///     <c>protocolVersion</c> is <c>0.x</c>, or that has none but a top-level <c>url</c> (v0.2 before 0.2.5). Neither
    ///     member is in the v1.0 card, and a blended card (v1.0 plus v0.3 members) has <c>supportedInterfaces</c>.
    /// </summary>
    internal static string? LegacyCardVersion(JsonObject card)
    {
        if (card["supportedInterfaces"] is JsonArray { Count: > 0 }) return null;
        if (card["protocolVersion"] is JsonValue version && version.TryGetValue<string>(out var text))
            return text.StartsWith("0.", StringComparison.Ordinal) ? text : null;
        return card["url"] is JsonValue url && url.TryGetValue<string>(out _) ? "0.2 (no protocolVersion)" : null;
    }

    /// <summary>
    ///     The REQUIRED card fields (spec 5.7, docs/a2a.proto) that are empty or missing. The card is still used: cards are
    ///     read leniently, so a card from another SDK that leaves one out keeps working.
    /// </summary>
    internal static List<string> MissingRequired(AgentCard card)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(card.Name)) missing.Add("name");
        if (string.IsNullOrWhiteSpace(card.Description)) missing.Add("description");
        if (string.IsNullOrWhiteSpace(card.Version)) missing.Add("version");
        if (card.SupportedInterfaces is not { Count: > 0 }) missing.Add("supportedInterfaces");
        else
            for (var i = 0; i < card.SupportedInterfaces.Count; i++)
            {
                var itf = card.SupportedInterfaces[i];
                if (string.IsNullOrWhiteSpace(itf?.Url)) missing.Add($"supportedInterfaces[{i}].url");
                if (string.IsNullOrWhiteSpace(itf?.ProtocolBinding)) missing.Add($"supportedInterfaces[{i}].protocolBinding");
            }

        if (card.DefaultInputModes is not { Count: > 0 }) missing.Add("defaultInputModes");
        if (card.DefaultOutputModes is not { Count: > 0 }) missing.Add("defaultOutputModes");
        if (card.Skills is not { Count: > 0 }) missing.Add("skills");
        else
            for (var i = 0; i < card.Skills.Count; i++)
            {
                var skill = card.Skills[i];
                if (string.IsNullOrWhiteSpace(skill?.Id)) missing.Add($"skills[{i}].id");
                if (string.IsNullOrWhiteSpace(skill?.Name)) missing.Add($"skills[{i}].name");
                if (string.IsNullOrWhiteSpace(skill?.Description)) missing.Add($"skills[{i}].description");
                if (skill?.Tags is not { Count: > 0 }) missing.Add($"skills[{i}].tags");
            }

        return missing;
    }

    private bool IsRejected(AgentCardVerificationResult verification)
    {
        return verification.Status switch
        {
            AgentCardVerificationStatus.Verified => false,
            AgentCardVerificationStatus.Failed => true,
            _ => _options.Trust.EffectiveRequireSignature
        };
    }

    private async Task<bool> StillAcceptedAsync(CacheEntry entry, Uri uri, CancellationToken cancellationToken)
    {
        var result = entry.Result;
        // The verifier only reads the tree (it canonicalizes a copy), so the cached one is reused instead of parsing the text again.
        var node = entry.Parsed ?? ParseCard(Encoding.UTF8.GetBytes(result.RawJson));
        var now = await _verifier.VerifyAsync(node, uri, cancellationToken).ConfigureAwait(false);
        return now.Status == result.Verification.Status && now.KeyId == result.Verification.KeyId && !IsRejected(now);
    }

    // Parses the card into a fully materialized tree. JsonObject reads lazily and throws ArgumentException on a duplicate
    // member when first touched, which on netstandard2.0 (no AllowDuplicateProperties) would otherwise escape from verification.
    private static JsonObject ParseCard(ReadOnlySpan<byte> raw)
    {
        try
        {
            var node =
                JsonNode.Parse(raw,
                    documentOptions: new JsonDocumentOptions { MaxDepth = A2AJson.MaxJsonDepth }) as JsonObject
                ?? throw new JsonException("Agent Card must be a JSON object.");
            Materialize(node);
            return node;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new A2AException(A2AErrorCode.InvalidAgentResponse,
                $"Malformed Agent Card: {TextSanitizer.ForMessage(ex.Message)}", ex);
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
        try
        {
            return A2AJson.Deserialize<AgentCard>(signed.ToJsonString());
        }
        catch (JsonException ex)
        {
            throw new A2AException(A2AErrorCode.InvalidAgentResponse,
                $"Malformed signed Agent Card content: {TextSanitizer.ForMessage(ex.Message)}", ex);
        }
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
                ClientLog.CardKeyChanged(_logger, origin, keyId, pinned.KeyId);
                await _options.PinStore.SetAsync(origin, pinned with { KeyId = keyId }, cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        ClientLog.CardChanged(_logger, origin, pinned.Hash, hash);
        CardChangedCounter.Add(1, new KeyValuePair<string, object?>("origin", origin));
        CardChanged?.Invoke(this, new CardChangedEventArgs(origin, pinned.Hash, hash));

        if (_options.PinPolicy == PinPolicy.Strict) throw new AgentCardPinException(origin, pinned.Hash, hash);
        await _options.PinStore.SetAsync(origin, new CardPin(hash, keyId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     How long a card response may be used without asking again, as a private cache reads it (RFC 9111): <c>no-store</c>
    ///     is not kept at all; <c>no-cache</c>, <c>Vary: *</c> and (without Cache-Control) <c>Pragma: no-cache</c> are kept
    ///     but revalidated on every use; otherwise the freshness lifetime (<c>max-age</c>, else <c>Expires</c> minus
    ///     <c>Date</c>, else <see cref="A2ACardResolverOptions.DefaultCacheDuration" />) less the response's age (<c>Age</c>, or
    ///     the time since <c>Date</c> when that is larger). <c>s-maxage</c> is for shared caches and is ignored.
    ///     <c>must-revalidate</c> needs nothing: a stale card is never used.
    /// </summary>
    internal (DateTimeOffset Expires, bool Store) Freshness(HttpResponseMessage response)
    {
        return Freshness(response, DateTimeOffset.UtcNow, _options.DefaultCacheDuration);
    }

    internal static (DateTimeOffset Expires, bool Store) Freshness(HttpResponseMessage response, DateTimeOffset now,
        TimeSpan defaultLifetime)
    {
        var cc = response.Headers.CacheControl;
        if (cc is { NoStore: true }) return (DateTimeOffset.MinValue, false);
        var revalidate = cc is { NoCache: true }
                         || response.Headers.Vary.Contains("*")
                         || (cc is null && response.Headers.Pragma.Any(p =>
                             string.Equals(p.Name, "no-cache", StringComparison.OrdinalIgnoreCase)));
        if (revalidate) return (DateTimeOffset.MinValue, true);

        var date = response.Headers.Date;
        TimeSpan lifetime;
        if (cc?.MaxAge is { } maxAge) lifetime = maxAge;
        else if (response.Content.Headers.Expires is { } expires) lifetime = expires - (date ?? now);
        else lifetime = defaultLifetime;

        var apparentAge = date is { } d && now > d ? now - d : TimeSpan.Zero;
        var age = response.Headers.Age is { } a && a > apparentAge ? a : apparentAge;
        var remaining = lifetime - age;
        return (remaining > TimeSpan.Zero ? now + remaining : DateTimeOffset.MinValue, true);
    }

    private sealed record CacheEntry(ResolvedAgentCard Result, EntityTagHeaderValue? ETag, DateTimeOffset Expires)
    {
        /// <summary>
        ///     The received card as a parsed tree (<see cref="ResolvedAgentCard.RawJson" /> parsed), or null when it must be
        ///     parsed again.
        /// </summary>
        public JsonObject? Parsed { get; init; }

        /// <summary>The response's <c>Last-Modified</c>, sent as <c>If-Modified-Since</c> when there is no entity tag.</summary>
        public DateTimeOffset? LastModified { get; init; }
    }
}

/// <summary>The Agent Card failed signature verification or, with signatures required, is unsigned or unverified.</summary>
public sealed class AgentCardVerificationException(AgentCardVerificationResult result)
    : A2AException(A2AErrorCode.InvalidAgentResponse,
        $"Agent Card signature check failed ({result.Status}): {result.Reason}")
{
    /// <summary>The verification result that caused the failure.</summary>
    public AgentCardVerificationResult Result { get; } = result;
}

/// <summary>With <see cref="PinPolicy.Strict" />, the origin served a card whose content differs from the pinned one.</summary>
public sealed class AgentCardPinException(string origin, string oldHash, string newHash)
    : A2AException(A2AErrorCode.InvalidAgentResponse,
        $"Agent Card from {origin} changed: pinned {oldHash}, received {newHash}")
{
    /// <summary>Origin that served the changed card.</summary>
    public string Origin { get; } = origin;

    /// <summary>Hash of the pinned card.</summary>
    public string OldHash { get; } = oldHash;

    /// <summary>Hash of the card that was received.</summary>
    public string NewHash { get; } = newHash;
}