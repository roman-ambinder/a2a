using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Json;

namespace A2A.Signing;

public enum AgentCardVerificationStatus
{
    /// <summary>At least the required number of signatures verified.</summary>
    Valid,

    /// <summary>The card has no signatures.</summary>
    Unsigned,

    /// <summary>Signatures exist but none (or too few) verified.</summary>
    Invalid,
}

public sealed record AgentCardVerificationResult(AgentCardVerificationStatus Status, IReadOnlyList<string> VerifiedKeyIds, IReadOnlyList<string> Errors)
{
    public bool IsValid => Status == AgentCardVerificationStatus.Valid;
}

/// <summary>What the verifier knows when asking for a key.</summary>
public sealed record KeyResolutionContext(string KeyId, string Algorithm, string? JwksUrl, Uri? CardSource);

/// <summary>Finds public keys for Agent Card signatures. Implementations decide what is trusted.</summary>
public interface IAgentCardKeyResolver
{
    ValueTask<IReadOnlyList<Jwk>> ResolveAsync(KeyResolutionContext context, CancellationToken cancellationToken = default);
}

/// <summary>Trusted key store: keys configured out of band. Strongest option.</summary>
public sealed class StaticKeyResolver : IAgentCardKeyResolver
{
    private readonly ConcurrentDictionary<string, Jwk> _keys = new(StringComparer.Ordinal);

    public StaticKeyResolver(IEnumerable<Jwk>? keys = null)
    {
        foreach (var k in keys ?? []) Add(k);
    }

    public void Add(Jwk key) => _keys[key.Kid ?? throw new ArgumentException("Trusted keys need a kid.")] = key;

    /// <summary>Removes a key, e.g. after revocation.</summary>
    public bool Remove(string kid) => _keys.TryRemove(kid, out _);

    public ValueTask<IReadOnlyList<Jwk>> ResolveAsync(KeyResolutionContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<Jwk>>(_keys.TryGetValue(context.KeyId, out var k) ? [k] : []);
}

public sealed class JwksKeyResolverOptions
{
    /// <summary>Reject non-HTTPS <c>jku</c> URLs. Default true.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// Only follow a <c>jku</c> whose host equals the host the card was fetched from (or a host in <see cref="AllowedHosts"/>).
    /// Stops a tampered card from pointing at an attacker's key set. Default true.
    /// </summary>
    public bool RequireSameHostAsCard { get; set; } = true;

    public ISet<string> AllowedHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>Fetches keys from the <c>jku</c> URL in the protected header, with origin checks and caching.</summary>
public sealed class JwksKeyResolver(HttpClient httpClient, JwksKeyResolverOptions? options = null) : IAgentCardKeyResolver
{
    private readonly JwksKeyResolverOptions _options = options ?? new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, JwkSet Set)> _cache = new();

    public async ValueTask<IReadOnlyList<Jwk>> ResolveAsync(KeyResolutionContext context, CancellationToken cancellationToken = default)
    {
        if (context.JwksUrl is null || !Uri.TryCreate(context.JwksUrl, UriKind.Absolute, out var jku)) return [];
        if (_options.RequireHttps && jku.Scheme != Uri.UriSchemeHttps) return [];
        var hostOk = _options.AllowedHosts.Contains(jku.Host)
                     || (!_options.RequireSameHostAsCard)
                     || (context.CardSource is not null && string.Equals(context.CardSource.Host, jku.Host, StringComparison.OrdinalIgnoreCase));
        if (!hostOk) return [];

        var key = jku.AbsoluteUri;
        if (!_cache.TryGetValue(key, out var entry) || entry.Expires < DateTimeOffset.UtcNow)
        {
            var set = await httpClient.GetFromJsonAsync<JwkSet>(jku, cancellationToken).ConfigureAwait(false) ?? new JwkSet();
            entry = (DateTimeOffset.UtcNow + _options.CacheDuration, set);
            _cache[key] = entry;
        }
        return entry.Set.Keys.Where(k => k.Kid == context.KeyId).ToList();
    }
}

/// <summary>Tries resolvers in order and returns the first non-empty result.</summary>
public sealed class CompositeKeyResolver(params IAgentCardKeyResolver[] resolvers) : IAgentCardKeyResolver
{
    public async ValueTask<IReadOnlyList<Jwk>> ResolveAsync(KeyResolutionContext context, CancellationToken cancellationToken = default)
    {
        foreach (var r in resolvers)
        {
            var keys = await r.ResolveAsync(context, cancellationToken).ConfigureAwait(false);
            if (keys.Count > 0) return keys;
        }
        return [];
    }
}

public sealed class AgentCardVerifierOptions
{
    /// <summary>Accepted algorithms. Defaults to all asymmetric algorithms in <see cref="JwsAlgorithms.All"/>.</summary>
    public ISet<string> AllowedAlgorithms { get; } = new HashSet<string>(JwsAlgorithms.All, StringComparer.Ordinal);

    /// <summary>Number of distinct keys that must verify. Default 1.</summary>
    public int RequiredValidSignatures { get; set; } = 1;
}

/// <summary>
/// Verifies Agent Card JWS signatures (spec §8.4.3), with stricter rules than the spec requires:
/// algorithm allowlist (no "none"/HS*), rejection of unknown <c>crit</c> headers, key/algorithm matching,
/// <c>jku</c> origin checks (via <see cref="JwksKeyResolver"/>). The payload is <see cref="AgentCardCanonicalizer.Canonicalize(JsonObject)"/>,
/// which matches a2a-python and a2a-js.
/// </summary>
public sealed class AgentCardVerifier(IAgentCardKeyResolver keyResolver, AgentCardVerifierOptions? options = null)
{
    private readonly AgentCardVerifierOptions _options = options ?? new();

    public Task<AgentCardVerificationResult> VerifyAsync(AgentCard card, Uri? source = null, CancellationToken cancellationToken = default) =>
        VerifyAsync(AgentCardCanonicalizer.ToNode(card), source, cancellationToken);

    /// <summary>Verifies the card exactly as received.</summary>
    /// <remarks>
    /// <b>Security:</b> a <see cref="AgentCardVerificationStatus.Valid"/> result covers only the canonical form.
    /// Members outside the A2A v1.0 Agent Card schema, and values listed by <see cref="AgentCardCanonicalizer.PrunedPaths"/>,
    /// are not covered by the signature and must not be trusted from a signed card. A signed card with a member that
    /// matches a v1.0 field only when case is ignored (<see cref="AgentCardCanonicalizer.CaseVariantMembers"/>) is
    /// <see cref="AgentCardVerificationStatus.Invalid"/>.
    /// </remarks>
    public Task<AgentCardVerificationResult> VerifyAsync(string rawCardJson, Uri? source = null, CancellationToken cancellationToken = default) =>
        VerifyAsync(JsonNode.Parse(rawCardJson)?.AsObject() ?? throw new JsonException("Agent Card must be a JSON object."), source, cancellationToken);

    /// <inheritdoc cref="VerifyAsync(string, Uri?, CancellationToken)"/>
    public async Task<AgentCardVerificationResult> VerifyAsync(JsonObject card, Uri? source = null, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var verified = new List<string>();

        if (card["signatures"] is not JsonArray sigs || sigs.Count == 0)
            return new(AgentCardVerificationStatus.Unsigned, verified, errors);

        // A case-insensitive reader would bind these to model properties although the signature does not cover them.
        var caseVariants = AgentCardCanonicalizer.CaseVariantMembers(card);
        if (caseVariants.Count > 0)
        {
            errors.AddRange(caseVariants.Select(p => $"member '{p}' differs from a v1.0 Agent Card field only by case and is not signed"));
            return new(AgentCardVerificationStatus.Invalid, verified, errors);
        }

        byte[]? payload = null;

        for (var i = 0; i < sigs.Count; i++)
        {
            AgentCardSignature? sig;
            try { sig = sigs[i]?.Deserialize<AgentCardSignature>(A2AJson.Options); }
            catch (JsonException ex) { errors.Add($"signature[{i}]: malformed ({ex.Message})"); continue; }
            if (sig is null || string.IsNullOrEmpty(sig.Protected) || string.IsNullOrEmpty(sig.Signature))
            {
                errors.Add($"signature[{i}]: missing protected header or signature");
                continue;
            }

            Dictionary<string, JsonElement>? header;
            byte[] sigBytes;
            try
            {
                header = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Base64Url.Decode(sig.Protected));
                sigBytes = Base64Url.Decode(sig.Signature);
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                errors.Add($"signature[{i}]: undecodable ({ex.Message})");
                continue;
            }
            if (header is null) { errors.Add($"signature[{i}]: empty header"); continue; }

            var alg = header.TryGetValue("alg", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            var kid = header.TryGetValue("kid", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            var jku = header.TryGetValue("jku", out var j) && j.ValueKind == JsonValueKind.String ? j.GetString() : null;

            if (alg is null || !_options.AllowedAlgorithms.Contains(alg) || !JwsAlgorithms.IsSupported(alg))
            {
                errors.Add($"signature[{i}]: algorithm '{alg}' not allowed");
                continue;
            }
            if (header.ContainsKey("crit"))
            {
                errors.Add($"signature[{i}]: unsupported 'crit' header");
                continue;
            }
            if (string.IsNullOrEmpty(kid))
            {
                errors.Add($"signature[{i}]: missing kid");
                continue;
            }

            var keys = await keyResolver.ResolveAsync(new KeyResolutionContext(kid, alg, jku, source), cancellationToken).ConfigureAwait(false);
            if (keys.Count == 0)
            {
                errors.Add($"signature[{i}]: no trusted key for kid '{kid}'");
                continue;
            }

            payload ??= AgentCardCanonicalizer.Canonicalize(card);
            var ok = false;
            foreach (var jwk in keys)
            {
                if (!JwsAlgorithms.KeyMatches(alg, jwk)) continue;
                if (jwk.Alg is not null && jwk.Alg != alg) continue;
                using var pub = jwk.ToPublicKey();
                if (Check(sig.Protected, payload, alg, pub, sigBytes))
                {
                    ok = true;
                    break;
                }
            }
            if (ok) { if (!verified.Contains(kid)) verified.Add(kid); }
            else errors.Add($"signature[{i}]: verification failed for kid '{kid}'");
        }

        var status = verified.Count >= Math.Max(1, _options.RequiredValidSignatures)
            ? AgentCardVerificationStatus.Valid
            : AgentCardVerificationStatus.Invalid;
        return new(status, verified, errors);
    }

    private static bool Check(string protectedB64, byte[] payload, string alg, System.Security.Cryptography.AsymmetricAlgorithm key, byte[] sig)
    {
        var input = Encoding.ASCII.GetBytes(protectedB64 + "." + Base64Url.Encode(payload));
        return JwsAlgorithms.Verify(alg, key, input, sig);
    }
}
