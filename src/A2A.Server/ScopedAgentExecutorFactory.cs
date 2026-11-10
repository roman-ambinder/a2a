using A2A.Server.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Server;

/// <summary>
///     The DI-registered <see cref="IAgentExecutorFactory" />: creates a scope per execution and resolves the executor
///     from it: the
///     one the <see cref="AgentExecutorResolver" /> picks for the tenant when one is registered, else the registered
///     <see cref="IAgentExecutor" /> for every tenant. Disposing the lease disposes the scope.
/// </summary>
/// <param name="scopes">Creates the scope of each execution.</param>
/// <param name="resolver">Picks each tenant's executor, or null when one executor serves every tenant.</param>
public sealed class ScopedAgentExecutorFactory(IServiceScopeFactory scopes, AgentExecutorResolver? resolver = null)
    : IAgentExecutorFactory
{
    /// <inheritdoc />
    public async ValueTask<AgentExecutorLease> RentAsync(string tenant, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        try
        {
            var executor = resolver is null
                ? scope.ServiceProvider.GetRequiredService<IAgentExecutor>()
                : resolver.Resolve(tenant, scope.ServiceProvider);
            if (executor is not null) return new AgentExecutorLease(executor, scope);
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await scope.DisposeAsync().ConfigureAwait(false);
        return default;
    }
}