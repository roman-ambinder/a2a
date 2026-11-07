using A2A.Server.Push;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace A2A.Server;

public static class A2AServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the A2A server core with <typeparamref name="TExecutor"/> as the agent (scoped: one instance per execution).
    /// Defaults: in-memory stores, owner+tenant access policy, HTTP push sender with SSRF guard. Register your own
    /// <see cref="ITaskStore"/>, <see cref="IPushNotificationConfigStore"/>, or <see cref="ITaskAccessPolicy"/> before or after to replace them.
    /// </summary>
    public static IServiceCollection AddA2AServer<TExecutor>(this IServiceCollection services, Action<A2AServerOptions> configure)
        where TExecutor : class, IAgentExecutor
    {
        services.TryAddScoped<IAgentExecutor, TExecutor>();
        return services.AddA2AServerCore(configure);
    }

    /// <summary>Registers the A2A server core with a delegate as the agent.</summary>
    public static IServiceCollection AddA2AServer(this IServiceCollection services, Action<A2AServerOptions> configure,
        Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute)
    {
        services.TryAddSingleton<IAgentExecutor>(new DelegateAgentExecutor(execute));
        return services.AddA2AServerCore(configure);
    }

    private static IServiceCollection AddA2AServerCore(this IServiceCollection services, Action<A2AServerOptions> configure)
    {
        services.AddOptions<A2AServerOptions>().Configure(configure);
        services.TryAddSingleton<ITaskStore, InMemoryTaskStore>();
        services.TryAddSingleton<IPushNotificationConfigStore, InMemoryPushNotificationConfigStore>();
        services.TryAddSingleton<ITaskAccessPolicy, OwnerTenantAccessPolicy>();
        services.TryAddSingleton<TaskEventBus>();
        services.AddHttpClient(HttpPushNotificationSender.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => PushUrlValidator.CreateHandler(sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.PushNotifications));
        services.TryAddSingleton<HttpPushNotificationSender>();
        services.TryAddSingleton<IPushNotificationSender>(sp => sp.GetRequiredService<HttpPushNotificationSender>());
        services.TryAddSingleton<A2ARequestHandler>();
        return services;
    }
}
