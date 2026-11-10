using A2A.Server;
using A2A.Server.Audit;
using A2A.Server.Postgres;
using A2A.Server.Push;
using A2A.Server.Retention;
using A2A.Server.Storage;

namespace Snippets.Audit;

public sealed class MyAgent : IAgentExecutor
{
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
        updater.ReplyAsync("ok", cancellationToken).AsTask();
}

#region CustomSink
/// <summary>Sends every event to your own store. Append only.</summary>
public sealed class MyAuditSink : A2AAuditSink
{
    public override ValueTask WriteAsync(A2AAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        Console.WriteLine($"{auditEvent.Time:O} {auditEvent.Actor} {auditEvent.Action} {auditEvent.Outcome} {auditEvent.TaskId}");
        return ValueTask.CompletedTask;
    }
}
#endregion

public static class Samples
{
    public static void Postgres(IServiceCollection services, AgentCard card, string postgres)
    {
        #region PostgresSink
        services.AddA2AServer(o => o.AgentCard = card)
            .AddAgentExecutor<MyAgent>()
            .AddPostgresStores(postgres)
            .AddPostgresAuditSink(postgres);
        #endregion
    }

    public static void Custom(IServiceCollection services, AgentCard card)
    {
        #region FailClosed
        services.AddA2AServer(o =>
            {
                o.AgentCard = card;
                o.Audit.FailClosed = true;                       // no change without a record
                o.Audit.WriteTimeout = TimeSpan.FromSeconds(2);  // the default
            })
            .AddAgentExecutor<MyAgent>()
            .AddAuditSink<MyAuditSink>();
        #endregion
    }

    public static async Task Admin(A2ATaskAdmin tasks, A2APushAdmin push, TaskKey key, PushDelivery dead)
    {
        #region AdminActor
        await tasks.DeleteTaskAsync(key, actor: "ops-bob");
        var letters = await push.ListDeadLettersAsync(key.Tenant, 50, actor: "ops-bob");
        await push.RedriveAsync(dead, actor: "ops-bob");
        #endregion
        _ = letters;
    }
}
