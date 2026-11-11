using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Postgres;
using A2A.Server.Push;
using A2A.Server.Redis;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace A2A.Stores.Tests;

/// <summary>The A2A health checks over real PostgreSQL stores and a real Redis coordinator, and over an unreachable database.</summary>
[Collection(ContainersCollection.Name)]
public sealed class HealthCheckStoreTests(PostgresFixture db, RedisFixture redisFixture)
{
    private static readonly TaskKey Alice = new("t", "alice", "task1");

    /// <summary>A container with logging and a lifetime: the shutdown check needs one, and a plain container has no host.</summary>
    private static ServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, IdleLifetime>();
        return services;
    }

    private async Task<ServiceProvider> PostgresAsync(Action<A2AHealthCheckOptions>? configure = null)
    {
        var schema = await db.NewSchemaAsync();
        var services = NewServices();
        services.AddA2AServer();
        services.AddA2APostgresStores(db.DataSource, o =>
        {
            o.Schema = schema.Schema;
            o.CreateSchemaOnStartup = false;
        });
        services.AddHealthChecks().AddA2AHealthChecks(configure);
        return services.BuildServiceProvider();
    }

    private static async Task<HealthReport> CheckAsync(ServiceProvider sp) => await sp.GetRequiredService<HealthCheckService>().CheckHealthAsync();

    /// <summary>Dead-letters one delivery per sequence in <paramref name="sequences"/>.</summary>
    private static async Task DeadLetterAsync(PushOutbox outbox, params long[] sequences)
    {
        foreach (var seq in sequences)
        {
            Assert.True(await outbox.EnqueueAsync(new PushDelivery(Alice, "c1", seq, $"id-{seq}"), new byte[] { 1 }));
            // Only the lowest sequence of a task and config is leased at a time.
            Assert.True(await outbox.DeadLetterAsync(Assert.Single(await outbox.LeaseDueAsync(10, TimeSpan.FromMinutes(1))), "HTTP 404"));
        }
    }

    [Fact]
    public async Task Postgres_stores_and_coordinator_are_all_healthy()
    {
        await using var sp = await PostgresAsync();

        var report = await CheckAsync(sp);

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Equal(4, report.Entries.Count);
    }

    [Fact]
    public async Task Dead_letters_in_postgres_trip_the_count_thresholds()
    {
        await using var sp = await PostgresAsync(o =>
        {
            o.DeadLetterDegradedCount = 2;
            o.DeadLetterUnhealthyCount = 3;
        });
        var outbox = sp.GetRequiredService<PushOutbox>();
        HealthReportEntry Push(HealthReport r) => r.Entries[A2AHealthChecks.PushOutboxName];

        await DeadLetterAsync(outbox, 1);
        Assert.Equal(HealthStatus.Healthy, Push(await CheckAsync(sp)).Status);

        await DeadLetterAsync(outbox, 2);
        Assert.Equal(HealthStatus.Degraded, Push(await CheckAsync(sp)).Status);

        await DeadLetterAsync(outbox, 3, 4);
        var entry = Push(await CheckAsync(sp));
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.Equal(3, entry.Data["dead_letters"]); // 4 exist; counting stops at the highest threshold
    }

    [Fact]
    public async Task A_waiting_delivery_in_postgres_trips_the_age_threshold()
    {
        await using var sp = await PostgresAsync(o => o.PushBacklogDegradedAge = TimeSpan.FromMilliseconds(10));
        var outbox = sp.GetRequiredService<PushOutbox>();
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(sp)).Entries[A2AHealthChecks.PushOutboxName].Status);

        Assert.True(await outbox.EnqueueAsync(new PushDelivery(Alice, "c1", 1, "id-1"), new byte[] { 1 }));

        // Wait until the delivery is older than the 10 ms threshold rather than for a fixed time.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var entry = (await CheckAsync(sp)).Entries[A2AHealthChecks.PushOutboxName];
        while (entry.Status != HealthStatus.Degraded && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            entry = (await CheckAsync(sp)).Entries[A2AHealthChecks.PushOutboxName];
        }
        Assert.Equal(HealthStatus.Degraded, entry.Status);
        Assert.True((double)entry.Data["oldest_due_age_seconds"] >= 0.01);
    }

    [Fact]
    public async Task Redis_coordinator_is_healthy()
    {
        await using var redis = await redisFixture.ConnectAsync();
        var services = NewServices();
        services.AddA2AServer().AddRedisExecutionCoordinator(redis, o => o.ChannelPrefix = $"health:{Guid.NewGuid():N}:");
        services.AddHealthChecks().AddA2AHealthChecks();
        await using var sp = services.BuildServiceProvider();

        var report = await sp.GetRequiredService<HealthCheckService>().CheckHealthAsync(r => r.Name == A2AHealthChecks.CoordinatorName);

        Assert.Equal(HealthStatus.Healthy, Assert.Single(report.Entries).Value.Status);
        Assert.IsType<RedisExecutionCoordinator>(sp.GetRequiredService<A2A.Server.Coordination.ExecutionCoordinator>());
    }

    /// <summary>No Docker needed: nothing listens on port 1.</summary>
    [Fact]
    public async Task Unreachable_postgres_is_unhealthy_and_shows_no_connection_details()
    {
        var services = NewServices();
        services.AddA2AServer();
        services.AddA2APostgresStores("Host=127.0.0.1;Port=1;Username=secret-user;Password=s3cret-pw;Database=x;Timeout=1", o => o.CreateSchemaOnStartup = false);
        services.AddHealthChecks().AddA2AHealthChecks(o => o.Timeout = TimeSpan.FromSeconds(30));
        await using var sp = services.BuildServiceProvider();

        var report = await CheckAsync(sp);

        Assert.Equal(HealthStatus.Unhealthy, report.Entries[A2AHealthChecks.TaskStoreName].Status);
        Assert.Equal(HealthStatus.Unhealthy, report.Entries[A2AHealthChecks.CoordinatorName].Status);
        Assert.Equal(HealthStatus.Degraded, report.Entries[A2AHealthChecks.PushOutboxName].Status);
        foreach (var entry in report.Entries.Values)
        {
            var visible = entry.Description + "|" + string.Join(",", entry.Data.Select(d => $"{d.Key}={d.Value}")) + "|" + entry.Exception;
            Assert.DoesNotContain("s3cret-pw", visible);
            Assert.DoesNotContain("secret-user", visible);
            Assert.DoesNotContain("127.0.0.1", visible);
            Assert.Null(entry.Exception);
        }
    }

    private sealed class IdleLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
