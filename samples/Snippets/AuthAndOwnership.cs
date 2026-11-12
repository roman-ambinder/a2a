using System.Security.Claims;
using A2A.Delegation;
using A2A.Client;
using A2A.Client.Delegation;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;

namespace Snippets.AuthAndOwnership;

internal sealed class MyAgent : IAgentExecutor
{
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
        updater.CompleteAsync(cancellationToken).AsTask();
}

#region Resolver
// Use the "oid" claim as the owner id instead of NameIdentifier or "sub".
internal sealed class OidPrincipalResolver : IPrincipalResolver
{
    public string? Resolve(ClaimsPrincipal user) => user.FindFirst("oid")?.Value;
}
#endregion

#region Handler
// Only callers with the "a2a.cancel" scope may cancel tasks.
internal sealed class ScopeHandler : A2AAuthorizationHandler
{
    public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation,
        object request, CancellationToken cancellationToken)
    {
        if (operation.Kind == A2AOperation.CancelTask)
            return ValueTask.FromResult(call.User?.HasClaim("scope", "a2a.cancel") == true);
        return base.AuthorizeAsync(call, operation, request, cancellationToken);
    }
}
#endregion

#region Delegate
// An agent that hands part of its work to another agent.
internal sealed class CoordinatorAgent(A2AClient worker, Uri workerUrl) : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext ctx, TaskUpdater updater, CancellationToken ct)
    {
        // Add this agent to the chain. Start a new chain if the caller sent none.
        var me = new ActorChainEntry { Sub = "agent:coordinator" };
        var chain = ctx.Message.GetActorChain()?.AppendHop(me)
            ?? ActorChain.Start(new ActorChainEntry { Sub = ctx.Call.Principal }).AppendHop(me);
        var next = Message.FromUser("rebase it").SetActorChain(chain);

        // Swap the caller's token for a narrow one. CanExchangeToken tells you if this can run.
        var token = await ctx.ExchangeTokenAsync("agent:rebase-worker", ["repo:read"], ct);

        await worker.SendMessageAsync(next, options: new A2ARequestOptions().UseBearerToken(token, workerUrl), cancellationToken: ct);
        await updater.CompleteAsync("done", ct);
    }
}
#endregion

internal static class Samples
{
    public static void Protect()
    {
        #region Protect
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthentication("Bearer"); // add your scheme handler here, for example AddJwtBearer
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("A2ACaller", p => p.RequireAuthenticatedUser());
        builder.Services.AddA2A(a => a
            .AddAgentExecutor<MyAgent>()
            .AddAgentCard("My Agent", "Says hello.")
            .RequirePolicy("A2ACaller"));   // every A2A endpoint; the Agent Card stays anonymous

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapA2A("/a2a");
        #endregion
    }

    public static void Anonymous()
    {
        var services = new ServiceCollection();
        #region Anonymous
        services.AddA2A(a => a.Configure(o => o.AllowAnonymous = true));
        #endregion
    }

    public static void Custom()
    {
        var services = new ServiceCollection();
        #region Register
        services.AddA2AServer()
            .AddAuthorizationHandler<ScopeHandler>();
        services.AddSingleton<IPrincipalResolver, OidPrincipalResolver>();
        #endregion
    }

    public static void Client()
    {
        var services = new ServiceCollection();
        #region Client
        services.AddA2AClient("weather", o => o.AgentUrl = new Uri("https://agent.example.com/"))
            .AddBearerToken((sp, ct) => ValueTask.FromResult("my-access-token"));
        #endregion
    }

    public static async Task NoRedirects()
    {
        #region NoRedirects
        using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });
        var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"), http);
        #endregion
        _ = client;
    }

    public static void Exchanger()
    {
        var builder = WebApplication.CreateBuilder();
        #region Exchanger
        // The exchanger never follows redirects, so the secret and the subject token stay on the token endpoint.
        builder.Services.AddA2AServer()
            .AddTokenExchanger(new OAuthTokenExchanger(new OAuthTokenExchangerOptions
            {
                TokenEndpoint = new Uri("https://idp.example/token"),
                ClientId = "my-agent",
                ClientSecret = "my-secret",
            }));
        #endregion
    }
}
