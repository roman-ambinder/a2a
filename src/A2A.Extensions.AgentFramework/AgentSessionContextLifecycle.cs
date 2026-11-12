using A2A.Server.Retention;
using A2A.Server.Storage;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.Extensions.AgentFramework;

/// <summary>What the last <c>AddAIAgent</c> call chose, for the services below.</summary>
internal sealed record AIAgentRegistration(AgentSessionMode SessionMode, object? ServiceKey);

/// <summary>Deletes the stored session of a context when retention deletes the context.</summary>
internal sealed class AgentSessionContextLifecycle(
    IServiceScopeFactory scopes,
    AIAgentRegistration registration,
    ILogger<AgentSessionContextLifecycle>? logger = null) : IA2AContextLifecycle
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public async ValueTask OnContextDeletedAsync(ContextRecord context, CancellationToken cancellationToken)
    {
        if (registration.SessionMode != AgentSessionMode.Stored) return;
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetService<AgentSessionStore>();
            if (store is null) return;
            var agent = registration.ServiceKey is null
                ? scope.ServiceProvider.GetRequiredService<AIAgent>()
                : scope.ServiceProvider.GetRequiredKeyedService<AIAgent>(registration.ServiceKey);
            try
            {
                // The owner's session, and the session of every other caller of a shared context (SessionIndex).
                await SessionIndex.DeleteAllAsync(store, agent, context.Tenant, context.ContextId, context.Owner,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Logged here with what failed; retention would only say that a hook failed.
                AgentFrameworkLog.SessionDeleteFailed(_logger, ex, context.ContextId);
            }
        }
    }
}

/// <summary>Refuses to start a host that chose <see cref="AgentSessionMode.Stored" /> without a session store.</summary>
internal sealed class StoredSessionCheck(IServiceScopeFactory scopes, AIAgentRegistration registration) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (registration.SessionMode != AgentSessionMode.Stored) return Task.CompletedTask;
        // A scope: the store may be registered as scoped.
        using var scope = scopes.CreateScope();
        if (scope.ServiceProvider.GetService<AgentSessionStore>() is null)
            throw new InvalidOperationException(AIAgentExecutor.StoreMissingText);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
