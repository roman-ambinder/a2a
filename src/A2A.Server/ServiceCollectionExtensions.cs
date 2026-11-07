using System.Diagnostics.CodeAnalysis;
using A2A.Server.Push;
using A2A.Server.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace A2A.Server;

/// <summary>Dependency injection extensions for the A2A server core.</summary>
public static class A2AServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the A2A server core and returns a builder to add the agent, its card and extra services.
    /// Defaults: in-memory stores (tasks, events, push configs, idempotency), HTTP push sender with SSRF guard, allow-all authorization handler. Register your own
    /// <see cref="Storage.TaskStore"/>, <see cref="Storage.PushConfigStore"/> or <see cref="Idempotency.IdempotencyStore"/> before or after to replace them.
    /// </summary>
    public static A2AServerBuilder AddA2AServer(this IServiceCollection services, Action<A2AServerOptions>? configure = null)
    {
        var options = services.AddOptions<A2AServerOptions>();
        if (configure is not null) options.Configure(configure);
        services.TryAddSingleton<Storage.TaskStore, Storage.InMemoryTaskStore>();
        services.TryAddSingleton<Storage.PushConfigStore, Storage.InMemoryPushConfigStore>();
        services.TryAddSingleton<Storage.EventStore>(sp => new Storage.InMemoryEventStore(
            sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Streaming, sp.GetService<TimeProvider>()));
        services.TryAddSingleton<Idempotency.IdempotencyStore>(sp => new Idempotency.InMemoryIdempotencyStore(
            sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Idempotency, sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IPushSecretProtector, EphemeralPushSecretProtector>();
        services.TryAddSingleton<IPrincipalResolver, DefaultPrincipalResolver>();
        services.TryAddSingleton<A2AAuthorizationHandler, AllowAllAuthorizationHandler>();
        services.TryAddSingleton<TaskEventBus>();
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<A2AServerOptions>>().Value);
        services.TryAddSingleton<IAgentExecutorFactory, ScopedAgentExecutorFactory>();
        services.TryAddKeyedSingleton<HttpMessageInvoker>(HttpPushNotificationSender.InvokerServiceKey, (sp, _) => new HttpMessageInvoker(
            PushUrlValidator.CreateHandler(sp.GetRequiredService<A2AServerOptions>().PushNotifications), disposeHandler: true));
        services.TryAddSingleton(sp => new HttpPushNotificationSender(
            sp.GetRequiredKeyedService<HttpMessageInvoker>(HttpPushNotificationSender.InvokerServiceKey),
            sp.GetRequiredService<Storage.PushConfigStore>(), sp.GetRequiredService<IPushSecretProtector>(),
            sp.GetRequiredService<A2AServerOptions>(), sp.GetService<Microsoft.Extensions.Logging.ILogger<HttpPushNotificationSender>>(),
            sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IPushNotificationSender>(sp => sp.GetRequiredService<HttpPushNotificationSender>());
        services.TryAddSingleton<IPushConfigChangeListener>(sp => sp.GetRequiredService<HttpPushNotificationSender>());
        services.TryAddSingleton<Pipeline.CallValidator>();
        services.TryAddSingleton<PushConfigService>();
        services.TryAddSingleton<Idempotency.IdempotencyCoordinator>();
        services.TryAddSingleton<A2ARequestHandler>();
        return new A2AServerBuilder(services);
    }
}

/// <summary>Fluent setup for the A2A server. Created by <c>AddA2AServer</c>.</summary>
public sealed class A2AServerBuilder
{
    internal A2AServerBuilder(IServiceCollection services) => Services = services;

    /// <summary>The underlying service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Uses <typeparamref name="T"/> as the agent. Scoped: one instance per execution. Replaces an earlier executor.</summary>
    public A2AServerBuilder AddAgentExecutor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, IAgentExecutor
    {
        Services.Replace(ServiceDescriptor.Scoped<IAgentExecutor, T>());
        return this;
    }

    /// <summary>Uses a delegate as the agent. Replaces an earlier executor.</summary>
    public A2AServerBuilder AddAgentExecutor(Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute)
    {
        Services.Replace(ServiceDescriptor.Singleton<IAgentExecutor>(new DelegateAgentExecutor(execute)));
        return this;
    }

    /// <summary>Sets the public Agent Card (same as setting <see cref="A2AServerOptions.AgentCard"/>).</summary>
    public A2AServerBuilder AddAgentCard(AgentCard card)
    {
        Services.Configure<A2AServerOptions>(o => o.AgentCard = card);
        return this;
    }

    /// <summary>Uses <typeparamref name="T"/> to authorize every operation. Replaces an earlier handler.</summary>
    public A2AServerBuilder AddAuthorizationHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : A2AAuthorizationHandler
    {
        Services.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler, T>());
        return this;
    }

    /// <summary>
    /// Uses the in-memory task, push config, event and idempotency stores. They are already registered by default, so this changes nothing
    /// unless you registered your own stores earlier (it does not replace them).
    /// </summary>
    public A2AServerBuilder AddInMemoryStores()
    {
        Services.TryAddSingleton<Storage.TaskStore, Storage.InMemoryTaskStore>();
        Services.TryAddSingleton<Storage.PushConfigStore, Storage.InMemoryPushConfigStore>();
        Services.TryAddSingleton<Storage.EventStore>(sp => new Storage.InMemoryEventStore(
            sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Streaming, sp.GetService<TimeProvider>()));
        Services.TryAddSingleton<Idempotency.IdempotencyStore>(sp => new Idempotency.InMemoryIdempotencyStore(
            sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Idempotency, sp.GetService<TimeProvider>()));
        return this;
    }
}
