using System.Security.Cryptography;
using A2A.Json;
using A2A.Server;
using A2A.Server.Agents;
using A2A.Server.AspNetCore;
using A2A.Signing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace A2A.Tests;

/// <summary>
/// The card endpoint caches signatures by signed content and key, with a size cap. A card served often keeps its cached
/// signature while many other cards pass through (least recently used eviction), instead of being signed again whenever the
/// cache fills up.
/// </summary>
public sealed class CardSignatureCacheTests
{
    /// <summary>A new card instance on every call (so the per-instance body cache never hits), one per tenant.</summary>
    private sealed class NewCardEachCall(AgentCardSigningKey key) : IAgentCardProvider
    {
        public AgentCard Get() => throw new NotSupportedException();

        public ValueTask<AgentCard?> GetAsync(AgentRequestScope scope, CancellationToken cancellationToken)
        {
            var card = TestAgentHost.DefaultCard();
            card.Name = "agent " + scope.Tenant;
            return new(card);
        }

        public ValueTask<AgentCardSigningKey?> GetSigningKeyAsync(string tenant, CancellationToken cancellationToken) => new(key);
    }

    private static readonly IServiceProvider NoServices = new ServiceCollection().BuildServiceProvider();

    /// <summary>The signature value of the card served for <paramref name="tenant"/> (ECDSA: a new signing gives a new value).</summary>
    private static async Task<string> SignatureAsync(AgentCardEndpoint endpoint, string tenant)
    {
        var http = new DefaultHttpContext { RequestServices = NoServices };
        http.Request.RouteValues["tenant"] = tenant;
        using var body = new MemoryStream();
        http.Response.Body = body;
        await endpoint.GetCardAsync(http);
        var card = A2AJson.Deserialize<AgentCard>(System.Text.Encoding.UTF8.GetString(body.ToArray()));
        return Assert.Single(card.Signatures!).Signature;
    }

    [Fact]
    public async Task Card_served_often_keeps_its_signature_while_other_cards_fill_the_cache()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var endpoint = new AgentCardEndpoint(new NewCardEachCall(new AgentCardSigningKey(ec, "k1", "ES256")),
            Options.Create(new A2AHttpOptions()), NullLogger<AgentCardEndpoint>.Instance);

        var hot = await SignatureAsync(endpoint, "hot");
        for (var i = 0; i < 600; i++)
        {
            await SignatureAsync(endpoint, "t" + i);
            Assert.Equal(hot, await SignatureAsync(endpoint, "hot")); // never evicted: it is always the most recent
        }
    }
}

public sealed class LruCacheTests
{
    [Fact]
    public void Full_cache_evicts_the_least_recently_used_entry()
    {
        var cache = new LruCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        Assert.True(cache.TryGetValue("a", out _)); // a is now the most recent
        cache.Set("c", 3);                          // evicts b

        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGetValue("b", out _));
        Assert.True(cache.TryGetValue("a", out var a));
        Assert.Equal(1, a);
        Assert.True(cache.TryGetValue("c", out _));
    }

    [Fact]
    public void Replacing_an_entry_does_not_evict_another()
    {
        var cache = new LruCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.Set("a", 10);
        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGetValue("b", out _));
        Assert.True(cache.TryGetValue("a", out var a));
        Assert.Equal(10, a);
    }
}
