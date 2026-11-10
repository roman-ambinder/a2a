using System.Collections.Concurrent;
using System.Text.Json;
using A2A.Server;
using A2A.Server.Execution;
using A2A.Server.Storage;

namespace Snippets.Continuation;

/// <summary>Where the host keeps the keys of tasks it will continue. Use a database in production.</summary>
internal sealed class PendingKeys
{
    private readonly ConcurrentDictionary<string, TaskKey> _keys = new();

    public void Add(TaskKey key) => _keys[key.TaskId] = key;

    public void Remove(TaskKey key) => _keys.TryRemove(key.TaskId, out _);

    public IReadOnlyCollection<TaskKey> All => _keys.Values.ToArray();
}

internal sealed class ReportAgent(PendingKeys pending) : IContinuableAgentExecutor
{
    #region Agent
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        // Keep the key. The host needs it to continue this task later.
        pending.Add(new TaskKey(context.Call.Tenant, context.Call.Principal, context.TaskId));
        await updater.StartWorkAsync("Report requested. I will update this task when it is ready.", cancellationToken);
    }

    public async Task ContinueAsync(TaskContinuationContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        // No inbound request: there is no bearer token here.
        await updater.CompleteAsync("The report is ready.", cancellationToken);
        pending.Remove(context.Key);
    }
    #endregion
}

internal sealed class ContinuationTimer(TaskExecutionEngine engine, PendingKeys pending) : BackgroundService
{
    #region Timer
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var key in pending.All)
            {
                var result = await engine.ContinueTaskAsync(key, cancellationToken: stoppingToken);
                switch (result.Status)
                {
                    case TaskContinuationStatus.TaskNotFound:
                    case TaskContinuationStatus.TaskTerminal:
                    case TaskContinuationStatus.InterruptedTimedOut:
                        pending.Remove(key); // it will never continue
                        break;
                    // Started: the agent runs. AlreadyRunning: try again on the next tick.
                    // NotSupported: the agent is not an IContinuableAgentExecutor. Fix the registration.
                }
            }
        }
    }
    #endregion
}

internal static class Samples
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<PendingKeys>();
        services.AddA2AServer().AddAgentExecutor<ReportAgent>();
        services.AddHostedService<ContinuationTimer>();
    }

    public static void MapCallback(WebApplication app)
    {
        #region Callback
        // The host decides who may call this. The SDK does not check the caller.
        app.MapPost("/internal/continue/{tenant}/{owner}/{taskId}",
                async (string tenant, string owner, string taskId, JsonElement? body, TaskExecutionEngine engine, CancellationToken ct) =>
                {
                    var metadata = body is { ValueKind: JsonValueKind.Object } b
                        ? new Dictionary<string, JsonElement> { ["callback"] = b.Clone() }
                        : null;
                    var result = await engine.ContinueTaskAsync(new TaskKey(tenant, owner, taskId), metadata, ct);
                    return result.Status switch
                    {
                        TaskContinuationStatus.Started => Results.Accepted(),
                        TaskContinuationStatus.TaskNotFound => Results.NotFound(),
                        TaskContinuationStatus.AlreadyRunning => Results.Conflict(),
                        _ => Results.UnprocessableEntity(new { status = result.Status.ToString() }),
                    };
                })
            .RequireAuthorization("A2AContinuation");
        #endregion
    }
}
