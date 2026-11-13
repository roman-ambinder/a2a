using System.Collections.Concurrent;
using System.Net;
using System.Text;
using A2A.Client;
using A2A.Json;

namespace A2A.Tests;

/// <summary>
///     Agent Card caching (spec 8.6): the client reads response freshness as a private cache under RFC 9111, and the
///     public card endpoint sends Last-Modified.
/// </summary>
public class CardCachingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Default = TimeSpan.FromMinutes(5);

    private static HttpResponseMessage Response(Action<HttpResponseMessage>? set = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        set?.Invoke(r);
        return r;
    }

    // ---- freshness (RFC 9111, private cache) ----

    [Fact]
    public void No_store_is_not_kept()
    {
        using var r = Response(x => x.Headers.TryAddWithoutValidation("Cache-Control", "no-store, max-age=600"));
        Assert.False(A2ACardResolver.Freshness(r, Now, Default).Store);
    }

    [Theory]
    [InlineData("Cache-Control", "no-cache")]
    [InlineData("Vary", "*")]
    [InlineData("Pragma", "no-cache")]
    public void Must_revalidate_on_every_use(string header, string value)
    {
        using var r = Response(x => x.Headers.TryAddWithoutValidation(header, value));
        var (expires, store) = A2ACardResolver.Freshness(r, Now, Default);
        Assert.True(store);
        Assert.Equal(DateTimeOffset.MinValue, expires);
    }

    [Fact]
    public void Pragma_is_ignored_when_Cache_Control_is_present()
    {
        using var r = Response(x =>
        {
            x.Headers.TryAddWithoutValidation("Cache-Control", "max-age=60");
            x.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        });
        Assert.Equal(Now.AddSeconds(60), A2ACardResolver.Freshness(r, Now, Default).Expires);
    }

    [Theory]
    [InlineData("max-age=300", null, null, null, 300)] // max-age
    [InlineData("max-age=300", 100, null, null, 200)] // less Age
    [InlineData("max-age=300", 400, null, null, -1)] // older than its lifetime: stale
    [InlineData("max-age=300", null, -120, null, 180)] // Date two minutes ago: apparent age
    [InlineData("max-age=300", 30, -120, null, 180)] // the larger of Age and apparent age
    [InlineData(null, null, 0, 600, 600)] // Expires minus Date
    [InlineData(null, null, null, null, 300)] // no freshness information: the default
    [InlineData("s-maxage=10", null, null, null, 300)] // s-maxage is for shared caches
    public void Lifetime_less_age(string? cacheControl, int? ageSeconds, int? dateOffsetSeconds, int? expiresInSeconds, int expected)
    {
        using var r = Response(x =>
        {
            if (cacheControl is not null) x.Headers.TryAddWithoutValidation("Cache-Control", cacheControl);
            if (ageSeconds is { } a) x.Headers.Age = TimeSpan.FromSeconds(a);
            if (dateOffsetSeconds is { } d) x.Headers.Date = Now.AddSeconds(d);
            if (expiresInSeconds is { } e) x.Content.Headers.Expires = Now.AddSeconds(e);
        });

        var (expires, store) = A2ACardResolver.Freshness(r, Now, Default);

        Assert.True(store);
        Assert.Equal(expected < 0 ? DateTimeOffset.MinValue : Now.AddSeconds(expected), expires);
    }

    // ---- the resolver ----

    private sealed class CardHost(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return Task.FromResult(respond(request));
        }
    }

    private static readonly Uri Agent = new("https://agent.example/");

    private static HttpResponseMessage Card(Action<HttpResponseMessage>? set = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(A2AJson.Serialize(SigningTests.SampleCard()), Encoding.UTF8, "application/json")
        };
        set?.Invoke(r);
        return r;
    }

    // Found by a2a-itk: the official A2A .NET SDK reads a card request without A2A-Version as a v0.3 client's, and its
    // blended card then says pushNotifications false, so this client refused to register push with it.
    [Fact]
    public async Task The_card_request_says_it_is_a_v1_client()
    {
        var host = new CardHost(req =>
        {
            var v1 = req.Headers.TryGetValues(A2AProtocol.Headers.Version, out var values) && values.Single() == "1.0";
            var card = SigningTests.SampleCard();
            card.Capabilities.PushNotifications = v1;
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(A2AJson.Serialize(card), Encoding.UTF8, "application/json") };
        });
        var resolved = await new A2ACardResolver(new HttpClient(host)).ResolveAsync(Agent);
        Assert.True(resolved.Card.Capabilities.PushNotifications);
        Assert.Equal(A2AProtocol.Version, Assert.Single(host.Requests.Single().Headers.GetValues(A2AProtocol.Headers.Version)));
    }

    [Fact]
    public async Task A_no_store_card_is_fetched_again_and_no_validator_is_sent()
    {
        var host = new CardHost(_ => Card(r =>
        {
            r.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
            r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
        }));
        var resolver = new A2ACardResolver(new HttpClient(host));

        await resolver.ResolveAsync(Agent);
        await resolver.ResolveAsync(Agent);

        var requests = host.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Empty(requests[1].Headers.IfNoneMatch); // nothing was stored to revalidate
    }

    [Fact]
    public async Task Without_an_entity_tag_Last_Modified_is_sent_back_and_a_304_keeps_the_card()
    {
        var lastModified = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        var host = new CardHost(req => req.Headers.IfModifiedSince is not null
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : Card(r =>
            {
                r.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
                r.Content.Headers.LastModified = lastModified;
            }));
        var resolver = new A2ACardResolver(new HttpClient(host));

        var first = await resolver.ResolveAsync(Agent);
        var second = await resolver.ResolveAsync(Agent);

        var requests = host.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(lastModified, requests[1].Headers.IfModifiedSince);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task A_card_older_than_its_max_age_is_fetched_again()
    {
        var host = new CardHost(_ => Card(r =>
        {
            r.Headers.TryAddWithoutValidation("Cache-Control", "max-age=60");
            r.Headers.Age = TimeSpan.FromSeconds(120); // a proxy kept it for two minutes
        }));
        var resolver = new A2ACardResolver(new HttpClient(host));

        await resolver.ResolveAsync(Agent);
        await resolver.ResolveAsync(Agent);

        Assert.Equal(2, host.Requests.Count);
    }

    [Fact]
    public async Task A_fresh_card_is_served_from_the_cache()
    {
        var host = new CardHost(_ => Card(r => r.Headers.TryAddWithoutValidation("Cache-Control", "max-age=600")));
        var resolver = new A2ACardResolver(new HttpClient(host));

        await resolver.ResolveAsync(Agent);
        await resolver.ResolveAsync(Agent);

        Assert.Single(host.Requests);
    }

    // ---- the public card endpoint ----

    [Fact]
    public async Task The_public_card_has_Last_Modified_but_If_Modified_Since_does_not_give_304()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var http = host.Http();
        using var first = await http.GetAsync("/.well-known/agent-card.json");
        var lastModified = first.Content.Headers.LastModified;

        using var ims = new HttpRequestMessage(HttpMethod.Get, "/.well-known/agent-card.json");
        ims.Headers.IfModifiedSince = DateTimeOffset.UtcNow.AddDays(1);
        using var second = await http.SendAsync(ims);
        using var inm = new HttpRequestMessage(HttpMethod.Get, "/.well-known/agent-card.json");
        inm.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag!.Tag);
        using var third = await http.SendAsync(inm);

        Assert.NotNull(lastModified);
        Assert.Equal(0, lastModified.Value.Millisecond);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode); // the entity tag decides, not the per-instance time
        Assert.Equal(lastModified, second.Content.Headers.LastModified); // same content, same time
        Assert.Equal(HttpStatusCode.NotModified, third.StatusCode);
    }
}
