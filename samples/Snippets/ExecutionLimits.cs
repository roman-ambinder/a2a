using A2A.Server;

namespace Snippets.ExecutionLimits;

internal static class Samples
{
    public static void Concurrency()
    {
        var services = new ServiceCollection();
        #region Concurrency
        services.AddA2AServer(o =>
        {
            o.Limits.MaxConcurrentExecutions = 50;
            o.Limits.MaxConcurrentExecutionsPerTenant = 10;
            o.Limits.MaxQueuedExecutions = 100;
            o.Limits.MaxExecutionQueueWait = TimeSpan.FromSeconds(5);
        });
        #endregion
    }

    public static void Budget()
    {
        var services = new ServiceCollection();
        #region Budget
        services.AddA2AServer(o =>
        {
            o.Limits.MaxTaskTokens = 200_000;
            o.Limits.MaxTaskCostUnits = 5.00m;
        });
        #endregion
    }
}
