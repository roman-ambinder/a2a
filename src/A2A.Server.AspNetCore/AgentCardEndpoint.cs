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
/// Nothing is cached across requests, so a provider may change cards at runtime.
/// </summary>
internal sealed class AgentCardEndpoint(IServiceProvider services, IOptions<A2AServerOptions> serverOptions, IOptions<A2AHttpOptions> httpOptions,
    ILogger<AgentCardEndpoint> logger)
{
    /// <summary>Most distinct pruned-path warnings remembered, so each is logged once and memory stays bounded.</summary>
    private const int MaxWarnings = 256;

    private readonly AgentCardProvider _cards = services.GetService<AgentCardProvider>() ?? new DefaultAgentCardProvider(serverOptions);
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);

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
        var etag = ETag(unsigned, key);
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = $"public, max-age={(int)httpOptions.Value.AgentCardMaxAge.TotalSeconds}";
        if (http.Request.Headers.IfNoneMatch.Any(v => v == etag || v == "*"))
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
    /// The ETag covers the unsigned card and the public signing key, not the signature, which differs on every signing
    /// (ECDSA), so a conditional request still matches while neither changed.
    /// </summary>
    private static string ETag(byte[] unsignedCard, AgentCardSigningKey? key)
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

    private AgentCard Sign(AgentCard card, AgentCardSigningKey key)
    {
        var pruned = AgentCardCanonicalizer.PrunedPaths(AgentCardCanonicalizer.ToNode(card));
        if (pruned.Count > 0)
        {
            var paths = string.Join(", ", pruned);
            if (_warned.Count < MaxWarnings && _warned.TryAdd(paths, 0))
                logger.LogWarning("Agent Card signature does not cover these empty values, which a2a-python/a2a-js canonicalization removes: {PrunedPaths}", paths);
        }
        return AgentCardSigner.Sign(card, key);
    }
}
