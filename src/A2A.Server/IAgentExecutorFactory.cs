namespace A2A.Server;

/// <summary>
///     Supplies the <see cref="IAgentExecutor" /> for one execution of a tenant's task. Lets the server core run without a
///     DI
///     container, and lets one host serve many agents (one agent per tenant).
/// </summary>
public interface IAgentExecutorFactory
{
    /// <summary>
    ///     Gets an executor for one execution of <paramref name="tenant" />'s task. Dispose the returned lease when the
    ///     execution
    ///     ends. A lease whose <see cref="AgentExecutorLease.Executor" /> is null means no agent serves the tenant: a message
    ///     is
    ///     refused (<see cref="A2AErrorCode.UnsupportedOperation" />) before anything is stored. A factory that throws fails
    ///     the
    ///     execution, like an executor that throws.
    /// </summary>
    /// <param name="tenant">The tenant (<c>""</c> is the default tenant).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<AgentExecutorLease> RentAsync(string tenant, CancellationToken cancellationToken);
}

/// <summary>An executor on loan for one execution. Disposing it releases whatever scope produced the executor.</summary>
/// <param name="executor">The executor to run, or null when no agent serves the tenant.</param>
/// <param name="scope">Released when the lease is disposed; <see langword="null" /> when nothing needs releasing.</param>
public readonly struct AgentExecutorLease(IAgentExecutor? executor, IAsyncDisposable? scope) : IAsyncDisposable
{
    /// <summary>The executor to run, or null when no agent serves the tenant.</summary>
    public IAgentExecutor? Executor { get; } = executor;

    /// <summary>Releases the scope that produced <see cref="Executor" />.</summary>
    public ValueTask DisposeAsync()
    {
        return scope?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}