using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Postgres;
using A2A.Server.Redis;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace A2A.Stores.Tests;

/// <summary>
/// Startup warnings for mixes of durable and process-local parts that work on one instance and break across restarts or
/// several instances. The host starts for real (stores on PostgreSQL, coordinator on Redis); nothing throws.
/// </summary>
[Collection(ContainersCollection.Name)]
public sealed class StartupDiagnosticsTests(PostgresFixture db, RedisFixture redisFixture)
{
    private const string EphemeralProtector = "EphemeralPushSecretProtector";
    private const string LocalCoordinator = "InMemoryExecutionCoordinator";
    private const string RetentionOff = "AddA2ARetention";

    private static async Task<List<(LogLevel Level, string Message)>> StartAsync(Action<IServiceCollection> configure)
    {
        var logs = new CaptureLogger();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        configure(builder.Services);
        using var host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();
        lock (logs.Entries) return logs.Entries.ToList();
    }

    private Task<List<(LogLevel Level, string Message)>> StartWithPostgresStoresAsync(PostgresStoreOptions schema) =>
        StartAsync(s => s.AddA2AServer().AddPostgresStores(db.DataSource, o =>
        {
            o.Schema = schema.Schema;
            o.CreateSchemaOnStartup = false;
        }));

    /// <summary>A durable push outbox with the default per-process protector: secrets cannot be read after a restart.</summary>
    [SkippableFact]
    public async Task Durable_outbox_with_the_per_process_protector_logs_an_error()
    {
        var logs = await StartWithPostgresStoresAsync(await db.NewSchemaAsync());

        var entry = Assert.Single(logs, e => e.Message.Contains(EphemeralProtector, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("dead-lettered", entry.Message, StringComparison.Ordinal);
        Assert.Contains("AddA2ADataProtectionPushSecrets", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>A durable task store with the default process-local coordinator: cancel and running checks stay local.</summary>
    [SkippableFact]
    public async Task Durable_task_store_with_the_local_coordinator_logs_a_warning()
    {
        var logs = await StartWithPostgresStoresAsync(await db.NewSchemaAsync());

        var entry = Assert.Single(logs, e => e.Message.Contains(LocalCoordinator, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("process-local", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>A durable task store without the retention sweep: nothing is ever deleted or timed out.</summary>
    [SkippableFact]
    public async Task Durable_task_store_without_retention_logs_a_warning()
    {
        var logs = await StartWithPostgresStoresAsync(await db.NewSchemaAsync());

        var entry = Assert.Single(logs, e => e.Message.Contains(RetentionOff, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("never deleted", entry.Message, StringComparison.Ordinal);
        Assert.Contains("never time out", entry.Message, StringComparison.Ordinal);
        Assert.Contains("stay WORKING", entry.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Persistent_protector_and_shared_coordinator_log_nothing()
    {
        var schema = await db.NewSchemaAsync();
        var redis = await redisFixture.ConnectAsync();
        await using var _ = redis;
        var logs = await StartAsync(s =>
        {
            s.AddA2AServer()
                .AddPostgresStores(db.DataSource, o =>
                {
                    o.Schema = schema.Schema;
                    o.CreateSchemaOnStartup = false;
                })
                .AddRedisExecutionCoordinator(redis, o => o.ChannelPrefix = $"diag:{Guid.NewGuid():N}:");
            s.AddDataProtection();
            s.AddA2ADataProtectionPushSecrets();
            s.AddA2ARetention();
        });

        Assert.DoesNotContain(logs, e => e.Message.Contains(EphemeralProtector, StringComparison.Ordinal));
        Assert.DoesNotContain(logs, e => e.Message.Contains(LocalCoordinator, StringComparison.Ordinal));
        Assert.DoesNotContain(logs, e => e.Message.Contains(RetentionOff, StringComparison.Ordinal));
    }

    [Fact]
    public async Task In_memory_defaults_log_nothing()
    {
        var logs = await StartAsync(s => s.AddA2AServer());

        Assert.DoesNotContain(logs, e => e.Message.Contains(EphemeralProtector, StringComparison.Ordinal));
        Assert.DoesNotContain(logs, e => e.Message.Contains(LocalCoordinator, StringComparison.Ordinal));
        Assert.DoesNotContain(logs, e => e.Message.Contains(RetentionOff, StringComparison.Ordinal));
    }

    private sealed class CaptureLogger : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Sink(this);
        public void Dispose() { }

        private sealed class Sink(CaptureLogger owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries) owner.Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
