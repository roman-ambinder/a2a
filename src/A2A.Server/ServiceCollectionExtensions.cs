using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
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
        services.TryAddSingleton(sp => new TaskEventBus(sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Streaming));
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
        // The registered sender hears config changes only if it implements the listener; resolving it never builds the HTTP sender
        // when a host has replaced IPushNotificationSender.
        services.TryAddSingleton<IPushConfigChangeListener>(sp =>
            sp.GetRequiredService<IPushNotificationSender>() as IPushConfigChangeListener ?? NoOpPushConfigChangeListener.Instance);
        services.TryAddSingleton<Pipeline.CallValidator>();
        services.TryAddSingleton<PushConfigService>();
        services.TryAddSingleton<Idempotency.IdempotencyCoordinator>();
        services.TryAddSingleton<Execution.TaskExecutionEngine>();
        services.TryAddSingleton<Streaming.TaskStreamer>();
        services.TryAddSingleton<A2ARequestHandler>();
        services.TryAddSingleton<IA2ARequestHandler>(sp => sp.GetRequiredService<A2ARequestHandler>());
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
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<A2AServerOptions>, Pipeline.ExtensionCardEntries>());
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
