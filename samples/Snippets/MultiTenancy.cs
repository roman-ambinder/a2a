using System.Collections.Concurrent;
using A2A.Server;
using A2A.Server.Agents;
using A2A.Server.AspNetCore;

namespace Snippets.MultiTenancy;

public sealed class SalesAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        #region ReadTenant
        var tenant = context.Call.Tenant;   // "" is the default tenant
        await updater.ReplyAsync($"Hello from the {tenant} agent.", cancellationToken);
        #endregion
    }
}

public sealed class SupportAgent : IAgentExecutor
{
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
        updater.ReplyAsync("Support here.", cancellationToken).AsTask();
}

#region Cards
public sealed class MyCards : TenantCardProvider
{
    private readonly ConcurrentDictionary<string, AgentCard> _cards = new(StringComparer.Ordinal);

    public override ValueTask<AgentCard?> GetCardAsync(AgentRequestScope scope, CancellationToken cancellationToken)
    {
        if (scope.Tenant is not ("sales" or "support")) return new((AgentCard?)null);   // no agent for this tenant
        // The same instance on every call, so the server completes and encodes the card once.
        return new(_cards.GetOrAdd(scope.Tenant, Build));
    }

    private static AgentCard Build(string tenant) => new()
    {
        Name = $"{tenant} agent",
        Description = $"The {tenant} agent.",
        Version = "1.0.0",
        SupportedInterfaces =
        [
            new AgentInterface { Url = $"https://agents.example.com/{tenant}/a2a", ProtocolBinding = ProtocolBindings.JsonRpc },
        ],
        Capabilities = new AgentCapabilities { Streaming = true },
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = tenant, Name = tenant, Description = $"Answers {tenant} questions", Tags = [tenant] }],
    };
}
#endregion

#region Agents
public sealed class MyAgents : AgentExecutorResolver
{
    public override IAgentExecutor? Resolve(string tenant, IServiceProvider services) => tenant switch
    {
        "sales" => services.GetRequiredService<SalesAgent>(),
        "support" => services.GetRequiredService<SupportAgent>(),
        _ => null,   // no agent: the message is refused before anything is stored
    };
}
#endregion

public static class Samples
{
    public static void Register(IServiceCollection services)
    {
        #region Register
        services.AddScoped<SalesAgent>();
        services.AddScoped<SupportAgent>();
        services.AddA2AServer()
            .AddTenantCardProvider<MyCards>()
            .AddAgentExecutorResolver<MyAgents>();
        services.AddA2AServerAspNetCore();
        #endregion
    }

    public static void Map(WebApplication app)
    {
        #region Map
        app.MapAgentCard();   // /.well-known/agent-card.json and /{tenant}/.well-known/agent-card.json
        app.MapA2A("/{tenant}/a2a", o => o.AuthorizationPolicy = "ApiKey");
        #endregion
    }
}
