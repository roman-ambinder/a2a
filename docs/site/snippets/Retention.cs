using A2A.Server;
using A2A.Server.Retention;
using A2A.Server.Storage;

namespace A2A.DocSnippets.Retention;

public static class Samples
{
    public static void Register(IServiceCollection services)
    {
        #region Register
        services.AddA2AServer(o => o.InterruptedTimeout = TimeSpan.FromHours(6));
        services.AddA2ARetention(o =>
        {
            o.TaskRetention = TimeSpan.FromDays(30);
            o.OrphanContextRetention = TimeSpan.FromDays(30);
            o.SweepInterval = TimeSpan.FromMinutes(10);
            o.BatchSize = 200;
        });
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
}
