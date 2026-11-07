using Microsoft.Extensions.DependencyInjection;

namespace A2A.Server;

/// <summary>
/// The DI-registered <see cref="IAgentExecutorFactory"/>: creates a scope per execution and resolves the registered
/// <see cref="IAgentExecutor"/> from it. Disposing the lease disposes the scope.
/// </summary>
public sealed class ScopedAgentExecutorFactory(IServiceScopeFactory scopes) : IAgentExecutorFactory
{
    /// <inheritdoc />
    public async ValueTask<AgentExecutorLease> RentAsync(CancellationToken ct)
    {
        var scope = scopes.CreateAsyncScope();
        try
        {
            return new AgentExecutorLease(scope.ServiceProvider.GetRequiredService<IAgentExecutor>(), scope);
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
