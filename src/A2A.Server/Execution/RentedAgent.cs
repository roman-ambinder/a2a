using System.Runtime.ExceptionServices;

namespace A2A.Server.Execution;

/// <summary>
/// The agent of one execution, rented from the <see cref="IAgentExecutorFactory"/> before anything is claimed or stored. The
/// request disposes the lease unless an execution took it (<see cref="TakeLease"/>), which then disposes it when the agent returns.
/// </summary>
internal sealed class RentedAgent : IAsyncDisposable
{
    private AgentExecutorLease? _lease;

    private RentedAgent(AgentExecutorLease? lease, IAgentExecutor? executor, ExceptionDispatchInfo? error)
    {
        _lease = lease;
        Executor = executor;
        Error = error;
    }

    /// <summary>The executor, or null when the factory returned none or failed.</summary>
    public IAgentExecutor? Executor { get; }

    /// <summary>The factory's failure, rethrown by the execution so the task fails like an agent that throws.</summary>
    public ExceptionDispatchInfo? Error { get; }

    /// <summary>Rents the tenant's agent. A failing factory does not throw here: see <see cref="Error"/>.</summary>
    public static async ValueTask<RentedAgent> RentAsync(IAgentExecutorFactory factory, string tenant, IAgentExecutor? fallback, CancellationToken cancellationToken)
    {
        try
        {
            var lease = await factory.RentAsync(tenant, cancellationToken).ConfigureAwait(false);
            return new RentedAgent(lease, lease.Executor ?? fallback, null);
        }
        catch (Exception ex)
        {
            return new RentedAgent(null, fallback, ExceptionDispatchInfo.Capture(ex));
        }
    }

    /// <summary>An agent with no lease to release (a fixed executor).</summary>
    public static RentedAgent Of(IAgentExecutor executor) => new(null, executor, null);

    /// <summary>Hands the lease to the execution; null when there is none.</summary>
    public AgentExecutorLease? TakeLease()
    {
        var taken = _lease;
        _lease = null;
        return taken;
    }

    public ValueTask DisposeAsync()
    {
        var owned = _lease;
        _lease = null;
        return owned?.DisposeAsync() ?? default;
    }
}
