using Microsoft.Extensions.DependencyInjection;

namespace A2A.Server.Agents;

/// <summary>
/// Picks the agent (<see cref="IAgentExecutor"/>) that runs a tenant's tasks, so one host can serve many agents (one agent per
/// tenant). Register one with <c>AddAgentExecutorResolver&lt;T&gt;()</c>; without it, the executor registered with
/// <c>AddAgentExecutor</c> runs every tenant's tasks. Asked once per execution (each message, each CancelTask), from the tenant
/// alone, so it also works for work that has no HTTP request.
/// </summary>
public abstract class AgentExecutorResolver
{
    /// <summary>
    /// The executor for <paramref name="tenant"/>, or null when no agent serves it. <paramref name="services"/> is the
    /// execution's own scope (disposed when the execution ends), so scoped executors and their dependencies can be resolved
    /// from it. A null executor fails the execution, like an executor that throws; the request was already refused when the
    /// tenant has no card (<see cref="AgentCardProvider"/>).
    /// </summary>
    public abstract IAgentExecutor? Resolve(string tenant, IServiceProvider services);
}

/// <summary>The resolver used when none is registered: the single registered <see cref="IAgentExecutor"/> for every tenant.</summary>
internal sealed class DefaultAgentExecutorResolver : AgentExecutorResolver
{
    public override IAgentExecutor? Resolve(string tenant, IServiceProvider services) => services.GetRequiredService<IAgentExecutor>();
}
