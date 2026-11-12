using A2A.Server.AspNetCore;
using A2A.Server;
using A2A.Server.Retention;
using A2A.Server.Storage;
using A2A.Client;
using MyAgent = Snippets.Audit.MyAgent;

namespace Snippets.Retention;

public static class Samples
{
    public static void Register(IServiceCollection services)
    {
        #region Register
        services.AddA2A(a => a
            .Configure(o => o.InterruptedTimeout = TimeSpan.FromHours(6))
            .AddRetention(o =>
            {
                o.TaskRetention = TimeSpan.FromDays(30);
                o.OrphanContextRetention = TimeSpan.FromDays(30);
                o.SweepInterval = TimeSpan.FromMinutes(10);
                o.BatchSize = 200;
            }));
        #endregion
    }

    public static void MapAdmin(WebApplication app)
    {
        #region Admin
        app.MapDelete("/admin/tasks/{tenant}/{owner}/{taskId}",
                async (string tenant, string owner, string taskId, A2ATaskAdmin admin, CancellationToken ct) =>
                    await admin.DeleteTaskAsync(new TaskKey(tenant, owner, taskId), ct)
                        ? Results.NoContent()
                        : Results.NotFound())
            .RequireAuthorization("A2AAdmin");
        #endregion
    }

    public static void TaskDeletion(WebApplicationBuilder builder)
    {
        #region TaskDeletion
        builder.Services.AddA2AServer()
            .AddAgentExecutor<MyAgent>()
            .AddTaskDeletion();
        #endregion
    }

    public static async Task DeleteOwnTask(IA2AClient client, AgentTask task)
    {
        #region DeleteOwnTask
        await client.DeleteTaskAsync(task.Id);
        #endregion
    }
}
