using A2A.Server.Coordination;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace A2A.Server.Redis;

public static class A2ARedisServiceCollectionExtensions
{
    /// <summary>
    /// Uses <see cref="RedisTaskEventBus"/> as the <see cref="TaskEventBus"/>, connecting with a StackExchange.Redis
    /// configuration string (for example <c>localhost:6379</c>). Works before or after <c>AddA2AServer</c>. A hosted
    /// service starts and stops the subscription. Pair it with a shared event store (for example A2A.Server.Postgres).
    /// Also registers the <see cref="RedisExecutionCoordinator"/> on the same connection, unless
    /// <see cref="RedisEventBusOptions.RegisterCoordinator"/> is false.
    /// </summary>
    public static IServiceCollection AddA2ARedisEventBus(this IServiceCollection services, string configuration,
        Action<RedisEventBusOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(configuration);
        return services.AddBusCore(services.SharedConnection(configuration), configure);
    }

    /// <summary>
    /// Uses <see cref="RedisTaskEventBus"/> over a multiplexer you own (it is not disposed by the container). Also registers
    /// the <see cref="RedisExecutionCoordinator"/>, unless <see cref="RedisEventBusOptions.RegisterCoordinator"/> is false.
    /// </summary>
    public static IServiceCollection AddA2ARedisEventBus(this IServiceCollection services, IConnectionMultiplexer redis,
        Action<RedisEventBusOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(redis);
        return services.AddBusCore(_ => redis, configure);
    }

    /// <summary>Uses <see cref="RedisTaskEventBus"/>. See <see cref="AddA2ARedisEventBus(IServiceCollection, string, Action{RedisEventBusOptions}?)"/>.</summary>
    public static A2AServerBuilder AddRedisEventBus(this A2AServerBuilder builder, string configuration, Action<RedisEventBusOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddA2ARedisEventBus(configuration, configure);
        return builder;
    }

    /// <summary>Uses <see cref="RedisTaskEventBus"/> over a multiplexer you own.</summary>
    public static A2AServerBuilder AddRedisEventBus(this A2AServerBuilder builder, IConnectionMultiplexer redis, Action<RedisEventBusOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddA2ARedisEventBus(redis, configure);
        return builder;
    }

    /// <summary>
    /// Uses <see cref="RedisExecutionCoordinator"/> as the <see cref="ExecutionCoordinator"/>, connecting with a
    /// StackExchange.Redis configuration string. Only <see cref="RedisEventBusOptions.ChannelPrefix"/> is used from the
    /// options; keep it equal to the bus's. Not needed after <c>AddRedisEventBus</c>, which registers the coordinator too.
    /// A hosted service starts and stops the cancel subscription. Works before or after <c>AddA2AServer</c>. Throws
    /// <see cref="InvalidOperationException"/> when an earlier bus or coordinator registration used another configuration string.
    /// </summary>
    public static IServiceCollection AddA2ARedisExecutionCoordinator(this IServiceCollection services, string configuration,
        Action<RedisEventBusOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(configuration);
        return services.AddCoordinatorCore(services.SharedConnection(configuration), Configure(configure));
    }

    /// <summary>Uses <see cref="RedisExecutionCoordinator"/> over a multiplexer you own (it is not disposed by the container).</summary>
    public static IServiceCollection AddA2ARedisExecutionCoordinator(this IServiceCollection services, IConnectionMultiplexer redis,
        Action<RedisEventBusOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(redis);
        return services.AddCoordinatorCore(_ => redis, Configure(configure));
    }

    /// <summary>Uses <see cref="RedisExecutionCoordinator"/>. See <see cref="AddA2ARedisExecutionCoordinator(IServiceCollection, string, Action{RedisEventBusOptions}?)"/>.</summary>
    public static A2AServerBuilder AddRedisExecutionCoordinator(this A2AServerBuilder builder, string configuration, Action<RedisEventBusOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddA2ARedisExecutionCoordinator(configuration, configure);
        return builder;
    }

    /// <summary>Uses <see cref="RedisExecutionCoordinator"/> over a multiplexer you own.</summary>
    public static A2AServerBuilder AddRedisExecutionCoordinator(this A2AServerBuilder builder, IConnectionMultiplexer redis, Action<RedisEventBusOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddA2ARedisExecutionCoordinator(redis, configure);
        return builder;
    }

    private const string ServiceKey = "A2A.Server.Redis";

    /// <summary>
    /// One multiplexer per container for the configuration-string overloads, shared by the bus and the coordinator. A later
    /// registration with a different configuration string would silently use the first one, so it throws instead.
    /// </summary>
    /// <exception cref="InvalidOperationException">An earlier registration used another configuration string.</exception>
    private static Func<IServiceProvider, IConnectionMultiplexer> SharedConnection(this IServiceCollection services, string configuration)
    {
        var earlier = services.FirstOrDefault(d => d.IsKeyedService && Equals(d.ServiceKey, ServiceKey) && d.ServiceType == typeof(SharedConfiguration))
            ?.KeyedImplementationInstance as SharedConfiguration;
        if (earlier is null)
        {
            services.AddKeyedSingleton(ServiceKey, new SharedConfiguration(configuration));
            services.TryAddKeyedSingleton<IConnectionMultiplexer>(ServiceKey, (_, _) => ConnectionMultiplexer.Connect(configuration));
        }
        else if (!string.Equals(earlier.Value, configuration, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The A2A Redis bus and coordinator share one connection, already registered with the configuration '{earlier.Value}'; " +
                $"this registration passes '{configuration}'. Use the same configuration string, or pass an IConnectionMultiplexer.");
        }
        return sp => sp.GetRequiredKeyedService<IConnectionMultiplexer>(ServiceKey);
    }

    /// <summary>The configuration string of the shared connection (see <see cref="SharedConnection"/>).</summary>
    private sealed record SharedConfiguration(string Value);

    private static RedisEventBusOptions Configure(Action<RedisEventBusOptions>? configure)
    {
        var options = new RedisEventBusOptions();
        configure?.Invoke(options);
        return options;
    }

    private static IServiceCollection AddBusCore(this IServiceCollection services, Func<IServiceProvider, IConnectionMultiplexer> redis,
        Action<RedisEventBusOptions>? configure)
    {
        var options = Configure(configure);
        services.Replace(ServiceDescriptor.Singleton(sp => new RedisTaskEventBus(redis(sp), options, sp.GetService<ILogger<RedisTaskEventBus>>())));
        services.Replace(ServiceDescriptor.Singleton<TaskEventBus>(sp => sp.GetRequiredService<RedisTaskEventBus>()));
        services.AddSingleton<IHostedService>(sp => new RedisEventBusHostedService(sp.GetRequiredService<RedisTaskEventBus>()));
        if (options.RegisterCoordinator) services.AddCoordinatorCore(redis, options);
        return services;
    }

    private static IServiceCollection AddCoordinatorCore(this IServiceCollection services, Func<IServiceProvider, IConnectionMultiplexer> redis,
        RedisEventBusOptions options)
    {
        services.Replace(ServiceDescriptor.Singleton(sp => new RedisExecutionCoordinator(redis(sp), options,
            sp.GetService<IOptions<A2AServerOptions>>()?.Value.Coordination, sp.GetService<ILogger<RedisExecutionCoordinator>>())));
        services.Replace(ServiceDescriptor.Singleton<ExecutionCoordinator>(sp => sp.GetRequiredService<RedisExecutionCoordinator>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RedisCoordinatorHostedService>(
            sp => new RedisCoordinatorHostedService(sp.GetRequiredService<RedisExecutionCoordinator>())));
        return services;
    }
}

/// <summary>Starts the bus subscription with the host and stops it on shutdown.</summary>
internal sealed class RedisEventBusHostedService(RedisTaskEventBus bus) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => bus.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => bus.StopAsync();
}

/// <summary>Starts the coordinator's cancel subscription with the host and stops it on shutdown.</summary>
internal sealed class RedisCoordinatorHostedService(RedisExecutionCoordinator coordinator) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => coordinator.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => coordinator.StopAsync();
}
