using A2A.Server.Idempotency;
using A2A.Server.Push;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace A2A.Server.Postgres;

public static class A2APostgresServiceCollectionExtensions
{
    /// <summary>
    /// Uses PostgreSQL for the task, event, push config, push outbox and idempotency stores. Works before or after <c>AddA2AServer</c>
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

    /// <summary>Key of the data source this package creates from a connection string.</summary>
    private const string ServiceKey = "A2A.Server.Postgres";

    private static IServiceCollection AddCore(this IServiceCollection services, Func<IServiceProvider, NpgsqlDataSource> db,
        Action<PostgresStoreOptions>? configure)
    {
        var options = new PostgresStoreOptions();
        configure?.Invoke(options);
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
        return services;
    }
}
