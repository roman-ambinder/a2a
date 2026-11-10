namespace A2A.Server.Agents;

/// <summary>
///     Picks the agent (<see cref="IAgentExecutor" />) that runs a tenant's tasks, so one host can serve many agents (one
///     agent per
///     tenant). Register one with <see cref="A2AServerBuilder.AddAgentExecutorResolver{T}" />; the DI-registered
///     <see cref="ScopedAgentExecutorFactory" /> asks it. Without it, the executor registered with <c>AddAgentExecutor</c>
///     runs every
///     tenant's tasks. Asked once per execution (each message, each CancelTask), from the tenant alone, so it also works
///     for work
///     that has no HTTP request.
/// </summary>
public abstract class AgentExecutorResolver
{
    /// <summary>
    ///     The executor for <paramref name="tenant" />, or null when no agent serves it. <paramref name="services" /> is the
    ///     execution's own scope (disposed when the execution ends), so scoped executors and their dependencies can be
    ///     resolved
    ///     from it. A null executor refuses a message with <see cref="A2AErrorCode.UnsupportedOperation" /> before anything is
    ///     stored; a resolver that throws fails the execution, like an executor that throws.
    /// </summary>
    /// <param name="tenant">The tenant (<c>""</c> is the default tenant).</param>
    /// <param name="services">The execution's scope.</param>
    public abstract IAgentExecutor? Resolve(string tenant, IServiceProvider services);
}