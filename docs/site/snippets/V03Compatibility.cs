using A2A.Client;
using A2A.Client.Cards;
using A2A.Client.V0_3Compat;
using A2A.Signing;
using A2A.Server.AspNetCore;
using A2A.Server.V0_3Compat;
using OpenTelemetry.Metrics;

namespace A2A.DocSnippets.V03Compatibility;

internal static class Snippets
{
    public static void Server(WebApplication app)
    {
        #region Server
        // The v0.3 routes and the blended card are added to the group that MapA2A returns.
        app.MapA2A("/", o => o.EnableV03Compat()).RequireAuthorization();
        #endregion
    }

    public static void ServerWithPolicy(WebApplication app)
    {
        #region ServerPolicy
        // A policy set here covers the v0.3 routes too.
        app.MapA2A("/", o =>
        {
            o.AuthorizationPolicy = "A2A";
            o.EnableV03Compat();
        });
        #endregion
    }

    public static async Task<A2AClient> ClientAsync(HttpClient httpClient)
    {
        #region Client
        var client = await V03CompatClientFactory.CreateAsync(new Uri("https://old-agent.example.com/"), httpClient);
        var reply = await client.SendMessageAsync("hello");
        Console.WriteLine(reply);
        #endregion
        return client;
    }

    public static A2AClient ClientFromCard(AgentCard card, HttpClient httpClient)
    {
        #region ClientFromCard
        // When you already hold the card. This throws UnsupportedOperation when it has no usable interface.
        return V03CompatClientFactory.Create(card, httpClient);
        #endregion
    }

    public static Task<A2AClient> ClientWithResolverOptionsAsync(Uri agentUrl, HttpClient httpClient)
    {
        #region ClientResolver
        // The factory copies these options and never changes your object.
        var resolverOptions = new A2ACardResolverOptions { PinPolicy = PinPolicy.Strict };
        return V03CompatClientFactory.CreateAsync(agentUrl, httpClient, resolverOptions: resolverOptions);
        #endregion
    }

    public static Task<A2AClient> ClientUnsignedAsync(Uri agentUrl, HttpClient httpClient, JwkSet keys)
    {
        #region ClientUnsigned
        // A v0.3-only card is always unsigned. With pinned keys the client refuses it unless you opt out.
        var resolverOptions = new A2ACardResolverOptions();
        resolverOptions.Trust.TrustedKeys[CardTrustPolicy.OriginOf(agentUrl)] = keys;
        resolverOptions.Trust.RequireSignature = false;
        return V03CompatClientFactory.CreateAsync(agentUrl, httpClient, resolverOptions: resolverOptions);
        #endregion
    }

    public static void Metrics(IServiceCollection services)
    {
        #region Metrics
        // Both v0.3 counters are on the "A2A" meter.
        services.AddOpenTelemetry()
            .WithMetrics(m => m.AddMeter("A2A").AddOtlpExporter());
        #endregion
    }
}
