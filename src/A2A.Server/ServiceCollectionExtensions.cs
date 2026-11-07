using System.Diagnostics.CodeAnalysis;
using A2A.Server.Push;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace A2A.Server;

public static class A2AServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the A2A server core and returns a builder to add the agent, its card and extra services.
    /// Defaults: in-memory stores (tasks, events, push outbox, push configs, idempotency), a hosted <see cref="PushDeliveryService"/>
    /// that sends push notifications through an HTTP client with the SSRF guard, the <see cref="A2APushAdmin"/> dead-letter API,
    /// and an allow-all authorization handler. Register your own <see cref="Storage.TaskStore"/> (with the
    /// <see cref="Storage.EventStore"/> and <see cref="PushOutbox"/> it commits to), <see cref="Storage.PushConfigStore"/> or
    /// <see cref="Idempotency.IdempotencyStore"/> before or after to replace them.
    /// </summary>
    public static A2AServerBuilder AddA2AServer(this IServiceCollection services, Action<A2AServerOptions>? configure = null)
    {
        var options = services.AddOptions<A2AServerOptions>();
        if (configure is not null) options.Configure(configure);
        services.TryAddSingleton<Storage.TaskStore>(InMemoryTaskStore);
        services.TryAddSingleton<Storage.PushConfigStore, Storage.InMemoryPushConfigStore>();
        services.TryAddSingleton<Storage.EventStore>(InMemoryEventStore);
        services.TryAddSingleton<PushOutbox>(InMemoryPushOutbox);
        services.TryAddSingleton<Idempotency.IdempotencyStore>(sp => new Idempotency.InMemoryIdempotencyStore(
            sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Idempotency, sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IPushSecretProtector, EphemeralPushSecretProtector>();
        services.TryAddSingleton<IPrincipalResolver, DefaultPrincipalResolver>();
        services.TryAddSingleton<A2AAuthorizationHandler, AllowAllAuthorizationHandler>();
        services.TryAddSingleton<TaskEventBus>();
        services.AddHttpClient(PushDeliveryService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => PushUrlValidator.CreateHandler(sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.PushNotifications));
        services.TryAddSingleton<PushDeliveryService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PushDeliveryService>(sp => sp.GetRequiredService<PushDeliveryService>()));
        services.TryAddSingleton<A2APushAdmin>();
        services.TryAddSingleton<A2ARequestHandler>();
        return new A2AServerBuilder(services);
    }

    internal static Storage.InMemoryEventStore InMemoryEventStore(IServiceProvider sp) =>
        new(sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Streaming, sp.GetService<TimeProvider>());

    internal static Push.InMemoryPushOutbox InMemoryPushOutbox(IServiceProvider sp) => new(sp.GetService<TimeProvider>());

    /// <summary>
    /// The in-memory task store commits events and push deliveries to the registered event store and push outbox, so those
    /// must be the in-memory ones too.
    /// </summary>
    internal static Storage.InMemoryTaskStore InMemoryTaskStore(IServiceProvider sp) =>
        new(sp.GetRequiredService<Storage.EventStore>() as Storage.InMemoryEventStore
            ?? throw new InvalidOperationException(
                "The in-memory TaskStore writes events to the in-memory EventStore, but another EventStore is registered. " +
                "Register a TaskStore that commits to your EventStore (the same provider's task store)."),
            sp.GetRequiredService<PushOutbox>() as Push.InMemoryPushOutbox
            ?? throw new InvalidOperationException(
                "The in-memory TaskStore writes push deliveries to the in-memory PushOutbox, but another PushOutbox is registered. " +
                "Register a TaskStore that commits to your PushOutbox (the same provider's task store)."));
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
    /// Uses the in-memory task, push config, event, push outbox and idempotency stores. They are already registered by default, so this changes nothing
    /// unless you registered your own stores earlier (it does not replace them).
    /// </summary>
    public A2AServerBuilder AddInMemoryStores()
    {
        Services.TryAddSingleton<Storage.TaskStore>(A2AServerServiceCollectionExtensions.InMemoryTaskStore);
        Services.TryAddSingleton<Storage.PushConfigStore, Storage.InMemoryPushConfigStore>();
        Services.TryAddSingleton<Storage.EventStore>(A2AServerServiceCollectionExtensions.InMemoryEventStore);
        Services.TryAddSingleton<PushOutbox>(A2AServerServiceCollectionExtensions.InMemoryPushOutbox);
        Services.TryAddSingleton<Idempotency.IdempotencyStore>(sp => new Idempotency.InMemoryIdempotencyStore(
            sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Idempotency, sp.GetService<TimeProvider>()));
        return this;
    }
}
