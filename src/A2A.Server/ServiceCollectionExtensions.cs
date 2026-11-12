using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using A2A.Delegation;
using A2A.Server.Agents;
using A2A.Server.Audit;
using A2A.Server.Authorization;
using A2A.Server.Coordination;
using A2A.Server.Execution;
using A2A.Server.Idempotency;
using A2A.Server.Pipeline;
using A2A.Server.Push;
using A2A.Server.Retention;
using A2A.Server.Storage;
using A2A.Server.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Server;

/// <summary>Dependency injection extensions for the A2A server core.</summary>
public static class A2AServerServiceCollectionExtensions
{
    /// <summary>
    ///     The server core alone, for hosts without ASP.NET Core; an ASP.NET Core app calls <c>AddA2A</c> instead, which
    ///     calls this.     ///     Registers the A2A server core and returns a builder to add the agent, its card and extra services.
    ///     Defaults: in-memory stores (tasks, events, push outbox, push configs, idempotency), a hosted
    ///     <see cref="PushDeliveryService" />
    ///     that sends push notifications through an HTTP client with the SSRF guard, the <see cref="A2APushAdmin" />
    ///     dead-letter API,
    ///     the <see cref="Retention.A2ATaskAdmin" /> task delete API, the audit trail (<see cref="Audit.A2AAuditSink" />,
    ///     logging by default), an allow-all authorization handler, a process-local
    ///     <see cref="Coordination.ExecutionCoordinator" />,
    ///     and a single agent for every tenant (the configured card and executor; see
    ///     <see cref="A2AServerBuilder.AddTenantCardProvider{T}" />
    ///     and <see cref="A2AServerBuilder.AddAgentExecutorResolver{T}" /> for one agent per tenant). Register your
    ///     own <see cref="Storage.TaskStore" /> (with the <see cref="Storage.EventStore" /> and <see cref="PushOutbox" /> it
    ///     commits
    ///     to), <see cref="Storage.PushConfigStore" />, <see cref="Idempotency.IdempotencyStore" /> or
    ///     <see cref="Coordination.ExecutionCoordinator" /> before or after to replace them. At startup it logs an error when
    ///     the push
    ///     outbox is durable but push secrets use the per-process <see cref="EphemeralPushSecretProtector" />, and a warning
    ///     when the
    ///     task store is durable but the coordinator is the process-local one.
    /// </summary>
    public static A2AServerBuilder AddA2AServer(this IServiceCollection services,
        Action<A2AServerOptions>? configure = null)
    {
        var options = services.AddOptions<A2AServerOptions>();
        if (configure is not null) options.Configure(configure);
        // The security event log goes to the host's logging, when there is any.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<A2AServerOptions>, ConfigureOptions<A2AServerOptions>>(sp =>
                new ConfigureOptions<A2AServerOptions>(o =>
                    o.SecurityLogger ??= sp.GetService<ILoggerFactory>()?.CreateLogger(SecurityEventLog.Category))));
        services.TryAddSingleton<TaskStore>(InMemoryTaskStore);
        services.TryAddSingleton<PushConfigStore, InMemoryPushConfigStore>();
        services.TryAddSingleton<EventStore>(InMemoryEventStore);
        services.TryAddSingleton<PushOutbox>(InMemoryPushOutbox);
        services.TryAddSingleton<IdempotencyStore>(sp => new InMemoryIdempotencyStore(
            sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Idempotency, sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IPushSecretProtector, EphemeralPushSecretProtector>();
        services.TryAddSingleton<IPrincipalResolver, DefaultPrincipalResolver>();
        services.TryAddSingleton<A2AAuthorizationHandler, AllowAllAuthorizationHandler>();
        services.TryAddSingleton(sp =>
            new TaskEventBus(sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Streaming));
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<A2AServerOptions>>().Value);
        services.TryAddSingleton<IAgentExecutorFactory>(sp => new ScopedAgentExecutorFactory(
            sp.GetRequiredService<IServiceScopeFactory>(), sp.GetService<AgentExecutorResolver>()));
        services.TryAddSingleton<IAgentCardProvider>(sp => new AgentCardProvider(
            sp.GetRequiredService<A2AServerOptions>(),
            sp.GetServices<A2AExtensionOperation>(), sp.GetService<TenantCardProvider>()));
        services.TryAddSingleton<ExecutionCoordinator>(sp => new InMemoryExecutionCoordinator(
            sp.GetRequiredService<A2AServerOptions>().Coordination, sp.GetService<TimeProvider>()));
        services.TryAddKeyedSingleton<HttpMessageInvoker>(PushDeliveryService.InvokerServiceKey, (sp, _) =>
            new HttpMessageInvoker(
                PushUrlValidator.CreateHandler(sp.GetRequiredService<A2AServerOptions>().PushNotifications), true));
        services.TryAddSingleton(sp => new PushDeliveryService(
            sp.GetRequiredService<PushOutbox>(), sp.GetRequiredService<PushConfigStore>(),
            sp.GetRequiredService<IPushSecretProtector>(),
            sp.GetRequiredKeyedService<HttpMessageInvoker>(PushDeliveryService.InvokerServiceKey),
            sp.GetRequiredService<A2AServerOptions>(),
            sp.GetService<ILogger<PushDeliveryService>>(), sp.GetService<TimeProvider>()));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, PushDeliveryService>(sp =>
                sp.GetRequiredService<PushDeliveryService>()));
        services.TryAddSingleton<A2AAuditSink, LoggerAuditSink>();
        services.TryAddSingleton<A2AAuditor>();
        services.TryAddSingleton<A2APushAdmin>();
        services.TryAddSingleton<A2ATaskAdmin>();
        services.TryAddSingleton<CallValidator>();
        services.TryAddSingleton<PushConfigService>();
        services.TryAddSingleton<IdempotencyCoordinator>();
        services.TryAddSingleton<TaskExecutionEngine>();
        services.TryAddSingleton<A2ARequestHandler>();
        services.TryAddSingleton<IA2ARequestHandler>(sp => sp.GetRequiredService<A2ARequestHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, A2AStartupDiagnostics>());
        // Registered after the push delivery service, so on stop it drains first (hosted services stop in reverse order).
        services.TryAddSingleton(sp => new TaskExecutionDrainService(
            sp.GetRequiredService<TaskExecutionEngine>(), sp.GetRequiredService<A2AServerOptions>(),
            sp.GetService<ILogger<TaskExecutionDrainService>>(), sp.GetService<TimeProvider>()));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, TaskExecutionDrainService>(sp =>
                sp.GetRequiredService<TaskExecutionDrainService>()));
        return new A2AServerBuilder(services);
    }

    /// <summary>
    ///     Adds the retention sweep (<see cref="Retention.A2ARetentionService" />) as a hosted service: deletes expired
    ///     terminal
    ///     tasks with their data and orphan contexts, cancels tasks that waited longer than
    ///     <see cref="A2AServerOptions.InterruptedTimeout" /> for input, and fails tasks whose execution died with its
    ///     instance.
    ///     Without it none of this happens. Call it on every instance; one sweeps per interval. Options are checked when the
    ///     service starts. Calling it again only applies <paramref name="configure" /> as well. Public form:
    ///     <see cref="A2AServerBuilder.AddRetention" />.
    /// </summary>
    internal static IServiceCollection AddA2ARetention(this IServiceCollection services,
        Action<A2ARetentionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<A2ARetentionOptions>();
        if (configure is not null) options.Configure(configure);
        // A factory: the service has two public constructors, and the hooks only reach the one that takes them.
        services.TryAddSingleton(sp => A2ARetentionService.FromServices(sp));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, A2ARetentionService>(sp =>
                sp.GetRequiredService<A2ARetentionService>()));
        return services;
    }

    internal static InMemoryEventStore InMemoryEventStore(IServiceProvider sp)
    {
        return new InMemoryEventStore(sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Streaming,
            sp.GetService<TimeProvider>());
    }

    internal static InMemoryPushOutbox InMemoryPushOutbox(IServiceProvider sp)
    {
        return new InMemoryPushOutbox(sp.GetService<TimeProvider>());
    }

    /// <summary>
    ///     The in-memory task store commits events and push deliveries to the registered event store and push outbox, so those
    ///     must be the in-memory ones too.
    /// </summary>
    internal static InMemoryTaskStore InMemoryTaskStore(IServiceProvider sp)
    {
        return new InMemoryTaskStore(sp.GetRequiredService<EventStore>() as InMemoryEventStore
                                     ?? throw new InvalidOperationException(
                                         "The in-memory TaskStore writes events to the in-memory EventStore, but another EventStore is registered. " +
                                         "Register a TaskStore that commits to your EventStore (the same provider's task store)."),
            sp.GetRequiredService<PushOutbox>() as InMemoryPushOutbox
            ?? throw new InvalidOperationException(
                "The in-memory TaskStore writes push deliveries to the in-memory PushOutbox, but another PushOutbox is registered. " +
                "Register a TaskStore that commits to your PushOutbox (the same provider's task store)."));
    }
}

/// <summary>Fluent setup for the A2A server. Created by <c>AddA2AServer</c>.</summary>
public sealed class A2AServerBuilder
{
    internal A2AServerBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>The underlying service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>
    ///     Configures <see cref="A2AServerOptions" />: limits, streaming, push, idempotency, anonymous access and the rest.
    ///     Can be called more than once; the calls run in order, after settings read from configuration.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="configure" /> is null.</exception>
    public A2AServerBuilder Configure(Action<A2AServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure(configure);
        return this;
    }

    /// <summary>
    ///     Adds the retention sweep (<see cref="Retention.A2ARetentionService" />) as a hosted service: deletes expired
    ///     terminal tasks with their data and orphan contexts, cancels tasks that waited longer than
    ///     <see cref="A2AServerOptions.InterruptedTimeout" /> for input, and fails tasks whose execution died with its
    ///     instance. Without it none of this happens. Call it on every instance; one sweeps per interval. Options are checked
    ///     when the service starts. Calling it again only applies <paramref name="configure" /> as well.
    /// </summary>
    public A2AServerBuilder AddRetention(Action<A2ARetentionOptions>? configure = null)
    {
        Services.AddA2ARetention(configure);
        return this;
    }

    /// <summary>Uses <typeparamref name="T" /> as the agent. Scoped: one instance per execution. Replaces an earlier executor.</summary>
    public A2AServerBuilder AddAgentExecutor<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class, IAgentExecutor
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

    /// <summary>Sets the public Agent Card (same as setting <see cref="A2AServerOptions.AgentCard" />).</summary>
    public A2AServerBuilder AddAgentCard(AgentCard card)
    {
        Services.Configure<A2AServerOptions>(o => o.AgentCard = card);
        return this;
    }

    /// <summary>
    ///     Uses <typeparamref name="T" /> to supply each tenant's Agent Card (multi-agent hosting: one agent per tenant).
    ///     Singleton.
    ///     Replaces an earlier provider. The default <see cref="IAgentCardProvider" /> completes the cards it returns. Without
    ///     one,
    ///     <see cref="A2AServerOptions.AgentCard" /> serves every tenant.
    /// </summary>
    public A2AServerBuilder AddTenantCardProvider<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : TenantCardProvider
    {
        Services.Replace(ServiceDescriptor.Singleton<TenantCardProvider, T>());
        return this;
    }

    /// <summary>
    ///     Uses <typeparamref name="T" /> to pick each tenant's agent (multi-agent hosting: one agent per tenant). Singleton.
    ///     Replaces an earlier resolver. Without one, the executor from <see cref="AddAgentExecutor{T}" /> runs every tenant's
    ///     tasks.
    /// </summary>
    public A2AServerBuilder AddAgentExecutorResolver<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : AgentExecutorResolver
    {
        Services.Replace(ServiceDescriptor.Singleton<AgentExecutorResolver, T>());
        return this;
    }

    /// <summary>
    ///     Uses <typeparamref name="T" /> to exchange the caller's token for a token for another agent (see
    ///     <see cref="AgentRequestContext.ExchangeTokenAsync" />). Singleton. Replaces an earlier exchanger. With one
    ///     registered,
    ///     the server keeps the caller's bearer token in memory only, while the agent run lasts. The run can outlive the HTTP
    ///     request, and the token can expire before the run ends.
    /// </summary>
    /// <remarks>
    ///     Warning: the SDK sends the caller's <c>Authorization: Bearer</c> value to the exchanger whenever any scheme
    ///     authenticated the caller. Register an exchanger only when a Bearer handler validates that header on these
    ///     endpoints.
    ///     On a host that authenticates with cookies, API keys or mTLS, that header is unchecked.
    /// </remarks>
    public A2AServerBuilder AddTokenExchanger<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class, ITokenExchanger
    {
        Services.Replace(ServiceDescriptor.Singleton<ITokenExchanger, T>());
        return this;
    }

    /// <summary>
    ///     Uses <paramref name="exchanger" /> to exchange tokens. Replaces an earlier exchanger. See
    ///     <see cref="AddTokenExchanger{T}" />.
    /// </summary>
    /// <remarks>
    ///     Warning: the SDK sends the caller's <c>Authorization: Bearer</c> value to the exchanger whenever any scheme
    ///     authenticated the caller. Register an exchanger only when a Bearer handler validates that header on these
    ///     endpoints.
    ///     On a host that authenticates with cookies, API keys or mTLS, that header is unchecked.
    /// </remarks>
    public A2AServerBuilder AddTokenExchanger(ITokenExchanger exchanger)
    {
        ArgumentNullException.ThrowIfNull(exchanger);
        Services.Replace(ServiceDescriptor.Singleton(exchanger));
        return this;
    }

    /// <summary>Uses <typeparamref name="T" /> to authorize every operation. Replaces an earlier handler.</summary>
    public A2AServerBuilder AddAuthorizationHandler<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : A2AAuthorizationHandler
    {
        Services.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler, T>());
        return this;
    }

    /// <summary>
    ///     Uses <typeparamref name="T" /> as the audit sink (see <see cref="Audit.A2AAuditSink" />). Singleton. Replaces an
    ///     earlier sink,
    ///     including the default <see cref="Audit.LoggerAuditSink" />.
    /// </summary>
    public A2AServerBuilder AddAuditSink<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : A2AAuditSink
    {
        Services.Replace(ServiceDescriptor.Singleton<A2AAuditSink, T>());
        return this;
    }

    /// <summary>Uses <paramref name="sink" /> as the audit sink. Replaces an earlier sink. See <see cref="AddAuditSink{T}" />.</summary>
    public A2AServerBuilder AddAuditSink(A2AAuditSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        Services.Replace(ServiceDescriptor.Singleton(sink));
        return this;
    }

    /// <summary>
    ///     Adds <typeparamref name="T" /> to the operation pipeline (see <see cref="Pipeline.IA2AOperationFilter" />). Filters
    ///     run in
    ///     the order they are added, within their stage. <typeparamref name="T" /> is registered as a singleton unless it is
    ///     already
    ///     registered; one instance serves every call.
    /// </summary>
    /// <param name="stage">
    ///     <see cref="Pipeline.A2AFilterStage.AfterAuthorization" /> (the default): the filter only sees authorized calls.
    ///     <see cref="Pipeline.A2AFilterStage.BeforeAuthorization" />: the filter also sees unauthenticated and unauthorized
    ///     calls.
    /// </param>
    public A2AServerBuilder AddOperationFilter<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        A2AFilterStage stage = A2AFilterStage.AfterAuthorization) where T : class, IA2AOperationFilter
    {
        Services.TryAddSingleton<T>();
        Services.AddSingleton(sp => new A2AOperationFilterRegistration(sp.GetRequiredService<T>(), stage));
        return this;
    }

    /// <summary>
    ///     Adds an extension operation (see <see cref="Pipeline.A2AExtensionOperation" />): the JSON-RPC method
    ///     <paramref name="method" /> and the HTTP+JSON route <c>POST {pattern}/ext/{method}</c>. Calls run through the
    ///     operation
    ///     pipeline with <see cref="Pipeline.A2AOperationDescriptor.Extension" />. The public Agent Card lists
    ///     <paramref name="extensionUri" /> in its capabilities. A call must send <paramref name="extensionUri" /> in
    ///     <c>A2A-Extensions</c>; without it the call fails with
    ///     ExtensionSupportRequired. A host with its own <see cref="IAgentCardProvider" /> must list the URI in its card, or
    ///     the operation cannot be activated.
    ///     The gRPC binding serves them through <c>a2a.extensions.v1.A2AExtensionService</c> (<c>MapA2AGrpcExtensions</c>);
    ///     its streaming RPC is not served.
    /// </summary>
    /// <param name="method">The method name: letters, digits, <c>.</c>, <c>_</c> and <c>-</c>; not a built-in method name.</param>
    /// <param name="extensionUri">The absolute URI of the extension.</param>
    /// <param name="handler">Runs the operation with the pipeline context and the request params; returns the result.</param>
    /// <param name="authorizationPolicy">
    ///     An ASP.NET Core authorization policy the HTTP bindings evaluate first (see
    ///     <see cref="Pipeline.A2AExtensionOperation.AuthorizationPolicy" />).
    /// </param>
    /// <param name="allow">
    ///     True to let the default authorization handler allow the call (see
    ///     <see cref="Pipeline.A2AExtensionOperation.Allow" />).
    /// </param>
    /// <exception cref="ArgumentException">
    ///     The method name is not valid, is reserved (a built-in method, or a name that starts with <c>rpc.</c>), or is
    ///     already registered (compared ignoring case, as HTTP routes are);
    ///     or the extension URI is not absolute.
    /// </exception>
    public A2AServerBuilder AddExtensionOperation(string method, string extensionUri,
        Func<A2AOperationContext, JsonElement, CancellationToken, ValueTask<JsonElement>> handler,
        string? authorizationPolicy = null, bool allow = false)
    {
        A2AExtensionOperation.Validate(method, extensionUri, handler);
        foreach (var descriptor in Services)
            if (descriptor.ServiceType == typeof(A2AExtensionOperation) && !descriptor.IsKeyedService
                                                                        && descriptor.ImplementationInstance is
                                                                            A2AExtensionOperation existing
                                                                        && string.Equals(existing.Method, method,
                                                                            StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"An extension operation named '{existing.Method}' is already registered.",
                    nameof(method));
        if (string.Equals(method, Deletion.TaskDeletion.Method, StringComparison.OrdinalIgnoreCase)
            && Services.Any(d => d.ServiceType == typeof(TaskDeletionOperation)))
            throw new ArgumentException(
                $"An extension operation named '{Deletion.TaskDeletion.Method}' is already registered (AddTaskDeletion).",
                nameof(method));
        Services.AddSingleton(new A2AExtensionOperation(method, extensionUri, handler, authorizationPolicy, allow));
        return this;
    }

    /// <summary>
    ///     Adds an extension operation with typed params and result (see
    ///     <see
    ///         cref="AddExtensionOperation(string, string, Func{Pipeline.A2AOperationContext, JsonElement, CancellationToken, ValueTask{JsonElement}}, string?, bool)" />
    ///     ).
    ///     The params are read with <paramref name="requestType" /> and the result is written with
    ///     <paramref name="resultType" />, so the
    ///     operation is safe for trimming and native AOT. Params that do not bind to <typeparamref name="TRequest" />, or JSON
    ///     <c>null</c>
    ///     (JSON-RPC reads <c>"params": null</c> as <c>{}</c>), fail with InvalidParams.
    /// </summary>
    /// <typeparam name="TRequest">The type of the request params.</typeparam>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    /// <param name="method">The method name: letters, digits, <c>.</c>, <c>_</c> and <c>-</c>; not a reserved name.</param>
    /// <param name="extensionUri">The absolute URI of the extension.</param>
    /// <param name="requestType">The JSON type info used to read the params.</param>
    /// <param name="resultType">The JSON type info used to write the result.</param>
    /// <param name="handler">Runs the operation with the pipeline context and the typed request; returns the typed result.</param>
    /// <param name="authorizationPolicy">An ASP.NET Core authorization policy the HTTP bindings evaluate first.</param>
    /// <param name="allow">True to let the default authorization handler allow the call.</param>
    /// <exception cref="ArgumentException">The method name or extension URI is not valid, or the method is already registered.</exception>
    public A2AServerBuilder AddExtensionOperation<TRequest, TResult>(string method, string extensionUri,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResult> resultType,
        Func<A2AOperationContext, TRequest, CancellationToken, ValueTask<TResult>> handler,
        string? authorizationPolicy = null, bool allow = false)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(resultType);
        ArgumentNullException.ThrowIfNull(handler);
        return AddExtensionOperation(method, extensionUri, async (context, parameters, ct) =>
        {
            TRequest? request;
            try
            {
                request = parameters.Deserialize(requestType);
            }
            catch (JsonException e)
            {
                throw new A2AException(A2AErrorCode.InvalidParams,
                    "The params do not match the operation's request type.", e);
            }

            if (request is null) throw new A2AException(A2AErrorCode.InvalidParams, "The params must not be null.");
            var result = await handler(context, request, ct).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(result, resultType);
        }, authorizationPolicy, allow);
    }

    /// <summary>
    ///     Lets callers delete their own tasks (spec §13.4): adds the SDK extension operation
    ///     <see cref="A2A.Deletion.TaskDeletion.Method" /> (<c>a2a.tasks.delete</c>) of the extension
    ///     <see cref="A2A.Deletion.TaskDeletion.ExtensionUri" />, which the public Agent Card then lists. Without this call
    ///     the operation does not exist. It is served like any extension operation (see
    ///     <see cref="AddExtensionOperation(string, string, Func{Pipeline.A2AOperationContext, JsonElement, CancellationToken, ValueTask{JsonElement}}, string?, bool)" />):
    ///     the caller must activate the extension, and on gRPC the extension service must be mapped.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A call names the task in the params member <c>id</c>. The task is the one of the call's tenant and principal,
    ///         so a caller can delete only its own: another caller's task, or a missing one, is TaskNotFound. The caller must
    ///         be authenticated, also with <see cref="A2AServerOptions.AllowAnonymous" />. A task that is not in a terminal
    ///         state is refused with UnsupportedOperation (cancel it first). The task is deleted as
    ///         <see cref="Retention.A2ATaskAdmin.DeleteTaskAsync(Storage.TaskKey, string, CancellationToken)" /> deletes it,
    ///         with its event log, push configs, pending push deliveries, dead letters and artifact objects, and the delete is
    ///         audited as <see cref="Audit.A2AAuditAction.TaskDeleted" />.
    ///     </para>
    ///     <para>
    ///         The default authorization handler allows the operation. A custom <see cref="A2AAuthorizationHandler" /> sees
    ///         <see cref="A2AOperation.Extension" /> with the name <c>a2a.tasks.delete</c> and must allow it; a denial is
    ///         TaskNotFound, as for the built-in task operations. Calling this more than once has no further effect.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException">Another extension operation is already registered as <c>a2a.tasks.delete</c>.</exception>
    public A2AServerBuilder AddTaskDeletion()
    {
        if (Services.Any(d => d.ServiceType == typeof(TaskDeletionOperation))) return this;
        foreach (var descriptor in Services)
            if (descriptor.ServiceType == typeof(A2AExtensionOperation) && !descriptor.IsKeyedService
                                                                        && descriptor.ImplementationInstance is
                                                                            A2AExtensionOperation existing
                                                                        && string.Equals(existing.Method,
                                                                            Deletion.TaskDeletion.Method,
                                                                            StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"An extension operation named '{existing.Method}' is already registered.");
        Services.AddSingleton(sp => new TaskDeletionOperation(sp));
        Services.AddSingleton(sp => sp.GetRequiredService<TaskDeletionOperation>().Create());
        return this;
    }

    /// <summary>
    ///     Uses the in-memory task, push config, event, push outbox and idempotency stores. They are already registered by
    ///     default, so this changes nothing
    ///     unless you registered your own stores earlier (it does not replace them).
    /// </summary>
    public A2AServerBuilder AddInMemoryStores()
    {
        Services.TryAddSingleton<TaskStore>(A2AServerServiceCollectionExtensions.InMemoryTaskStore);
        Services.TryAddSingleton<PushConfigStore, InMemoryPushConfigStore>();
        Services.TryAddSingleton<EventStore>(A2AServerServiceCollectionExtensions.InMemoryEventStore);
        Services.TryAddSingleton<PushOutbox>(A2AServerServiceCollectionExtensions.InMemoryPushOutbox);
        Services.TryAddSingleton<IdempotencyStore>(sp => new InMemoryIdempotencyStore(
            sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Idempotency, sp.GetService<TimeProvider>()));
        return this;
    }
}