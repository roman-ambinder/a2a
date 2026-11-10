using A2A.Server.Coordination;
using A2A.Server.Push;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>Names and tags of the A2A health checks, and the method that registers them.</summary>
public static class A2AHealthChecks
{
    /// <summary>Name of the task store check: one cheap read.</summary>
    public const string TaskStoreName = "a2a_task_store";

    /// <summary>Name of the execution coordinator check: one cheap read.</summary>
    public const string CoordinatorName = "a2a_coordinator";

    /// <summary>Name of the push outbox check: backlog age and dead letter count.</summary>
    public const string PushOutboxName = "a2a_push_outbox";

    /// <summary>Name of the shutdown check: unhealthy once the host is stopping.</summary>
    public const string ShutdownName = "a2a_shutdown";

    /// <summary>Tag on every A2A check.</summary>
    public const string Tag = "a2a";

    /// <summary>
    ///     Tag for readiness probes. It is on the task store, coordinator and shutdown checks, and not on the push outbox
    ///     check:
    ///     one dead webhook must not take every instance out of the load balancer.
    /// </summary>
    public const string ReadyTag = "ready";

    /// <summary>
    ///     Adds the four A2A health checks: <see cref="TaskStoreName" />, <see cref="CoordinatorName" />,
    ///     <see cref="PushOutboxName" />
    ///     (unless <see cref="A2AHealthCheckOptions.IncludePushOutbox" /> is false) and <see cref="ShutdownName" />. The
    ///     checks are
    ///     cheap and never report connection details. When the push outbox check is on and the registered
    ///     <see cref="PushOutbox" /> does not implement <see cref="IPushOutboxStatistics" />, the host fails at startup.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="configure">Changes the options. Optional.</param>
    public static IHealthChecksBuilder AddA2AHealthChecks(this IHealthChecksBuilder builder,
        Action<A2AHealthCheckOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new A2AHealthCheckOptions();
        configure?.Invoke(options);
        options.Validate();

        string[] ready = [Tag, ReadyTag];
        string[] tagOnly = [Tag];
        builder.Add(new HealthCheckRegistration(TaskStoreName,
            sp => new TaskStoreHealthCheck(sp.GetRequiredService<TaskStore>(),
                sp.GetRequiredService<ILogger<TaskStoreHealthCheck>>()),
            HealthStatus.Unhealthy, ready, options.Timeout));
        builder.Add(new HealthCheckRegistration(CoordinatorName,
            sp => new CoordinatorHealthCheck(sp.GetRequiredService<ExecutionCoordinator>(),
                sp.GetRequiredService<ILogger<CoordinatorHealthCheck>>()),
            HealthStatus.Unhealthy, ready, options.Timeout));
        if (options.IncludePushOutbox)
        {
            var thresholds = new PushOutboxThresholds(options.PushBacklogDegradedAge, options.PushBacklogUnhealthyAge,
                options.DeadLetterDegradedCount, options.DeadLetterUnhealthyCount);
            builder.Add(new HealthCheckRegistration(PushOutboxName,
                sp => new PushOutboxHealthCheck(sp.GetRequiredService<PushOutbox>(), thresholds,
                    sp.GetRequiredService<ILogger<PushOutboxHealthCheck>>()),
                HealthStatus.Degraded, tagOnly, options.Timeout));
            builder.Services.AddSingleton<IHostedService>(sp =>
                new PushOutboxCapabilityCheck(sp.GetRequiredService<PushOutbox>()));
        }

        builder.Add(new HealthCheckRegistration(ShutdownName,
            sp => new ShutdownHealthCheck(sp.GetRequiredService<IHostApplicationLifetime>(),
                sp.GetRequiredService<IOptions<A2AServerOptions>>()),
            HealthStatus.Unhealthy, ready, options.Timeout));
        return builder;
    }
}

/// <summary>Options of <see cref="A2AHealthChecks.AddA2AHealthChecks" />. A threshold that is null is off.</summary>
public sealed class A2AHealthCheckOptions
{
    /// <summary>
    ///     How long one check may take before it fails: unhealthy, or degraded for the push outbox check. Must be above
    ///     zero. Default 5 seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Whether to add the push outbox check. Needs an outbox that implements <see cref="IPushOutboxStatistics" />.
    ///     Default true.
    /// </summary>
    public bool IncludePushOutbox { get; set; } = true;

    /// <summary>The push check is degraded when the oldest due delivery has waited at least this long. Default 5 minutes.</summary>
    public TimeSpan? PushBacklogDegradedAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The push check is unhealthy when the oldest due delivery has waited at least this long. Default off.</summary>
    public TimeSpan? PushBacklogUnhealthyAge { get; set; }

    /// <summary>The push check is degraded at this many dead letters. Must be at least 1. Default 100.</summary>
    public int? DeadLetterDegradedCount { get; set; } = 100;

    /// <summary>The push check is unhealthy at this many dead letters. Must be at least 1. Default off.</summary>
    public int? DeadLetterUnhealthyCount { get; set; }

    internal void Validate()
    {
        if (Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Timeout), Timeout, "Timeout must be above zero.");
        CheckAge(PushBacklogDegradedAge, nameof(PushBacklogDegradedAge));
        CheckAge(PushBacklogUnhealthyAge, nameof(PushBacklogUnhealthyAge));
        CheckCount(DeadLetterDegradedCount, nameof(DeadLetterDegradedCount));
        CheckCount(DeadLetterUnhealthyCount, nameof(DeadLetterUnhealthyCount));
    }

    private static void CheckAge(TimeSpan? value, string name)
    {
        if (value is { } v && v <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(name, v, $"{name} must be above zero, or null to turn it off.");
    }

    private static void CheckCount(int? value, string name)
    {
        if (value is { } v && v < 1)
            throw new ArgumentOutOfRangeException(name, v, $"{name} must be at least 1, or null to turn it off.");
    }
}

/// <summary>The probe key of the store and coordinator checks. It names no real task.</summary>
internal static class HealthProbe
{
    public static readonly TaskKey Key = new("a2a-health", "a2a-health", "a2a-health");

    /// <summary>
    ///     True for every exception the check must turn into a clean result. Only the cancellation of the check's own time
    ///     limit
    ///     goes on to the framework, which reports the timeout. Any other exception that reached the framework would be shown
    ///     with
    ///     its message, and a database or Redis client's message can name its host. That holds after the time limit too: a
    ///     client
    ///     that ignores the token can fail late.
    /// </summary>
    public static bool IsFailure(Exception ex, CancellationToken cancellationToken)
    {
        return ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested;
    }
}

/// <summary>Reads one task key that does not exist. The result is ignored: the check only proves the store answers.</summary>
internal sealed class TaskStoreHealthCheck(TaskStore store, ILogger<TaskStoreHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await store.GetAsync(HealthProbe.Key, cancellationToken).AsTask().WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return HealthCheckResult.Healthy("The task store answers.");
        }
        catch (Exception ex) when (HealthProbe.IsFailure(ex, cancellationToken))
        {
            AspNetCoreLog.HealthCheckFailed(logger, ex, A2AHealthChecks.TaskStoreName);
            return new HealthCheckResult(context.Registration.FailureStatus,
                $"The task store did not answer ({ex.GetType().Name}).", null, null);
        }
    }
}

/// <summary>Asks the coordinator about one task key that does not exist. The result is ignored.</summary>
internal sealed class CoordinatorHealthCheck(ExecutionCoordinator coordinator, ILogger<CoordinatorHealthCheck> logger)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await coordinator.IsRunningAsync(HealthProbe.Key, cancellationToken).AsTask().WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return HealthCheckResult.Healthy("The execution coordinator answers.");
        }
        catch (Exception ex) when (HealthProbe.IsFailure(ex, cancellationToken))
        {
            AspNetCoreLog.HealthCheckFailed(logger, ex, A2AHealthChecks.CoordinatorName);
            return new HealthCheckResult(context.Registration.FailureStatus,
                $"The execution coordinator did not answer ({ex.GetType().Name}).", null, null);
        }
    }
}

internal sealed record PushOutboxThresholds(
    TimeSpan? DegradedAge,
    TimeSpan? UnhealthyAge,
    int? DegradedCount,
    int? UnhealthyCount)
{
    /// <summary>The highest enabled count threshold: the most dead letters the check needs to count. 0 when none is enabled.</summary>
    public int CountCap => Math.Max(DegradedCount ?? 0, UnhealthyCount ?? 0);
}

/// <summary>Reports the push backlog age and the dead letter count. Output holds numbers only: no tenant, task or URL.</summary>
internal sealed class PushOutboxHealthCheck(
    PushOutbox outbox,
    PushOutboxThresholds thresholds,
    ILogger<PushOutboxHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        PushOutboxStatistics stats;
        try
        {
            stats = await ((IPushOutboxStatistics)outbox).GetStatisticsAsync(thresholds.CountCap, cancellationToken)
                .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (HealthProbe.IsFailure(ex, cancellationToken))
        {
            AspNetCoreLog.HealthCheckFailed(logger, ex, A2AHealthChecks.PushOutboxName);
            return new HealthCheckResult(context.Registration.FailureStatus,
                $"The push outbox did not answer ({ex.GetType().Name}).", null, null);
        }

        var data = new Dictionary<string, object>
        {
            ["oldest_due_age_seconds"] = stats.OldestDueAge.TotalSeconds,
            ["dead_letters"] = stats.DeadLetterCount
        };
        if ((thresholds.UnhealthyAge is { } ua && stats.OldestDueAge >= ua) ||
            (thresholds.UnhealthyCount is { } uc && stats.DeadLetterCount >= uc))
            return HealthCheckResult.Unhealthy("The push outbox has a backlog or too many dead letters.", data: data);
        if ((thresholds.DegradedAge is { } da && stats.OldestDueAge >= da) ||
            (thresholds.DegradedCount is { } dc && stats.DeadLetterCount >= dc))
            return HealthCheckResult.Degraded("The push outbox has a backlog or many dead letters.", data: data);
        return HealthCheckResult.Healthy("The push outbox is on time.", data);
    }
}

/// <summary>Unhealthy from the moment <c>ApplicationStopping</c> fires, while running tasks drain.</summary>
internal sealed class ShutdownHealthCheck(IHostApplicationLifetime lifetime, IOptions<A2AServerOptions> options)
    : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!lifetime.ApplicationStopping.IsCancellationRequested)
            return Task.FromResult(HealthCheckResult.Healthy("The host is running."));
        var data = new Dictionary<string, object>
            { ["shutdown_drain_timeout_seconds"] = options.Value.ShutdownDrainTimeout.TotalSeconds };
        return Task.FromResult(HealthCheckResult.Unhealthy("The host is stopping and running tasks are draining.",
            data: data));
    }
}

/// <summary>Fails the start when the push outbox check is on but the outbox cannot report statistics (ADR-001).</summary>
internal sealed class PushOutboxCapabilityCheck(PushOutbox outbox) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (outbox is not IPushOutboxStatistics)
            throw new InvalidOperationException(
                $"The registered PushOutbox ({outbox.GetType().FullName}) does not implement {nameof(IPushOutboxStatistics)}, which the push outbox health check needs. " +
                $"Implement the interface, or set {nameof(A2AHealthCheckOptions)}.{nameof(A2AHealthCheckOptions.IncludePushOutbox)} = false.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}