using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Push;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.Tests;

public class HealthCheckTests
{
    private const string Secret = "Password=hunter2;Host=db.internal";

    private static ServiceProvider Build(Action<A2AHealthCheckOptions>? configure = null, Action<IServiceCollection>? services = null)
    {
        var s = new ServiceCollection();
        s.AddLogging();
        services?.Invoke(s);
        s.TryAddSingleton<IHostApplicationLifetime, FakeLifetime>(); // a plain container has no host
        s.AddA2AServer();
        s.AddHealthChecks().AddA2AHealthChecks(configure);
        return s.BuildServiceProvider();
    }

    private static async Task<HealthReport> CheckAsync(ServiceProvider sp, Func<HealthCheckRegistration, bool>? predicate = null) =>
        await sp.GetRequiredService<HealthCheckService>().CheckHealthAsync(predicate);

    private static async Task<HealthReportEntry> PushAsync(PushOutboxStatistics stats, Action<A2AHealthCheckOptions>? configure = null)
    {
        var outbox = new StatsOutbox { Result = stats };
        await using var sp = Build(configure, s => s.AddSingleton<PushOutbox>(outbox));
        var report = await CheckAsync(sp, r => r.Name == A2AHealthChecks.PushOutboxName);
        return Assert.Single(report.Entries).Value;
    }

    /// <summary>Everything a caller of the endpoint could read from an entry.</summary>
    private static string Visible(HealthReportEntry entry) =>
        entry.Description + "|" + string.Join(",", entry.Data.Select(d => $"{d.Key}={d.Value}")) + "|" + entry.Exception;

    [Fact]
    public async Task All_four_checks_are_healthy_on_defaults()
    {
        await using var sp = Build();
        var report = await CheckAsync(sp);

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Equal(
            [A2AHealthChecks.CoordinatorName, A2AHealthChecks.PushOutboxName, A2AHealthChecks.ShutdownName, A2AHealthChecks.TaskStoreName],
            report.Entries.Keys.Order());
        Assert.All(report.Entries.Values, e => Assert.Contains(A2AHealthChecks.Tag, e.Tags));
    }

    [Fact]
    public async Task Ready_tag_covers_store_coordinator_and_shutdown_but_not_push()
    {
        await using var sp = Build();
        var report = await CheckAsync(sp, r => r.Tags.Contains(A2AHealthChecks.ReadyTag));

        Assert.Equal(
            [A2AHealthChecks.CoordinatorName, A2AHealthChecks.ShutdownName, A2AHealthChecks.TaskStoreName],
            report.Entries.Keys.Order());
    }

    [Fact]
    public async Task Push_outbox_check_can_be_left_out()
    {
        await using var sp = Build(o => o.IncludePushOutbox = false);
        var report = await CheckAsync(sp);

        Assert.DoesNotContain(A2AHealthChecks.PushOutboxName, report.Entries.Keys);
        Assert.Equal(3, report.Entries.Count);
    }

    [Fact]
    public async Task Push_check_reports_numbers_only()
    {
        var entry = await PushAsync(new PushOutboxStatistics(TimeSpan.FromSeconds(30), 7));

        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Equal(30d, entry.Data["oldest_due_age_seconds"]);
        Assert.Equal(7, entry.Data["dead_letters"]);
        Assert.Equal(2, entry.Data.Count);
    }

    [Fact]
    public async Task Push_age_threshold_degrades_at_the_limit()
    {
        Assert.Equal(HealthStatus.Healthy, (await PushAsync(new(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1), 0))).Status);
        Assert.Equal(HealthStatus.Degraded, (await PushAsync(new(TimeSpan.FromMinutes(5), 0))).Status);
    }

    [Fact]
    public async Task Push_count_threshold_degrades_at_the_limit()
    {
        Assert.Equal(HealthStatus.Healthy, (await PushAsync(new(TimeSpan.Zero, 99))).Status);
        Assert.Equal(HealthStatus.Degraded, (await PushAsync(new(TimeSpan.Zero, 100))).Status);
    }

    [Fact]
    public async Task Push_unhealthy_thresholds_win_over_degraded_ones()
    {
        void Configure(A2AHealthCheckOptions o)
        {
            o.PushBacklogUnhealthyAge = TimeSpan.FromMinutes(30);
            o.DeadLetterUnhealthyCount = 1000;
        }

        Assert.Equal(HealthStatus.Degraded, (await PushAsync(new(TimeSpan.FromMinutes(29), 999), Configure)).Status);
        Assert.Equal(HealthStatus.Unhealthy, (await PushAsync(new(TimeSpan.FromMinutes(30), 0), Configure)).Status);
        Assert.Equal(HealthStatus.Unhealthy, (await PushAsync(new(TimeSpan.Zero, 1000), Configure)).Status);
    }

    [Fact]
    public async Task Null_thresholds_turn_a_limit_off()
    {
        void Configure(A2AHealthCheckOptions o)
        {
            o.PushBacklogDegradedAge = null;
            o.DeadLetterDegradedCount = null;
        }

        Assert.Equal(HealthStatus.Healthy, (await PushAsync(new(TimeSpan.FromDays(30), 1_000_000), Configure)).Status);
    }

    [Fact]
    public async Task Age_threshold_alone_does_not_react_to_dead_letters()
    {
        Assert.Equal(HealthStatus.Healthy, (await PushAsync(new(TimeSpan.Zero, 1_000_000), o => o.DeadLetterDegradedCount = null)).Status);
    }

    [Theory]
    [InlineData(100, null, 100)]
    [InlineData(100, 500, 500)]
    [InlineData(null, 50, 50)]
    [InlineData(null, null, 0)]
    public async Task Cap_is_the_highest_enabled_count_threshold(int? degraded, int? unhealthy, int expected)
    {
        var outbox = new StatsOutbox();
        await using var sp = Build(o =>
        {
            o.DeadLetterDegradedCount = degraded;
            o.DeadLetterUnhealthyCount = unhealthy;
        }, s => s.AddSingleton<PushOutbox>(outbox));
        await CheckAsync(sp, r => r.Name == A2AHealthChecks.PushOutboxName);

        Assert.Equal(expected, outbox.LastCap);
    }

    [Fact]
    public async Task Push_failure_is_degraded_and_leaks_nothing()
    {
        var outbox = new StatsOutbox { Failure = new InvalidOperationException(Secret) };
        await using var sp = Build(services: s => s.AddSingleton<PushOutbox>(outbox));
        var entry = Assert.Single((await CheckAsync(sp, r => r.Name == A2AHealthChecks.PushOutboxName)).Entries).Value;

        Assert.Equal(HealthStatus.Degraded, entry.Status);
        Assert.Contains(nameof(InvalidOperationException), entry.Description);
        Assert.DoesNotContain("hunter2", Visible(entry));
        Assert.DoesNotContain("db.internal", Visible(entry));
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task Store_failure_is_unhealthy_and_leaks_nothing()
    {
        await using var sp = Build(services: s => s.AddSingleton<TaskStore>(new ProbeStore(_ => throw new InvalidOperationException(Secret))));
        var entry = Assert.Single((await CheckAsync(sp, r => r.Name == A2AHealthChecks.TaskStoreName)).Entries).Value;

        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.Contains(nameof(InvalidOperationException), entry.Description);
        Assert.DoesNotContain("hunter2", Visible(entry));
        Assert.DoesNotContain("db.internal", Visible(entry));
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task Slow_store_is_cut_off_by_the_timeout_and_reported_unhealthy()
    {
        var store = new ProbeStore(async ct => await Task.Delay(Timeout.Infinite, ct));
        await using var sp = Build(o => o.Timeout = TimeSpan.FromMilliseconds(200), s => s.AddSingleton<TaskStore>(store));
        var started = DateTime.UtcNow;
        var report = await CheckAsync(sp, r => r.Name == A2AHealthChecks.TaskStoreName);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.Equal(HealthStatus.Unhealthy, Assert.Single(report.Entries).Value.Status);
    }

    [Fact]
    public async Task A_store_that_ignores_the_token_is_still_cut_off_by_the_timeout()
    {
        var store = new ProbeStore(_ => new TaskCompletionSource().Task); // never finishes
        await using var sp = Build(o => o.Timeout = TimeSpan.FromMilliseconds(200), s => s.AddSingleton<TaskStore>(store));

        var report = await CheckAsync(sp, r => r.Name == A2AHealthChecks.TaskStoreName).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HealthStatus.Unhealthy, Assert.Single(report.Entries).Value.Status);
    }

    /// <summary>A client's error after the time limit must not reach the framework, which would show its message.</summary>
    [Fact]
    public async Task A_failure_after_the_time_limit_is_still_a_clean_result()
    {
        using var expired = new CancellationTokenSource();
        await expired.CancelAsync();
        var check = new TaskStoreHealthCheck(new ProbeStore(_ => throw new InvalidOperationException(Secret)), NullLogger<TaskStoreHealthCheck>.Instance);
        var context = new HealthCheckContext { Registration = new HealthCheckRegistration("x", check, HealthStatus.Unhealthy, null) };

        var result = await check.CheckHealthAsync(context, expired.Token);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.DoesNotContain("hunter2", result.Description);
        Assert.Null(result.Exception);
    }

    [Fact]
    public async Task Shutdown_check_turns_unhealthy_when_the_host_is_stopping()
    {
        var lifetime = new FakeLifetime();
        await using var sp = Build(services: s => s.AddSingleton<IHostApplicationLifetime>(lifetime));
        bool Shutdown(HealthCheckRegistration r) => r.Name == A2AHealthChecks.ShutdownName;

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(sp, Shutdown)).Status);

        lifetime.Stop();
        var entry = Assert.Single((await CheckAsync(sp, Shutdown)).Entries).Value;
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.Contains("stopping", entry.Description);
        Assert.Equal(20d, entry.Data["shutdown_drain_timeout_seconds"]);
    }

    [Fact]
    public async Task Missing_capability_fails_the_start_and_names_the_outbox_type()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<PushOutbox>(new NoStatsOutbox());
        builder.Services.AddHealthChecks().AddA2AHealthChecks();
        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains(typeof(NoStatsOutbox).FullName!, ex.Message);
        Assert.Contains(nameof(IPushOutboxStatistics), ex.Message);
        Assert.Contains("IncludePushOutbox", ex.Message);
    }

    [Fact]
    public async Task Missing_capability_is_fine_when_the_push_check_is_off()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<PushOutbox>(new NoStatsOutbox());
        builder.Services.AddHealthChecks().AddA2AHealthChecks(o => o.IncludePushOutbox = false);
        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public void In_memory_outbox_has_the_capability_by_default()
    {
        using var sp = Build();
        Assert.IsAssignableFrom<IPushOutboxStatistics>(sp.GetRequiredService<PushOutbox>());
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Invalid_options_throw(Action<A2AHealthCheckOptions> configure, string parameter)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddHealthChecks().AddA2AHealthChecks(configure));
        Assert.Equal(parameter, ex.ParamName);
    }

    public static TheoryData<Action<A2AHealthCheckOptions>, string> InvalidOptions() => new()
    {
        { o => o.Timeout = TimeSpan.Zero, nameof(A2AHealthCheckOptions.Timeout) },
        { o => o.Timeout = System.Threading.Timeout.InfiniteTimeSpan, nameof(A2AHealthCheckOptions.Timeout) },
        { o => o.PushBacklogDegradedAge = TimeSpan.Zero, nameof(A2AHealthCheckOptions.PushBacklogDegradedAge) },
        { o => o.PushBacklogUnhealthyAge = TimeSpan.FromSeconds(-1), nameof(A2AHealthCheckOptions.PushBacklogUnhealthyAge) },
        { o => o.DeadLetterDegradedCount = 0, nameof(A2AHealthCheckOptions.DeadLetterDegradedCount) },
        { o => o.DeadLetterUnhealthyCount = -5, nameof(A2AHealthCheckOptions.DeadLetterUnhealthyCount) },
    };

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stop();
        public void Stop() => _stopping.Cancel();
    }

    /// <summary>A task store whose GetAsync runs <paramref name="get"/>. Nothing else is used by a health check.</summary>
    private sealed class ProbeStore(Func<CancellationToken, Task> get) : TaskStore
    {
        public override async ValueTask<TaskRecord?> GetAsync(TaskKey key, CancellationToken ct = default)
        {
            await get(ct);
            return null;
        }

        public override ValueTask<TaskRecord> CreateAsync(TaskRecord record, CancellationToken ct = default) => throw new NotSupportedException();
        public override ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
        public override ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default) => throw new NotSupportedException();
        public override ValueTask<TaskPage> ListAsync(TaskQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public override ValueTask<ContextRecord?> GetContextAsync(string tenant, string contextId, CancellationToken ct = default) => throw new NotSupportedException();
        public override ValueTask<ContextRecord> GetOrCreateContextAsync(ContextRecord record, CancellationToken ct = default) => throw new NotSupportedException();
        public override ValueTask<bool> DeleteAsync(TaskKey key, CancellationToken ct = default) => throw new NotSupportedException();
        public override ValueTask<IReadOnlyList<TaskKey>> ListExpiredAsync(DateTimeOffset terminalBefore, int max, CancellationToken ct = default) => throw new NotSupportedException();
        public override ValueTask<IReadOnlyList<TaskKey>> ListInterruptedBeforeAsync(DateTimeOffset before, int max, CancellationToken ct = default) => throw new NotSupportedException();
        public override ValueTask<TaskRecord?> GetForMaintenanceAsync(string tenant, string taskId, CancellationToken ct = default) => throw new NotSupportedException();
        public override ValueTask<bool> DeleteContextAsync(string tenant, string contextId, CancellationToken ct = default) => throw new NotSupportedException();
        public override ValueTask<IReadOnlyList<ContextRecord>> ListOrphanContextsAsync(DateTimeOffset createdBefore, int max, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>An outbox without the statistics capability. Nothing else is used here.</summary>
    private class NoStatsOutbox : PushOutbox
    {
        public override ValueTask<IReadOnlyList<OutboxEntry>> LeaseDueAsync(int max, TimeSpan lease, CancellationToken ct = default) => throw new NotSupportedException();
        public override ValueTask<bool> CompleteAsync(OutboxEntry leased, CancellationToken ct = default) => throw new NotSupportedException();
        public override ValueTask<bool> RescheduleAsync(OutboxEntry leased, int attempt, DateTimeOffset dueAt, string error, CancellationToken ct = default) => throw new NotSupportedException();
        public override ValueTask<bool> DeadLetterAsync(OutboxEntry leased, string error, CancellationToken ct = default) => throw new NotSupportedException();
        public override ValueTask<IReadOnlyList<OutboxEntry>> ListDeadLettersAsync(string tenant, int max, CancellationToken ct = default) => throw new NotSupportedException();
        public override ValueTask<bool> RedriveAsync(PushDelivery d, CancellationToken ct = default) => throw new NotSupportedException();
        public override ValueTask DeleteAllAsync(TaskKey task, CancellationToken ct = default) => throw new NotSupportedException();
        public override ValueTask<bool> EnqueueAsync(PushDelivery d, ReadOnlyMemory<byte> payload, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>An outbox that returns <see cref="Result"/> (or throws <see cref="Failure"/>) and records the cap it was given.</summary>
    private sealed class StatsOutbox : NoStatsOutbox, IPushOutboxStatistics
    {
        public PushOutboxStatistics Result { get; init; }
        public Exception? Failure { get; init; }
        public int LastCap { get; private set; } = -1;

        public ValueTask<PushOutboxStatistics> GetStatisticsAsync(int maxDeadLetters, CancellationToken ct = default)
        {
            LastCap = maxDeadLetters;
            return Failure is null ? ValueTask.FromResult(Result) : throw Failure;
        }
    }
}
