using System.Security.Claims;
using A2A.Client;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.DocSnippets.AuthAndOwnership;

internal sealed class MyAgent : IAgentExecutor
{
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
        updater.CompleteAsync(cancellationToken: cancellationToken).AsTask();
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
            .AddAgentCard(new AgentCard { Name = "My Agent", Description = "Says hello.", Version = "1.0.0" }));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseA2A();
        app.UseAuthorization();
        app.MapA2A("/a2a", o => o.AuthorizationPolicy = "A2ACaller");
        #endregion
    }

    public static void Anonymous()
    {
        var services = new ServiceCollection();
        #region Anonymous
        services.AddA2AServer(o => o.AllowAnonymous = true);
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
}
