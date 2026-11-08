using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using A2A.Server.Push;
using A2A.Server.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace A2A.Server;

/// <summary>Dependency injection extensions for the A2A server core.</summary>
public static class A2AServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the A2A server core and returns a builder to add the agent, its card and extra services.
    /// Defaults: in-memory stores (tasks, events, push outbox, push configs, idempotency), a hosted <see cref="PushDeliveryService"/>
    /// that sends push notifications through an HTTP client with the SSRF guard, the <see cref="A2APushAdmin"/> dead-letter API,
    /// the <see cref="Retention.A2ATaskAdmin"/> task delete API, an allow-all authorization handler, a process-local <see cref="Coordination.ExecutionCoordinator"/>,
    /// and a single agent for every tenant (the configured card and executor; see <see cref="A2AServerBuilder.AddTenantCardProvider{T}"/>
    /// and <see cref="A2AServerBuilder.AddAgentExecutorResolver{T}"/> for one agent per tenant). Register your
    /// own <see cref="Storage.TaskStore"/> (with the <see cref="Storage.EventStore"/> and <see cref="PushOutbox"/> it commits
    /// to), <see cref="Storage.PushConfigStore"/>, <see cref="Idempotency.IdempotencyStore"/> or
    /// <see cref="Coordination.ExecutionCoordinator"/> before or after to replace them. At startup it logs an error when the push
    /// outbox is durable but push secrets use the per-process <see cref="EphemeralPushSecretProtector"/>, and a warning when the
    /// task store is durable but the coordinator is the process-local one.
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
        services.TryAddSingleton(sp => new TaskEventBus(sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Streaming));
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<A2AServerOptions>>().Value);
        services.TryAddSingleton<IAgentExecutorFactory>(sp => new ScopedAgentExecutorFactory(
            sp.GetRequiredService<IServiceScopeFactory>(), sp.GetService<Agents.AgentExecutorResolver>()));
        services.TryAddSingleton<IAgentCardProvider>(sp => new AgentCardProvider(sp.GetRequiredService<A2AServerOptions>(),
            sp.GetServices<Pipeline.A2AExtensionOperation>(), sp.GetService<Agents.TenantCardProvider>()));
        services.TryAddSingleton<Coordination.ExecutionCoordinator>(sp => new Coordination.InMemoryExecutionCoordinator(
            sp.GetRequiredService<A2AServerOptions>().Coordination, sp.GetService<TimeProvider>()));
        services.TryAddKeyedSingleton<HttpMessageInvoker>(PushDeliveryService.InvokerServiceKey, (sp, _) => new HttpMessageInvoker(
            PushUrlValidator.CreateHandler(sp.GetRequiredService<A2AServerOptions>().PushNotifications), disposeHandler: true));
        services.TryAddSingleton(sp => new PushDeliveryService(
            sp.GetRequiredService<PushOutbox>(), sp.GetRequiredService<Storage.PushConfigStore>(), sp.GetRequiredService<IPushSecretProtector>(),
            sp.GetRequiredKeyedService<HttpMessageInvoker>(PushDeliveryService.InvokerServiceKey), sp.GetRequiredService<A2AServerOptions>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<PushDeliveryService>>(), sp.GetService<TimeProvider>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PushDeliveryService>(sp => sp.GetRequiredService<PushDeliveryService>()));
        services.TryAddSingleton<A2APushAdmin>();
        services.TryAddSingleton<Retention.A2ATaskAdmin>();
        services.TryAddSingleton<Pipeline.CallValidator>();
        services.TryAddSingleton<PushConfigService>();
        services.TryAddSingleton<Idempotency.IdempotencyCoordinator>();
        services.TryAddSingleton<Execution.TaskExecutionEngine>();
        services.TryAddSingleton<Streaming.TaskStreamer>();
        services.TryAddSingleton<A2ARequestHandler>();
        services.TryAddSingleton<IA2ARequestHandler>(sp => sp.GetRequiredService<A2ARequestHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, A2AStartupDiagnostics>());
        return new A2AServerBuilder(services);
    }

    /// <summary>
    /// Adds the retention sweep (<see cref="Retention.A2ARetentionService"/>) as a hosted service: deletes expired terminal
    /// tasks with their data and orphan contexts, cancels tasks that waited longer than
    /// <see cref="A2AServerOptions.InterruptedTimeout"/> for input, and fails tasks whose execution died with its instance.
    /// Without it none of this happens. Call it on every instance; one sweeps per interval. Options are checked when the
    /// service starts. Calling it again only applies <paramref name="configure"/> as well.
    /// </summary>
    public static IServiceCollection AddA2ARetention(this IServiceCollection services, Action<Retention.A2ARetentionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<Retention.A2ARetentionOptions>();
        if (configure is not null) options.Configure(configure);
        services.TryAddSingleton<Retention.A2ARetentionService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, Retention.A2ARetentionService>(sp => sp.GetRequiredService<Retention.A2ARetentionService>()));
        return services;
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

    /// <summary>
    /// Uses <typeparamref name="T"/> to supply each tenant's Agent Card (multi-agent hosting: one agent per tenant). Singleton.
    /// Replaces an earlier provider. The default <see cref="IAgentCardProvider"/> completes the cards it returns. Without one,
    /// <see cref="A2AServerOptions.AgentCard"/> serves every tenant.
    /// </summary>
    public A2AServerBuilder AddTenantCardProvider<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : Agents.TenantCardProvider
    {
        Services.Replace(ServiceDescriptor.Singleton<Agents.TenantCardProvider, T>());
        return this;
    }

    /// <summary>
    /// Uses <typeparamref name="T"/> to pick each tenant's agent (multi-agent hosting: one agent per tenant). Singleton.
    /// Replaces an earlier resolver. Without one, the executor from <see cref="AddAgentExecutor{T}"/> runs every tenant's tasks.
    /// </summary>
    public A2AServerBuilder AddAgentExecutorResolver<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : Agents.AgentExecutorResolver
    {
        Services.Replace(ServiceDescriptor.Singleton<Agents.AgentExecutorResolver, T>());
        return this;
    }

    /// <summary>Uses <typeparamref name="T"/> to authorize every operation. Replaces an earlier handler.</summary>
    public A2AServerBuilder AddAuthorizationHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : A2AAuthorizationHandler
    {
        Services.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler, T>());
        return this;
    }

    /// <summary>
    /// Adds <typeparamref name="T"/> to the operation pipeline (see <see cref="Pipeline.IA2AOperationFilter"/>). Filters run in
    /// the order they are added, within their stage. <typeparamref name="T"/> is registered as a singleton unless it is already
    /// registered; one instance serves every call.
    /// </summary>
    /// <param name="stage">
    /// <see cref="Pipeline.A2AFilterStage.AfterAuthorization"/> (the default): the filter only sees authorized calls.
    /// <see cref="Pipeline.A2AFilterStage.BeforeAuthorization"/>: the filter also sees unauthenticated and unauthorized calls.
    /// </param>
    public A2AServerBuilder AddOperationFilter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        Pipeline.A2AFilterStage stage = Pipeline.A2AFilterStage.AfterAuthorization) where T : class, Pipeline.IA2AOperationFilter
    {
        Services.TryAddSingleton<T>();
        Services.AddSingleton(sp => new Pipeline.A2AOperationFilterRegistration(sp.GetRequiredService<T>(), stage));
        return this;
    }

    /// <summary>
    /// Adds an extension operation (see <see cref="Pipeline.A2AExtensionOperation"/>): the JSON-RPC method
    /// <paramref name="method"/> and the HTTP+JSON route <c>POST {pattern}/ext/{method}</c>. Calls run through the operation
    /// pipeline with <see cref="Pipeline.A2AOperationDescriptor.Extension"/>. The public Agent Card lists
    /// <paramref name="extensionUri"/> in its capabilities. The gRPC binding does not serve extension operations.
    /// </summary>
    /// <param name="method">The method name: letters, digits, <c>.</c>, <c>_</c> and <c>-</c>; not a built-in method name.</param>
    /// <param name="extensionUri">The absolute URI of the extension.</param>
    /// <param name="handler">Runs the operation with the pipeline context and the request params; returns the result.</param>
    /// <param name="authorizationPolicy">An ASP.NET Core authorization policy the HTTP bindings evaluate first (see <see cref="Pipeline.A2AExtensionOperation.AuthorizationPolicy"/>).</param>
    /// <param name="allow">True to let the default authorization handler allow the call (see <see cref="Pipeline.A2AExtensionOperation.Allow"/>).</param>
    /// <exception cref="ArgumentException">
    /// The method name is not valid, is a built-in method, or is already registered (compared ignoring case, as HTTP routes are);
    /// or the extension URI is not absolute.
    /// </exception>
    public A2AServerBuilder AddExtensionOperation(string method, string extensionUri,
        Func<Pipeline.A2AOperationContext, JsonElement, CancellationToken, ValueTask<JsonElement>> handler,
        string? authorizationPolicy = null, bool allow = false)
    {
        Pipeline.A2AExtensionOperation.Validate(method, extensionUri, handler);
        foreach (var descriptor in Services)
        {
            if (descriptor.ServiceType == typeof(Pipeline.A2AExtensionOperation) && !descriptor.IsKeyedService
                && descriptor.ImplementationInstance is Pipeline.A2AExtensionOperation existing
                && string.Equals(existing.Method, method, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"An extension operation named '{existing.Method}' is already registered.", nameof(method));
        }
        Services.AddSingleton(new Pipeline.A2AExtensionOperation(method, extensionUri, handler, authorizationPolicy, allow));
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
