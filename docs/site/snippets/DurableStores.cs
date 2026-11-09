using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Postgres;
using A2A.Server.Redis;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.DocSnippets.DurableStores;

public sealed class MyAgent : IAgentExecutor
{
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
        updater.ReplyAsync("ok", cancellationToken).AsTask();
}

public static class Samples
{
    public static void Register(IServiceCollection services, AgentCard card, string postgres, string redis)
    {
        #region Register
        services.AddA2AServer(o => o.AgentCard = card)
            .AddAgentExecutor<MyAgent>()
            .AddPostgresStores(postgres)
            .AddRedisEventBus(redis);          // also registers the Redis execution coordinator
        services.AddA2ARetention();            // opt-in, see the retention guide
        services.AddDataProtection();          // persist and share the key ring across instances
        services.AddA2ADataProtectionPushSecrets();
        #endregion
    }

    public static void PostgresOptions(IServiceCollection services, string postgres)
    {
        #region PostgresOptions
        services.AddA2AServer()
            .AddPostgresStores(postgres, o =>
            {
                o.Schema = "a2a";
                o.CreateSchemaOnStartup = false;   // you apply PostgresSchema.ScriptFor("a2a") yourself
                o.IdempotencyClaimTimeout = TimeSpan.FromMinutes(5);
            });
        #endregion
    }

    public static void Coordination(IServiceCollection services, string postgres, string redis)
    {
        #region Coordination
        services.AddA2AServer(o =>
            {
                o.Coordination.LeaseTtl = TimeSpan.FromSeconds(45);
                o.Coordination.Heartbeat = TimeSpan.FromSeconds(10);   // LeaseTtl must be longer than 2 x Heartbeat
            })
            .AddPostgresStores(postgres)
            .AddRedisEventBus(redis, o => o.ChannelPrefix = "{a2a}:");   // one hash slot on Redis Cluster
        #endregion
    }

    public static void PostgresCoordinator(IServiceCollection services, string postgres)
    {
        #region PostgresCoordinator
        services.AddA2AServer()
            .AddPostgresStores(postgres, o =>
            {
                o.CancelPollInterval = TimeSpan.FromMilliseconds(500);   // delay of a CancelTask that crosses instances
                // o.RegisterCoordinator = false;                        // keep the in-memory or your own coordinator
            });
        // Or the coordinator alone, replacing any earlier one:
        // .AddPostgresExecutionCoordinator(postgres)
        #endregion
    }

    public static void CoordinatorOnly(IServiceCollection services, string redis)
    {
        #region CoordinatorOnly
        services.AddA2AServer()
            .AddRedisExecutionCoordinator(redis, o => o.ChannelPrefix = "{a2a}:");
        #endregion
    }
}
