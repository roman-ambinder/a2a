using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Snippets.BearerAuthentication;

internal sealed class MyAgent : IAgentExecutor
{
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
        updater.CompleteAsync(cancellationToken: cancellationToken).AsTask();
}

internal static class Samples
{
    public static WebApplication Bearer(WebApplicationBuilder builder)
    {
        #region Bearer
        builder.Services.AddAuthentication().AddJwtBearer(o =>
        {
            o.Authority = "https://idp.example.com";
            o.Audience = "api://my-agent";
        });
        builder.Services.AddA2A(a => a
            .AddAgentExecutor<MyAgent>()
            .AddAgentCard(AgentCard.Create("My Agent", "Says hello."))
            .AddBearerAuthentication()); // the card says "send a bearer token"; AddJwtBearer checks it

        var app = builder.Build();
        app.UseAuthentication();
        app.UseA2A();
        app.UseAuthorization();
        app.MapA2A("/a2a"); // no RequireAuthorization: every A2A endpoint requires the token
        #endregion
        return app;
    }

    public static string DefaultScheme() => JwtBearerDefaults.AuthenticationScheme; // "Bearer"
}
