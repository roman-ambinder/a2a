using A2A.Server;

namespace A2A.DocSnippets.ExecutionLimits;

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

    private sealed record LlmResult(string Text, long TotalTokens, decimal Cost);

    private static Task<LlmResult> CallModelAsync(CancellationToken ct) => Task.FromResult(new LlmResult("Done.", 1200, 0.03m));

    public static async Task Report(TaskUpdater updater, CancellationToken ct)
    {
        #region Report
        var result = await CallModelAsync(ct);
        await updater.ReportUsageAsync(result.TotalTokens, result.Cost, ct);
        await updater.CompleteAsync(result.Text, ct);
        #endregion
    }
}
