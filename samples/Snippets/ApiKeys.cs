using A2A.Client;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Snippets.ApiKeys;

internal sealed class MyAgent : IAgentExecutor
{
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
        updater.CompleteAsync(cancellationToken: cancellationToken).AsTask();
}

internal static class Samples
{
    public static WebApplication Server(WebApplicationBuilder builder, string billingHash, string reportsHash)
    {
        #region Server
        builder.Services.AddA2A(builder.Configuration.GetSection("A2A"), a => a
            .AddAgentExecutor<MyAgent>()
            .AddApiKeyAuthentication(o => o
                .Add("billing", billingHash)              // a caller id and the hash of its key
                .Add("reports", reportsHash, "acme")));   // this key may only call the tenant "acme"

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapA2A("/a2a"); // no RequireAuthorization: the API-key policy goes on every A2A endpoint without one
        #endregion
        return app;
    }

    public static void CreateKey()
    {
        #region Create
        var key = A2AApiKey.Create();
        Console.WriteLine(key.Key);  // a2a_...: give it to the caller once
        Console.WriteLine(key.Hash); // sha256:...: store this on the agent
        #endregion
    }

    public static WebApplication Mixed(WebApplicationBuilder builder, string hash)
    {
        #region Mixed
        builder.Services.AddAuthentication().AddJwtBearer();
        builder.Services.AddAuthorization(o => o.AddPolicy("agents", p => p
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, A2AApiKeyOptions.SchemeName)
            .RequireAuthenticatedUser()));
        builder.Services.AddA2A(a => a.AddAgentExecutor<MyAgent>().AddApiKeyAuthentication(o => o.Add("billing", hash)));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapA2A("/a2a").RequireAuthorization("agents"); // an endpoint with its own policy keeps it
        #endregion
        return app;
    }

    public static void Store(IServiceCollection services)
    {
        #region Store
        services.AddSingleton<IApiKeyStore, DatabaseKeyStore>(); // replaces the configuration store
        #endregion
    }

    public static void ClientFromSecretStore(IServiceCollection services)
    {
        #region ClientSecret
        // The key is fetched when a request needs it; cache it in the store, not here.
        services.AddSingleton<MySecrets>();
        services.AddA2AClient("billing", o => o.AgentUrl = new Uri("https://agent.example.com/"))
            .AddApiKey((sp, ct) => sp.GetRequiredService<MySecrets>().GetAsync("billing-agent-key", ct));
        #endregion
    }
}

#region StoreImpl
// Keys kept in your own database: look a key up by its hash, cache briefly, return null when it is unknown or revoked.
internal sealed class DatabaseKeyStore : IApiKeyStore
{
    public async ValueTask<ApiKeyCaller?> FindAsync(string keyHash, CancellationToken cancellationToken)
    {
        var row = await LookUpAsync(keyHash, cancellationToken);
        return row is null ? null : new ApiKeyCaller(row.Value.CallerId, row.Value.Tenants);
    }

    private static Task<(string CallerId, string[]? Tenants)?> LookUpAsync(string hash, CancellationToken ct) =>
        Task.FromResult<(string, string[]?)?>(null); // your query here
}
#endregion

/// <summary>Stands for a secret store client, for example Azure Key Vault's SecretClient.</summary>
internal sealed class MySecrets
{
    public ValueTask<string> GetAsync(string name, CancellationToken cancellationToken) => ValueTask.FromResult(name);
}
