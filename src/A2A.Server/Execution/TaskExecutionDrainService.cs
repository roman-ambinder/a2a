using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace A2A.Server.Execution;

/// <summary>
/// On host stop, waits for the running executions to finish (<see cref="A2AServerOptions.ShutdownDrainTimeout"/>) before the
/// container disposes the engine, which asks the agents still running to stop.
/// </summary>
internal sealed partial class TaskExecutionDrainService(TaskExecutionEngine engine, A2AServerOptions options, ILogger<TaskExecutionDrainService>? logger = null, TimeProvider? time = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var timeout = options.ShutdownDrainTimeout;
        if (timeout <= TimeSpan.Zero) return;
        using var expiry = new CancellationTokenSource();
        await using var timer = (time ?? TimeProvider.System).CreateTimer(static state => ((CancellationTokenSource)state!).Cancel(), expiry, timeout, Timeout.InfiniteTimeSpan);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        try
        {
            await engine.WhenIdleAsync().WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            if (logger is not null) LogDrainGaveUp(logger, timeout);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Running agents did not finish within {Timeout} of the host stop; they are asked to stop and their tasks stay as they are.")]
    private static partial void LogDrainGaveUp(ILogger logger, TimeSpan timeout);
}
