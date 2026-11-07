using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Json;
using A2A.Net;
using A2A.Validation;

namespace A2A.Signing;

/// <summary>Outcome of checking an Agent Card's signatures against a <see cref="CardTrustPolicy"/> (spec §7.5).</summary>
public enum AgentCardVerificationStatus
{
    /// <summary>A signature verified with a trusted key.</summary>
    Verified,

    /// <summary>The card has no signatures.</summary>
    Unsigned,

    /// <summary>Signatures are present but no trusted key applies to any of them. A forged card with an attacker <c>jku</c> lands here.</summary>
    Unverified,

    /// <summary>A trusted key applies and the signature does not verify, or a signature header is invalid (<c>typ</c>, <c>alg</c>, <c>crit</c>, encoding).</summary>
    Failed,
}

/// <summary>Result of <see cref="AgentCardVerifier"/>.</summary>
/// <param name="Status">The outcome.</param>
/// <param name="KeyId">The <c>kid</c> of the signature that verified, or of the signature the outcome is about.</param>
/// <param name="Issuer">The card origin (<see cref="CardTrustPolicy.OriginOf"/>) trust was evaluated for.</param>
/// <param name="Reason">Why the card is not <see cref="AgentCardVerificationStatus.Verified"/>; <see langword="null"/> when it is.</param>
public sealed record AgentCardVerificationResult(AgentCardVerificationStatus Status, string? KeyId, string? Issuer, string? Reason);

/// <summary>
/// Verifies Agent Card JWS signatures (spec §8.4.3, §7.5) against a <see cref="CardTrustPolicy"/>: the key must be pinned for the
/// card's origin or come from an allowlisted <c>jku</c> host (HTTPS, SSRF-guarded fetch). Also enforces a <c>typ</c> and
/// algorithm allowlist (no "none"/HS*), rejects <c>crit</c>, and checks key/algorithm matching. The payload is
/// <see cref="AgentCardCanonicalizer.Canonicalize(JsonObject)"/>, which matches a2a-python and a2a-js.
/// </summary>
/// <remarks>
/// With several signatures, one that verifies with a trusted key makes the card <see cref="AgentCardVerificationStatus.Verified"/>
/// (others may use keys or algorithms this client does not know). Otherwise any failed signature makes it
/// <see cref="AgentCardVerificationStatus.Failed"/>, and if none failed it is <see cref="AgentCardVerificationStatus.Unverified"/>.
/// </remarks>
public sealed class AgentCardVerifier
{
    private const int MaxJwksBytes = 256 * 1024;

    /// <summary>Distinct <c>jku</c> key sets one verification may fetch; further signatures needing a fetch get no key.</summary>
    internal const int MaxJkuFetchesPerVerification = 5;

    private static readonly TimeSpan JwksCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan JwksFailureCacheDuration = TimeSpan.FromSeconds(60);

    // jku targets are chosen by the card, so the fetch goes through the same SSRF guard as push webhooks.
    private static readonly Lazy<HttpClient> GuardedJwksClient = new(() =>
        new HttpClient(PrivateAddressGuard.CreateHandler(allowPrivateNetworks: false, "JWKS"))
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = MaxJwksBytes,
        });

    private readonly HttpClient _jwksClient;
    // A failed fetch is cached too (Set null, Failure set) so a card cannot make every verification hit the host again.
    private readonly ConcurrentDictionary<string, JwksCacheEntry> _jwksCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a verifier. <c>jku</c> key sets are fetched through an SSRF-guarded client, cached for 10 minutes (failures for
    /// 60 seconds), and at most <see cref="MaxJkuFetchesPerVerification"/> distinct key sets are fetched per verification.
    /// </summary>
    public AgentCardVerifier(CardTrustPolicy policy) : this(policy, null)
    {
    }

    /// <summary>Test seam: fetches <c>jku</c> key sets with <paramref name="jwksClient"/> instead of the guarded client.</summary>
    internal AgentCardVerifier(CardTrustPolicy policy, HttpClient? jwksClient)
    {
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _jwksClient = jwksClient ?? GuardedJwksClient.Value;
    }

    /// <summary>The policy that decides which keys are trusted.</summary>
    public CardTrustPolicy Policy { get; }

    /// <inheritdoc cref="VerifyAsync(JsonObject, Uri, CancellationToken)"/>
    public Task<AgentCardVerificationResult> VerifyAsync(AgentCard card, Uri source, CancellationToken cancellationToken = default) =>
        VerifyAsync(AgentCardCanonicalizer.ToNode(card), source, cancellationToken);

    /// <inheritdoc cref="VerifyAsync(JsonObject, Uri, CancellationToken)"/>
    public Task<AgentCardVerificationResult> VerifyAsync(string rawCardJson, Uri source, CancellationToken cancellationToken = default) =>
        VerifyAsync(JsonNode.Parse(rawCardJson)?.AsObject() ?? throw new JsonException("Agent Card must be a JSON object."), source, cancellationToken);

    /// <summary>Verifies the card exactly as received from <paramref name="source"/>, whose origin selects the trusted keys.</summary>
    /// <remarks>
    /// <b>Security:</b> a <see cref="AgentCardVerificationStatus.Verified"/> result covers only the canonical form.
    /// Members outside the A2A v1.0 Agent Card schema, and values listed by <see cref="AgentCardCanonicalizer.PrunedPaths"/>,
    /// are not covered by the signature and must not be trusted from a signed card. A card that would verify but has a member
    /// that matches a v1.0 field only when case is ignored (<see cref="AgentCardCanonicalizer.CaseVariantMembers"/>) is
    /// <see cref="AgentCardVerificationStatus.Failed"/>.
    /// </remarks>
    public async Task<AgentCardVerificationResult> VerifyAsync(JsonObject card, Uri source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(source);
        var issuer = CardTrustPolicy.OriginOf(source);

        if (card["signatures"] is not JsonArray sigs || sigs.Count == 0)
            return new(AgentCardVerificationStatus.Unsigned, null, issuer, "the card has no signatures");

        var payload = new Lazy<byte[]>(() => AgentCardCanonicalizer.Canonicalize(card));
        var jkuFetches = new HashSet<string>(StringComparer.Ordinal);
        var failed = new List<(string? Kid, string Reason)>();
        var unverified = new List<(string? Kid, string Reason)>();

        for (var i = 0; i < sigs.Count; i++)
        {
            var (outcome, kid, reason) = await CheckAsync(sigs[i], issuer, payload, jkuFetches, cancellationToken).ConfigureAwait(false);
            switch (outcome)
            {
                case AgentCardVerificationStatus.Verified:
                    // A case-insensitive reader would bind these to model properties although the signature does not cover them.
                    var caseVariants = AgentCardCanonicalizer.CaseVariantMembers(card);
                    if (caseVariants.Count > 0)
                    {
                        return new(AgentCardVerificationStatus.Failed, kid, issuer,
                            string.Join("; ", caseVariants.Select(p => $"member '{TextSanitizer.ForMessage(p)}' differs from a v1.0 Agent Card field only by case and is not signed")));
                    }
                    return new(AgentCardVerificationStatus.Verified, kid, issuer, null);
                case AgentCardVerificationStatus.Failed:
                    failed.Add((kid, $"signature[{i}]: {reason}"));
                    break;
                default:
                    unverified.Add((kid, $"signature[{i}]: {reason}"));
                    break;
            }
        }

        return failed.Count > 0
            ? new(AgentCardVerificationStatus.Failed, failed[0].Kid, issuer, string.Join("; ", failed.Concat(unverified).Select(f => f.Reason)))
            : new(AgentCardVerificationStatus.Unverified, unverified[0].Kid, issuer, string.Join("; ", unverified.Select(f => f.Reason)));
    }

    private async Task<(AgentCardVerificationStatus Outcome, string? Kid, string? Reason)> CheckAsync(
        JsonNode? node, string issuer, Lazy<byte[]> payload, HashSet<string> jkuFetches, CancellationToken cancellationToken)
    {
        const AgentCardVerificationStatus Failed = AgentCardVerificationStatus.Failed;

        AgentCardSignature? sig;
        try { sig = node is null ? null : A2AJson.Deserialize<AgentCardSignature>(node.ToJsonString()); }
        catch (JsonException ex) { return (Failed, null, $"malformed ({TextSanitizer.ForMessage(ex.Message)})"); }
        if (sig is null || string.IsNullOrEmpty(sig.Protected) || string.IsNullOrEmpty(sig.Signature))
            return (Failed, null, "missing protected header or signature");

        Dictionary<string, JsonElement>? header;
        byte[] sigBytes;
        try
        {
            header = JsonSerializer.Deserialize(Base64Url.Decode(sig.Protected), A2AJson.GetTypeInfo<Dictionary<string, JsonElement>>());
            sigBytes = Base64Url.Decode(sig.Signature);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return (Failed, null, $"undecodable ({TextSanitizer.ForMessage(ex.Message)})");
        }
        if (header is null) return (Failed, null, "empty header");

        var alg = StringMember(header, "alg");
        var kid = StringMember(header, "kid");
        var typ = StringMember(header, "typ");
        var jku = StringMember(header, "jku");

        // Header values come from the card: sanitize before they reach Reason, exception messages and logs.
        if (typ is null) return (Failed, kid, "missing typ header");
        if (!Policy.AcceptedTyp.Contains(typ)) return (Failed, kid, $"typ '{TextSanitizer.ForMessage(typ)}' not accepted");
        if (alg is null || !Policy.AllowedAlgorithms.Contains(alg) || !JwsAlgorithms.IsSupported(alg))
            return (Failed, kid, $"algorithm '{TextSanitizer.ForMessage(alg)}' not allowed");
        if (header.ContainsKey("crit")) return (Failed, kid, "unsupported 'crit' header");
        if (string.IsNullOrEmpty(kid)) return (AgentCardVerificationStatus.Unverified, null, "missing kid, no trusted key applies");

        var keys = Policy.TrustedKeysFor(issuer, kid);
        string? jkuNote = null;
        if (keys.Count == 0 && jku is not null)
            (keys, jkuNote) = await JkuKeysAsync(jku, kid, issuer, jkuFetches, cancellationToken).ConfigureAwait(false);
        if (keys.Count == 0)
            return (AgentCardVerificationStatus.Unverified, kid,
                $"no trusted key for kid '{TextSanitizer.ForMessage(kid)}' at {issuer}" + (jkuNote is null ? "" : $" ({jkuNote})"));

        foreach (var jwk in keys)
        {
            if (!JwsAlgorithms.KeyMatches(alg, jwk)) continue;
            if (jwk.Alg is not null && jwk.Alg != alg) continue;
            try
            {
                using var pub = jwk.ToPublicKey();
                if (Check(sig.Protected, payload.Value, alg, pub, sigBytes)) return (AgentCardVerificationStatus.Verified, kid, null);
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or CryptographicException)
            {
                // An unusable trusted key cannot verify anything; fall through to the next one.
            }
        }
        return (Failed, kid, $"signature does not verify with trusted key '{TextSanitizer.ForMessage(kid)}'");
    }

    /// <summary>The keys with <paramref name="kid"/> from <paramref name="jku"/>, when its host is allowed for the card origin <paramref name="issuer"/>.</summary>
    private async Task<(IReadOnlyList<Jwk> Keys, string? Note)> JkuKeysAsync(string jku, string kid, string issuer, HashSet<string> fetches, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(jku, UriKind.Absolute, out var uri)) return ([], "jku is not an absolute URL");
        if (uri.Scheme != Uri.UriSchemeHttps) return ([], "jku must use https");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !Policy.IsJkuHostAllowed(issuer, uri.Host))
            return ([], $"jku host '{TextSanitizer.ForMessage(uri.Host)}' is not allowed for {issuer}");

        var cacheKey = uri.AbsoluteUri;
        var now = DateTimeOffset.UtcNow;
        if (!_jwksCache.TryGetValue(cacheKey, out var entry) || entry.Expires < now)
        {
            if (!fetches.Contains(cacheKey) && fetches.Count >= MaxJkuFetchesPerVerification)
                return ([], $"jku fetch limit of {MaxJkuFetchesPerVerification} per card reached");
            fetches.Add(cacheKey);
            try
            {
                var bytes = await _jwksClient.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);
                var set = JsonSerializer.Deserialize(bytes, A2AJson.GetTypeInfo<JwkSet>()) ?? new JwkSet();
                entry = new JwksCacheEntry(DateTimeOffset.UtcNow + JwksCacheDuration, set, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                entry = new JwksCacheEntry(DateTimeOffset.UtcNow + JwksFailureCacheDuration, null, Messages(ex));
            }
            EvictExpired(DateTimeOffset.UtcNow);
            _jwksCache[cacheKey] = entry;
        }
        if (entry.Set is null) return ([], $"jku fetch failed: {entry.Failure}");
        return (entry.Set.Keys.Where(k => string.Equals(k.Kid, kid, StringComparison.Ordinal)).ToList(), null);
    }

    private void EvictExpired(DateTimeOffset now)
    {
        foreach (var (key, cached) in _jwksCache)
        {
            if (cached.Expires < now) _jwksCache.TryRemove(new KeyValuePair<string, JwksCacheEntry>(key, cached));
        }
    }

    // The SSRF guard's reason (e.g. a private address) can sit in an inner exception of the handler's HttpRequestException.
    private static string Messages(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (!parts.Contains(e.Message)) parts.Add(e.Message);
        }
        return TextSanitizer.ForMessage(string.Join(" ", parts));
    }

    private sealed record JwksCacheEntry(DateTimeOffset Expires, JwkSet? Set, string? Failure);

    private static string? StringMember(Dictionary<string, JsonElement> header, string name) =>
        header.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Check(string protectedB64, byte[] payload, string alg, AsymmetricAlgorithm key, byte[] sig)
    {
        var input = Encoding.ASCII.GetBytes(protectedB64 + "." + Base64Url.Encode(payload));
        return JwsAlgorithms.Verify(alg, key, input, sig);
    }
}
