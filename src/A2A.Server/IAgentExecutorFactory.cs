namespace A2A.Server;

/// <summary>Supplies the <see cref="IAgentExecutor"/> for one execution. Lets the server core run without a DI container.</summary>
public interface IAgentExecutorFactory
{
    /// <summary>Gets an executor for one execution. Dispose the returned lease when the execution ends.</summary>
    ValueTask<AgentExecutorLease> RentAsync(CancellationToken cancellationToken);
}

/// <summary>An executor on loan for one execution. Disposing it releases whatever scope produced the executor.</summary>
/// <param name="executor">The executor to run.</param>
/// <param name="scope">Released when the lease is disposed; <see langword="null"/> when nothing needs releasing.</param>
public readonly struct AgentExecutorLease(IAgentExecutor executor, IAsyncDisposable? scope) : IAsyncDisposable
{
    /// <summary>The executor to run.</summary>
    public IAgentExecutor Executor { get; } = executor;

    /// <summary>Releases the scope that produced <see cref="Executor"/>.</summary>
    public ValueTask DisposeAsync() => scope?.DisposeAsync() ?? ValueTask.CompletedTask;
}
