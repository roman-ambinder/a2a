using System.Runtime.ExceptionServices;

namespace A2A.Server.Execution;

/// <summary>
/// The agent of one execution, rented from the <see cref="IAgentExecutorFactory"/> before anything is claimed or stored. The
/// request disposes the lease unless an execution took it (<see cref="TakeLease"/>), which then disposes it when the agent returns.
/// It also carries the execution slot (<see cref="AttachSlot"/>): the request releases it unless an execution took it
/// (<see cref="TakeSlot"/>), so every failure path before the start gives it back.
/// </summary>
internal sealed class RentedAgent : IAsyncDisposable
{
    private AgentExecutorLease? _lease;
    private ExecutionSlot? _slot;

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

    /// <summary>Gives the agent the execution slot the request took. At most one.</summary>
    public void AttachSlot(ExecutionSlot slot) => _slot = slot;

    /// <summary>Hands the slot to the execution; null when there is none.</summary>
    public ExecutionSlot? TakeSlot()
    {
        var taken = _slot;
        _slot = null;
        return taken;
    }

    public ValueTask DisposeAsync()
    {
        var slot = _slot;
        _slot = null;
        slot?.Dispose();
        var owned = _lease;
        _lease = null;
        return owned?.DisposeAsync() ?? default;
    }
}
