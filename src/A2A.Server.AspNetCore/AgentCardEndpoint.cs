using System.Collections.Concurrent;
using System.Security.Cryptography;
using A2A.Json;
using A2A.Server.Agents;
using A2A.Signing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>
/// Serves the public Agent Card (spec §8.2) with caching headers (§8.6) and optional JWS signing (§8.4), per tenant: the card
/// and the signing key come from the <see cref="AgentCardProvider"/> for the tenant the <see cref="ITenantResolver"/> finds.
/// Cards are never cached across requests, so a provider may change cards at runtime; only signatures are cached, keyed by
/// the signed content and the key, so a stable card is signed once.
/// </summary>
internal sealed class AgentCardEndpoint(IServiceProvider services, IOptions<A2AServerOptions> serverOptions, IOptions<A2AHttpOptions> httpOptions,
    ILogger<AgentCardEndpoint> logger)
{
    /// <summary>Most distinct pruned-path warnings remembered, so each is logged once and memory stays bounded.</summary>
    private const int MaxWarnings = 256;

    /// <summary>Most signatures cached. When full, the cache starts over (a simple size cap).</summary>
    private const int MaxSignatures = 256;

    private readonly AgentCardProvider _cards = services.GetService<AgentCardProvider>() ?? new DefaultAgentCardProvider(serverOptions);
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentCardSignature> _signatures = new(StringComparer.Ordinal);

    public async Task GetCardAsync(HttpContext http)
    {
        var tenant = Tenant(http);
        var card = await _cards.GetCardAsync(new AgentRequestScope(tenant, HttpPlumbing.RouteValues(http)), http.RequestAborted).ConfigureAwait(false);
        if (card is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var key = await SigningKeyAsync(tenant, http.RequestAborted).ConfigureAwait(false);
        var unsigned = A2AJson.SerializeToUtf8Bytes(card);
        var tag = OpaqueTag(unsigned, key);
        // A signed card is equivalent, not byte-identical, across signings: a weak validator.
        http.Response.Headers.ETag = key is null ? tag : "W/" + tag;
        http.Response.Headers.CacheControl = $"public, max-age={(int)httpOptions.Value.AgentCardMaxAge.TotalSeconds}";
        if (Matches(http.Request.Headers.IfNoneMatch, tag))
        {
            http.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        var body = key is null ? unsigned : A2AJson.SerializeToUtf8Bytes(Sign(card, key));
        http.Response.ContentType = A2AProtocol.JsonMediaType;
        await http.Response.Body.WriteAsync(body, http.RequestAborted).ConfigureAwait(false);
    }

    public async Task GetJwksAsync(HttpContext http)
    {
        var key = await SigningKeyAsync(Tenant(http), http.RequestAborted).ConfigureAwait(false);
        if (key is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        http.Response.Headers.CacheControl = $"public, max-age={(int)httpOptions.Value.AgentCardMaxAge.TotalSeconds}";
        http.Response.ContentType = A2AProtocol.JsonMediaType;
        await http.Response.Body.WriteAsync(A2AJson.SerializeToUtf8Bytes(new JwkSet { Keys = [key.ToPublicJwk()] }), http.RequestAborted).ConfigureAwait(false);
    }

    private static readonly RouteTenantResolver RouteTenants = new();

    /// <summary>The tenant from the registered <see cref="ITenantResolver"/> (the <c>tenant</c> route value when none is registered).</summary>
    private static string Tenant(HttpContext http) => (http.RequestServices.GetService<ITenantResolver>() ?? RouteTenants).Resolve(http) ?? "";

    /// <summary>The tenant's key from the provider, else the host's key (<see cref="A2AHttpOptions.SigningKey"/>).</summary>
    private async ValueTask<AgentCardSigningKey?> SigningKeyAsync(string tenant, CancellationToken ct) =>
        await _cards.GetSigningKeyAsync(tenant, ct).ConfigureAwait(false) ?? httpOptions.Value.SigningKey;

    /// <summary>
    /// If-None-Match compares weakly (RFC 9110 §13.1.2): <c>W/"x"</c> and <c>"x"</c> both match the tag <c>"x"</c>, as does <c>*</c>.
    /// </summary>
    private static bool Matches(Microsoft.Extensions.Primitives.StringValues ifNoneMatch, string tag)
    {
        foreach (var header in ifNoneMatch)
        {
            foreach (var raw in (header ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (raw == "*") return true;
                var candidate = raw.StartsWith("W/", StringComparison.Ordinal) ? raw[2..] : raw;
                if (string.Equals(candidate, tag, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The quoted entity tag. It covers the unsigned card and the public signing key, not the signature, which differs on
    /// every signing (ECDSA), so a conditional request still matches while neither changed.
    /// </summary>
    private static string OpaqueTag(byte[] unsignedCard, AgentCardSigningKey? key)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(unsignedCard);
        if (key is not null)
        {
            hash.AppendData("\n"u8);
            hash.AppendData(A2AJson.SerializeToUtf8Bytes(key.ToPublicJwk()));
        }
        return "\"" + Base64Url.Encode(hash.GetHashAndReset())[..22] + "\"";
    }

    /// <summary>
    /// A copy of the card with its signature appended, as <see cref="AgentCardSigner.Sign"/> makes it. The signature covers only
    /// the canonical content, so it is cached by the hash of that content and of the key (public key, kid, alg, jku).
    /// </summary>
    private AgentCard Sign(AgentCard card, AgentCardSigningKey key)
    {
        var canonical = AgentCardCanonicalizer.Canonicalize(AgentCardCanonicalizer.ToNode(card));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(canonical);
        hash.AppendData("\n"u8);
        hash.AppendData(A2AJson.SerializeToUtf8Bytes(key.ToPublicJwk()));
        hash.AppendData("\n"u8);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(key.JwksUrl ?? ""));
        var cacheKey = Base64Url.Encode(hash.GetHashAndReset());
        if (!_signatures.TryGetValue(cacheKey, out var signature))
        {
            WarnPruned(card);
            signature = AgentCardSigner.CreateSignature(card, key);
            if (_signatures.Count >= MaxSignatures) _signatures.Clear();
            _signatures[cacheKey] = signature;
        }
        var signed = A2AJson.Clone(card);
        signed.Signatures = [.. card.Signatures ?? [], signature];
        return signed;
    }

    private void WarnPruned(AgentCard card)
    {
        var pruned = AgentCardCanonicalizer.PrunedPaths(AgentCardCanonicalizer.ToNode(card));
        if (pruned.Count == 0) return;
        var paths = string.Join(", ", pruned);
        if (_warned.Count < MaxWarnings && _warned.TryAdd(paths, 0))
            logger.LogWarning("Agent Card signature does not cover these empty values, which a2a-python/a2a-js canonicalization removes: {PrunedPaths}", paths);
    }
}
