using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using A2A.Json;
using A2A.Server.Agents;
using A2A.Signing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>
/// Serves the public Agent Card (spec §8.2; the completed card of <see cref="IAgentCardProvider"/>) with caching headers (§8.6)
/// and optional JWS signing (§8.4), per tenant: the card and the signing key come from the <see cref="IAgentCardProvider"/> for
/// the tenant the <see cref="ITenantResolver"/> finds. The encoded body and its entity tag are kept per card instance and key,
/// so the host's card (the same instance on every call) is encoded and signed once; a provider that returns a new card is
/// encoded again, and signatures are also cached by the signed content and the key, so a stable card is signed once.
/// </summary>
internal sealed class AgentCardEndpoint(IAgentCardProvider cards, IOptions<A2AHttpOptions> httpOptions, ILogger<AgentCardEndpoint> logger,
    A2AEndpointRegistry? registry = null)
{
    /// <summary>Most distinct pruned-path warnings remembered, so each is logged once and memory stays bounded.</summary>
    private const int MaxWarnings = 256;

    /// <summary>Most signatures cached. When full, the least recently used one is evicted.</summary>
    private const int MaxSignatures = 256;

    private readonly ConditionalWeakTable<AgentCard, Encoded> _encoded = new();
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);
    private readonly LruCache<string, AgentCardSignature> _signatures = new(MaxSignatures, StringComparer.Ordinal);

    /// <summary>The served bytes of one card with one key, and their entity tag.</summary>
    private sealed record Encoded(AgentCardSigningKey? Key, byte[] Body, string Tag, string ETag);

    public async Task GetCardAsync(HttpContext http)
    {
        var tenant = Tenant(http);
        var card = await cards.GetAsync(new AgentRequestScope(tenant, HttpPlumbing.RouteValues(http)), http.RequestAborted).ConfigureAwait(false);
        if (card is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var key = await SigningKeyAsync(tenant, http.RequestAborted).ConfigureAwait(false);
        var encoded = Encode(card, key);
        http.Response.Headers.ETag = encoded.ETag;
        http.Response.Headers.Vary = "Accept"; // the media type follows the Accept header
        http.Response.Headers.CacheControl = $"public, max-age={(int)httpOptions.Value.AgentCardMaxAge.TotalSeconds}";
        if (Matches(http.Request.Headers.IfNoneMatch, encoded.Tag))
        {
            http.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        http.Response.ContentType = CardMediaType(http.Request);
        await http.Response.Body.WriteAsync(encoded.Body, http.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="A2AProtocol.AgentCardMediaType"/> when the request's <c>Accept</c> lists it (with a weight above 0), else
    /// <see cref="A2AProtocol.JsonMediaType"/>: a client that does not know the card media type keeps getting plain JSON.
    /// </summary>
    internal static string CardMediaType(HttpRequest request)
    {
        var accept = request.GetTypedHeaders().Accept;
        foreach (var range in accept)
        {
            if (range.Quality is 0) continue;
            if (range.MediaType.Equals(A2AProtocol.AgentCardMediaType, StringComparison.OrdinalIgnoreCase)) return A2AProtocol.AgentCardMediaType;
        }
        return A2AProtocol.JsonMediaType;
    }

    /// <summary>The body and tag of <paramref name="card"/> with <paramref name="key"/>, kept while the card instance lives.</summary>
    private Encoded Encode(AgentCard card, AgentCardSigningKey? key)
    {
        if (_encoded.TryGetValue(card, out var cached) && ReferenceEquals(cached.Key, key)) return cached;
        if (card.SupportedInterfaces.Count == 0)
            throw new InvalidOperationException(
                "The Agent Card lists no SupportedInterfaces and none could be completed from the mapped bindings. Map a binding with MapA2A, "
                + "MapA2AJsonRpc, MapA2AHttpJson or MapA2AGrpc at a pattern without a {tenant} segment (on the application root, not on a group), "
                + "or list the interfaces in A2AServerOptions.AgentCard.");
        // A protocol adapter (v0.3 compat) may add members to the body. The tag covers the transformed bytes.
        var transform = registry?.CardBodyTransform;
        var unsigned = A2AJson.SerializeToUtf8Bytes(card);
        if (transform is not null) unsigned = transform(unsigned);
        var tag = OpaqueTag(unsigned, key);
        var body = key is null ? unsigned : A2AJson.SerializeToUtf8Bytes(Sign(card, key));
        if (key is not null && transform is not null) body = transform(body);
        // A signed card is equivalent, not byte-identical, across signings: a weak validator.
        var encoded = new Encoded(key, body, tag, key is null ? tag : "W/" + tag);
        _encoded.AddOrUpdate(card, encoded);
        return encoded;
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
        await cards.GetSigningKeyAsync(tenant, ct).ConfigureAwait(false) ?? httpOptions.Value.SigningKey;

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
            _signatures.Set(cacheKey, signature);
        }
        var signed = JsonSerializer.Deserialize(A2AJson.SerializeToUtf8Bytes(card), A2AJson.GetTypeInfo<AgentCard>())!; // a copy: the card is shared
        signed.Signatures = [.. card.Signatures ?? [], signature];
        return signed;
    }

    private void WarnPruned(AgentCard card)
    {
        var pruned = AgentCardCanonicalizer.PrunedPaths(AgentCardCanonicalizer.ToNode(card));
        if (pruned.Count == 0) return;
        var paths = string.Join(", ", pruned);
        if (_warned.Count < MaxWarnings && _warned.TryAdd(paths, 0))
            AspNetCoreLog.CardSignatureSkipsPrunedValues(logger, paths);
    }
}
