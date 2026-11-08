using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace A2A.Server.Redis;

/// <summary>Registers the Redis event bus (<see cref="RedisTaskEventBus"/>) with the A2A server.</summary>
public static class A2ARedisServiceCollectionExtensions
{
    /// <summary>
    /// Uses <see cref="RedisTaskEventBus"/> as the <see cref="TaskEventBus"/>, connecting with a StackExchange.Redis
    /// configuration string (for example <c>localhost:6379</c>). Works before or after <c>AddA2AServer</c>. A hosted
    /// service starts and stops the subscription. Pair it with a shared event store (for example A2A.Server.Postgres).
    /// </summary>
    public static IServiceCollection AddA2ARedisEventBus(this IServiceCollection services, string configuration,
        Action<RedisEventBusOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(configuration);
        services.TryAddKeyedSingleton<IConnectionMultiplexer>(ServiceKey, (_, _) => ConnectionMultiplexer.Connect(configuration));
        return services.AddCore(sp => sp.GetRequiredKeyedService<IConnectionMultiplexer>(ServiceKey), configure);
    }

    /// <summary>Uses <see cref="RedisTaskEventBus"/> over a multiplexer you own (it is not disposed by the container).</summary>
    public static IServiceCollection AddA2ARedisEventBus(this IServiceCollection services, IConnectionMultiplexer redis,
        Action<RedisEventBusOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(redis);
        return services.AddCore(_ => redis, configure);
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

    private const string ServiceKey = "A2A.Server.Redis";

    private static IServiceCollection AddCore(this IServiceCollection services, Func<IServiceProvider, IConnectionMultiplexer> redis,
        Action<RedisEventBusOptions>? configure)
    {
        var options = new RedisEventBusOptions();
        configure?.Invoke(options);
        services.AddOptions<A2AServerOptions>();
        services.Replace(ServiceDescriptor.Singleton(sp => new RedisTaskEventBus(
            redis(sp), sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Streaming, options, sp.GetService<ILogger<RedisTaskEventBus>>())));
        services.Replace(ServiceDescriptor.Singleton<TaskEventBus>(sp => sp.GetRequiredService<RedisTaskEventBus>()));
        services.AddSingleton<IHostedService>(sp => new RedisEventBusHostedService(sp.GetRequiredService<RedisTaskEventBus>()));
        return services;
    }
}

/// <summary>Starts the bus subscription with the host and stops it on shutdown.</summary>
internal sealed class RedisEventBusHostedService(RedisTaskEventBus bus) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => bus.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => bus.StopAsync();
}
