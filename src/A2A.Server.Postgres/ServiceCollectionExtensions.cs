using A2A.Server.Audit;
using A2A.Server.Coordination;
using A2A.Server.Idempotency;
using A2A.Server.Push;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace A2A.Server.Postgres;

/// <summary>Registers the PostgreSQL stores with the A2A server.</summary>
public static class A2APostgresServiceCollectionExtensions
{
    /// <summary>
    /// Uses PostgreSQL for the task, event, push config, push outbox and idempotency stores, and for the execution coordinator
    /// (<see cref="PostgresExecutionCoordinator"/>) unless <see cref="PostgresStoreOptions.RegisterCoordinator"/> is false or you
    /// registered another coordinator (for example the Redis one), in any order. Works before or after <c>AddA2AServer</c>
    /// (it replaces the in-memory defaults). Builds an <see cref="NpgsqlDataSource"/> from <paramref name="connectionString"/>
    /// and disposes it with the container.
    /// </summary>
    public static IServiceCollection AddA2APostgresStores(this IServiceCollection services, string connectionString,
        Action<PostgresStoreOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        services.TryAddKeyedSingleton(ServiceKey, (_, _) => NpgsqlDataSource.Create(connectionString));
        return services.AddCore(sp => sp.GetRequiredKeyedService<NpgsqlDataSource>(ServiceKey), configure);
    }

    /// <summary>Uses PostgreSQL for all A2A stores, through a data source you own (it is not disposed by the container).</summary>
    public static IServiceCollection AddA2APostgresStores(this IServiceCollection services, NpgsqlDataSource dataSource,
        Action<PostgresStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return services.AddCore(_ => dataSource, configure);
    }

    /// <summary>Uses PostgreSQL for all A2A stores. See <see cref="AddA2APostgresStores(IServiceCollection, string, Action{PostgresStoreOptions}?)"/>.</summary>
    public static A2AServerBuilder AddPostgresStores(this A2AServerBuilder builder, string connectionString, Action<PostgresStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddA2APostgresStores(connectionString, configure);
        return builder;
    }

    /// <summary>Uses PostgreSQL for all A2A stores, through a data source you own.</summary>
    public static A2AServerBuilder AddPostgresStores(this A2AServerBuilder builder, NpgsqlDataSource dataSource, Action<PostgresStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddA2APostgresStores(dataSource, configure);
        return builder;
    }

    /// <summary>
    /// Uses <see cref="PostgresExecutionCoordinator"/> as the <see cref="ExecutionCoordinator"/>, whatever coordinator was
    /// registered before: leases, running checks, cancel requests and the sweep lease live in PostgreSQL, so a crash does not
    /// lose them. Not needed after <c>AddA2APostgresStores</c>, which registers it by default. A hosted service starts and stops
    /// the cancel poll (and creates the tables when <see cref="PostgresStoreOptions.CreateSchemaOnStartup"/> is on). Builds an
    /// <see cref="NpgsqlDataSource"/> from <paramref name="connectionString"/> (shared with <c>AddA2APostgresStores</c> when
    /// both use the same one) and disposes it with the container.
    /// </summary>
    public static IServiceCollection AddA2APostgresExecutionCoordinator(this IServiceCollection services, string connectionString,
        Action<PostgresStoreOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        services.TryAddKeyedSingleton(ServiceKey, (_, _) => NpgsqlDataSource.Create(connectionString));
        return services.AddCoordinatorCore(sp => sp.GetRequiredKeyedService<NpgsqlDataSource>(ServiceKey), Configure(configure), replace: true);
    }

    /// <summary>Uses <see cref="PostgresExecutionCoordinator"/> through a data source you own (it is not disposed by the container).</summary>
    public static IServiceCollection AddA2APostgresExecutionCoordinator(this IServiceCollection services, NpgsqlDataSource dataSource,
        Action<PostgresStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return services.AddCoordinatorCore(_ => dataSource, Configure(configure), replace: true);
    }

    /// <summary>Uses <see cref="PostgresExecutionCoordinator"/>. See <see cref="AddA2APostgresExecutionCoordinator(IServiceCollection, string, Action{PostgresStoreOptions}?)"/>.</summary>
    public static A2AServerBuilder AddPostgresExecutionCoordinator(this A2AServerBuilder builder, string connectionString, Action<PostgresStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddA2APostgresExecutionCoordinator(connectionString, configure);
        return builder;
    }

    /// <summary>Uses <see cref="PostgresExecutionCoordinator"/> through a data source you own.</summary>
    public static A2AServerBuilder AddPostgresExecutionCoordinator(this A2AServerBuilder builder, NpgsqlDataSource dataSource, Action<PostgresStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddA2APostgresExecutionCoordinator(dataSource, configure);
        return builder;
    }

    /// <summary>
    /// Writes the audit trail to PostgreSQL (<see cref="PostgresAuditSink"/>, table <c>a2a_audit_log</c>) instead of the log. It
    /// replaces the sink registered before, and a later <c>AddAuditSink</c> replaces this one. Works with or without
    /// <c>AddA2APostgresStores</c>, before or after <c>AddA2AServer</c>. A hosted service creates the table when
    /// <see cref="PostgresStoreOptions.CreateSchemaOnStartup"/> is on. Builds an <see cref="NpgsqlDataSource"/> from
    /// <paramref name="connectionString"/> (shared with the stores when they use the same one) and disposes it with the container.
    /// </summary>
    public static IServiceCollection AddA2APostgresAuditSink(this IServiceCollection services, string connectionString,
        Action<PostgresStoreOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        services.TryAddKeyedSingleton(ServiceKey, (_, _) => NpgsqlDataSource.Create(connectionString));
        return services.AddAuditCore(sp => sp.GetRequiredKeyedService<NpgsqlDataSource>(ServiceKey), configure);
    }

    /// <summary>Writes the audit trail to PostgreSQL through a data source you own (it is not disposed by the container). See <see cref="AddA2APostgresAuditSink(IServiceCollection, string, Action{PostgresStoreOptions}?)"/>.</summary>
    public static IServiceCollection AddA2APostgresAuditSink(this IServiceCollection services, NpgsqlDataSource dataSource,
        Action<PostgresStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return services.AddAuditCore(_ => dataSource, configure);
    }

    /// <summary>Writes the audit trail to PostgreSQL. See <see cref="AddA2APostgresAuditSink(IServiceCollection, string, Action{PostgresStoreOptions}?)"/>.</summary>
    public static A2AServerBuilder AddPostgresAuditSink(this A2AServerBuilder builder, string connectionString, Action<PostgresStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddA2APostgresAuditSink(connectionString, configure);
        return builder;
    }

    /// <summary>Writes the audit trail to PostgreSQL through a data source you own.</summary>
    public static A2AServerBuilder AddPostgresAuditSink(this A2AServerBuilder builder, NpgsqlDataSource dataSource, Action<PostgresStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddA2APostgresAuditSink(dataSource, configure);
        return builder;
    }

    private static IServiceCollection AddAuditCore(this IServiceCollection services, Func<IServiceProvider, NpgsqlDataSource> db,
        Action<PostgresStoreOptions>? configure)
    {
        var options = Configure(configure);
        services.Replace(ServiceDescriptor.Singleton<A2AAuditSink>(sp => new PostgresAuditSink(db(sp), options)));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PostgresAuditHostedService>(
            sp => new PostgresAuditHostedService(db(sp), options)));
        return services;
    }

    private static PostgresStoreOptions Configure(Action<PostgresStoreOptions>? configure)
    {
        var options = new PostgresStoreOptions();
        configure?.Invoke(options);
        return options;
    }

    /// <summary>Key of the data source this package creates from a connection string.</summary>
    private const string ServiceKey = "A2A.Server.Postgres";

    private static IServiceCollection AddCore(this IServiceCollection services, Func<IServiceProvider, NpgsqlDataSource> db,
        Action<PostgresStoreOptions>? configure)
    {
        var options = Configure(configure);
        services.AddOptions<A2AServerOptions>();

        services.Replace(ServiceDescriptor.Singleton<TaskStore>(sp => new PostgresTaskStore(
            db(sp), sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Streaming, options, sp.GetService<TimeProvider>())));
        services.Replace(ServiceDescriptor.Singleton<EventStore>(sp => new PostgresEventStore(
            db(sp), sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Streaming, options, sp.GetService<TimeProvider>())));
        services.Replace(ServiceDescriptor.Singleton<PushConfigStore>(sp => new PostgresPushConfigStore(db(sp), options)));
        services.Replace(ServiceDescriptor.Singleton<PushOutbox>(sp => new PostgresPushOutbox(db(sp), options, sp.GetService<TimeProvider>())));
        services.Replace(ServiceDescriptor.Singleton<IdempotencyStore>(sp => new PostgresIdempotencyStore(
            db(sp), sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Idempotency, options, sp.GetService<TimeProvider>())));
        services.Replace(ServiceDescriptor.Singleton(sp => new PostgresMaintenance(db(sp), options, sp.GetService<TimeProvider>())));
        services.AddSingleton<IHostedService>(sp => new PostgresStoreHostedService(
            db(sp), options, sp.GetRequiredService<PostgresMaintenance>(), sp.GetService<TimeProvider>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<PostgresStoreHostedService>>()));
        // The default coordinator: it keeps its leases through a crash, so the retention sweep can recover the tasks that were
        // running. Never over a coordinator the host chose (Redis, or its own), whatever the registration order.
        if (options.RegisterCoordinator) services.AddCoordinatorCore(db, options, replace: false);
        return services;
    }

    private static IServiceCollection AddCoordinatorCore(this IServiceCollection services, Func<IServiceProvider, NpgsqlDataSource> db,
        PostgresStoreOptions options, bool replace)
    {
        if (!replace && services.LastOrDefault(d => d.ServiceType == typeof(ExecutionCoordinator) && !d.IsKeyedService) is { } existing
            && !IsReplaceableDefault(existing))
            return services;
        services.Replace(ServiceDescriptor.Singleton(sp => new PostgresExecutionCoordinator(db(sp), options,
            sp.GetService<IOptions<A2AServerOptions>>()?.Value.Coordination, sp.GetService<TimeProvider>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<PostgresExecutionCoordinator>>())));
        services.Replace(ServiceDescriptor.Singleton<ExecutionCoordinator>(sp => sp.GetRequiredService<PostgresExecutionCoordinator>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PostgresCoordinatorHostedService>(
            sp => new PostgresCoordinatorHostedService(sp, db(sp), options)));
        return services;
    }

    /// <summary>
    /// True for the registrations that stand for "no choice made": the in-memory default of <c>AddA2AServer</c> and an earlier
    /// default of this package. Anything else (the Redis coordinator, a host's own) is a choice.
    /// </summary>
    private static bool IsReplaceableDefault(ServiceDescriptor descriptor)
    {
        var assembly = descriptor.ImplementationFactory?.Method.DeclaringType?.Assembly;
        return assembly == typeof(A2AServerOptions).Assembly || assembly == typeof(PostgresExecutionCoordinator).Assembly;
    }
}

/// <summary>Creates the tables if asked to, then starts the coordinator's cancel poll with the host and stops it on shutdown.</summary>
internal sealed class PostgresCoordinatorHostedService(IServiceProvider services, NpgsqlDataSource dataSource, PostgresStoreOptions options) : IHostedService
{
    private PostgresExecutionCoordinator? _started;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Another coordinator (for example Redis's, registered after the stores) took over: this one is not used, so it stays idle.
        if (services.GetRequiredService<ExecutionCoordinator>() is not PostgresExecutionCoordinator coordinator) return;
        _started = coordinator;
        // Idempotent and serialized by an advisory lock, so it is safe next to the stores' own hosted service, in any order.
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger<PostgresCoordinatorHostedService>();
        await PostgresSchema.PrepareForStartupAsync(dataSource, options, logger, cancellationToken).ConfigureAwait(false);
        await coordinator.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => _started?.StopAsync() ?? Task.CompletedTask;
}

/// <summary>Creates the audit table at startup, when <see cref="PostgresStoreOptions.CreateSchemaOnStartup"/> is on. Idempotent and serialized by an advisory lock, so it is safe next to the stores' own hosted service.</summary>
internal sealed class PostgresAuditHostedService(NpgsqlDataSource dataSource, PostgresStoreOptions options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        options.CreateSchemaOnStartup ? PostgresSchema.EnsureSchemaAsync(dataSource, options.Schema, cancellationToken) : Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
