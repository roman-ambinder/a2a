using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Server.AspNetCore;

public static class A2AServiceCollectionExtensions
{
    /// <summary>Configures HTTP-level settings (caller id, SSE keep-alive, card signing).</summary>
    public static IServiceCollection ConfigureA2AHttp(this IServiceCollection services, Action<A2AHttpOptions> configure) =>
        services.Configure(configure);
}

public static class A2AEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps both bindings under one base URL: JSON-RPC at <c>POST {pattern}</c> and HTTP+JSON at <c>{pattern}/message:send</c>,
    /// <c>{pattern}/tasks/...</c>, etc. Declare both in the Agent Card with the same URL. Returns the group so you can add
    /// <c>RequireAuthorization()</c>, rate limiting, or CORS. The Agent Card itself is mapped separately with <see cref="MapAgentCard"/>.
    /// </summary>
    public static RouteGroupBuilder MapA2A(this IEndpointRouteBuilder endpoints, string pattern = "/a2a")
    {
        var group = endpoints.MapGroup(pattern);
        var rpc = ActivatorUtilities.CreateInstance<JsonRpcEndpoint>(endpoints.ServiceProvider);
        group.MapPost("", rpc.HandleAsync).WithDisplayName("A2A JSON-RPC");
        ActivatorUtilities.CreateInstance<RestEndpoint>(endpoints.ServiceProvider).Map(group);
        return group;
    }

    /// <summary>Maps only the JSON-RPC binding at <c>POST {pattern}</c>.</summary>
    public static IEndpointConventionBuilder MapA2AJsonRpc(this IEndpointRouteBuilder endpoints, string pattern = "/a2a")
    {
        var rpc = ActivatorUtilities.CreateInstance<JsonRpcEndpoint>(endpoints.ServiceProvider);
        return endpoints.MapPost(pattern, rpc.HandleAsync).WithDisplayName("A2A JSON-RPC");
    }

    /// <summary>Maps only the HTTP+JSON binding under <paramref name="prefix"/>.</summary>
    public static RouteGroupBuilder MapA2AHttpJson(this IEndpointRouteBuilder endpoints, string prefix = "/a2a")
    {
        var group = endpoints.MapGroup(prefix);
        ActivatorUtilities.CreateInstance<RestEndpoint>(endpoints.ServiceProvider).Map(group);
        return group;
    }

    /// <summary>
    /// Serves the public Agent Card at <c>/.well-known/agent-card.json</c> (anonymous, cacheable). When a signing key is
    /// configured, the card is signed and the public key is served at <see cref="A2AHttpOptions.JwksPath"/>.
    /// </summary>
    public static IEndpointConventionBuilder MapAgentCard(this IEndpointRouteBuilder endpoints)
    {
        var card = ActivatorUtilities.CreateInstance<AgentCardEndpoint>(endpoints.ServiceProvider);
        var jwksPath = endpoints.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<A2AHttpOptions>>().Value.JwksPath;
        var group = endpoints.MapGroup("");
        group.MapGet(A2AProtocol.WellKnownAgentCardPath, card.GetCardAsync).WithDisplayName("A2A Agent Card");
        group.MapGet(jwksPath, card.GetJwksAsync).WithDisplayName("A2A Agent Card JWKS");
        group.AllowAnonymous();
        return group;
    }
}
