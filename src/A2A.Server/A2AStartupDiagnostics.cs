using A2A.Server.Coordination;
using A2A.Server.Idempotency;
using A2A.Server.Push;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace A2A.Server;

/// <summary>
/// Logs, once at startup, the mixes of durable and process-local parts that work on one instance but break across restarts or
/// several instances, a shared event bus over in-memory stores, a durable task store without the (opt-in) retention sweep, and per-tenant
/// agents behind the default allow-all authorization handler. Never throws: a host that does not use the feature in question must still start.
/// </summary>
internal sealed class A2AStartupDiagnostics(IServiceProvider services, ILogger<A2AStartupDiagnostics> logger) : IHostedService
{
    internal const string EphemeralProtectorMessage =
        "Push secrets are protected with a per-process key (EphemeralPushSecretProtector), but the push outbox is durable ({Outbox}). " +
        "Push configs with a token or credentials will be dead-lettered after a restart or when another instance sends them, " +
        "because their secrets cannot be read there. Register a persistent IPushSecretProtector: for example " +
        "services.AddDataProtection() with a key ring persisted and shared by every instance, then services.AddA2ADataProtectionPushSecrets() " +
        "(A2A.Server.AspNetCore).";

    internal const string LocalCoordinatorMessage =
        "The task store is durable ({TaskStore}), but the execution coordinator is the process-local InMemoryExecutionCoordinator. " +
        "Its leases die with the process: when the instance crashes, the tasks it was running stay unrecovered (WORKING, never failed by " +
        "the retention sweep). With several instances, CancelTask, running checks and dead-execution recovery also stay on the instance that runs the agent. " +
        "Register a durable coordinator: AddPostgresExecutionCoordinator (A2A.Server.Postgres, the default of AddPostgresStores) or " +
        "AddRedisEventBus / AddRedisExecutionCoordinator (A2A.Server.Redis).";

    internal const string RetentionOffMessage =
        "The task store is durable ({TaskStore}), but the retention sweep is off (it is opt-in). Without it, terminal tasks and " +
        "their data are never deleted, tasks waiting for input never time out (InterruptedTimeout does nothing), and tasks whose " +
        "instance died while running stay WORKING. Call services.AddA2ARetention() to turn it on; ignore this if you clean up yourself.";

    internal const string UploadsWithoutRetentionMessage =
        "Inbound file uploads are on (ArtifactStorage.Uploads.Enabled), but the retention sweep is off (it is opt-in). Nothing purges the uploads " +
        "that no message ever used, so they stay in the object store for good. Call services.AddA2ARetention() to turn it on.";

    internal const string InMemoryObjectsMessage =
        "The task store is durable ({TaskStore}), but artifact objects are kept by the process-local InMemoryArtifactObjectStore. " +
        "Objects are lost on restart and other instances cannot read them, so stored tasks keep links that no longer download. " +
        "Register a durable ArtifactObjectStore; use the in-memory one for tests and development only.";

    internal const string SharedBusMessage =
        "The event bus ({Bus}) spans server instances, but these stores are kept in this process: {InMemoryStores}. " +
        "Events cross instances, but the other instances cannot read the tasks and events kept here: a stream on another instance " +
        "gets task-not-found or cannot fill gaps in the events. " +
        "Use shared stores: AddPostgresStores (A2A.Server.Postgres) or your own durable stores.";

    internal const string TenantsWithoutAuthorizationMessage =
        "Agents are set up per tenant (a TenantCardProvider or an AgentExecutorResolver), but the authorization handler is the default " +
        "AllowAllAuthorizationHandler. It lets every caller reach every tenant's agent, and on a route without a tenant segment a " +
        "'tenant' field in the request body can select another tenant. Register an A2AAuthorizationHandler " +
        "(AddAuthorizationHandler<T>()) that checks call.Tenant against the caller. Ignore this if your route policies already check the tenant.";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Not inside the try: an object store without a link base is a configuration error, and the host must not start with it.
        RequireLinkBase();
        try
        {
            Check();
        }
        catch (Exception ex)
        {
            // The registrations themselves are broken; the request handler reports that when it is first used.
            ServerLog.StartupDiagnosticsSkipped(logger, ex);
        }
        return Task.CompletedTask;
    }

    private void RequireLinkBase()
    {
        if (services.GetService<ArtifactObjectStore>() is null) return;
        var storage = services.GetService<A2AServerOptions>()?.ArtifactStorage;
        if (storage is { PublicBaseUrl: null, PublicBaseUrlResolver: null })
            throw new InvalidOperationException("An ArtifactObjectStore is registered, but A2AServerOptions.ArtifactStorage.PublicBaseUrl (or PublicBaseUrlResolver) is not set. Artifact links need a base URL.");
    }

    private void Check()
    {
        var outbox = services.GetService<PushOutbox>();
        if (outbox is not null and not InMemoryPushOutbox && services.GetService<IPushSecretProtector>() is EphemeralPushSecretProtector)
            ServerLog.EphemeralProtectorWithDurableOutbox(logger, outbox.GetType().Name);

        var tasks = services.GetService<TaskStore>();

        // TaskEventBus is the local default; any other bus spans instances (like the Redis one).
        var bus = services.GetService<TaskEventBus>();
        if (bus is not null && bus.GetType() != typeof(TaskEventBus))
        {
            var inMemory = new List<string>();
            if (tasks is InMemoryTaskStore) inMemory.Add(nameof(InMemoryTaskStore));
            if (services.GetService<EventStore>() is InMemoryEventStore) inMemory.Add(nameof(InMemoryEventStore));
            if (services.GetService<IdempotencyStore>() is InMemoryIdempotencyStore) inMemory.Add(nameof(InMemoryIdempotencyStore));
            if (inMemory.Count > 0) ServerLog.SharedBusWithInMemoryStores(logger, bus.GetType().Name, string.Join(", ", inMemory));
        }
        if (tasks is not null and not InMemoryTaskStore && services.GetService<ExecutionCoordinator>() is InMemoryExecutionCoordinator)
            ServerLog.LocalCoordinatorWithDurableStore(logger, tasks.GetType().Name);

        if (tasks is not null and not InMemoryTaskStore && services.GetService<Retention.A2ARetentionService>() is null)
            ServerLog.RetentionOffWithDurableStore(logger, tasks.GetType().Name);

        if (services.GetService<A2AServerOptions>()?.ArtifactStorage.Uploads.Enabled == true && services.GetService<Retention.A2ARetentionService>() is null)
            ServerLog.UploadsWithoutRetention(logger);

        if (tasks is not null and not InMemoryTaskStore && services.GetService<ArtifactObjectStore>() is InMemoryArtifactObjectStore)
            ServerLog.InMemoryObjectsWithDurableStore(logger, tasks.GetType().Name);

        // Not RequestCards.IsPerTenant: a card provider that does not build on AgentCardProvider is not seen there.
        // No resolver is registered by default, so any resolver is the host's.
        if (services.GetService<Authorization.A2AAuthorizationHandler>() is Authorization.AllowAllAuthorizationHandler
            && (services.GetService<Agents.TenantCardProvider>() is not null || services.GetService<Agents.AgentExecutorResolver>() is not null))
            ServerLog.TenantsWithoutAuthorization(logger);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
