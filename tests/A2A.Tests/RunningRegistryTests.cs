using A2A.Server;

namespace A2A.Tests;

/// <summary>The registry CancelTask and the blocking waits use to find the executions running for a task.</summary>
public class RunningRegistryTests
{
    /// <summary>
    /// Executions of one task start and finish concurrently. An execution that was added and not yet removed must always be
    /// visible: a remove that empties the set must never detach a set another add just put an execution into.
    /// </summary>
    [Fact]
    public async Task An_added_item_stays_visible_until_removed_under_concurrent_add_and_remove()
    {
        var registry = new RunningRegistry<object>();
        var missed = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        var workers = Enumerable.Range(0, Math.Max(4, Environment.ProcessorCount)).Select(_ => Task.Run(() =>
        {
            while (DateTime.UtcNow < deadline && Volatile.Read(ref missed) == 0)
            {
                for (var i = 0; i < 1000; i++)
                {
                    var item = new object();
                    registry.Add("task", item);
                    if (!registry.Snapshot("task").Contains(item)) Interlocked.Increment(ref missed);
                    registry.Remove("task", item);
                }
            }
        })).ToArray();
        await Task.WhenAll(workers);

        Assert.Equal(0, missed);
        Assert.Empty(registry.Snapshot("task"));
        Assert.Empty(registry.All());
    }

    [Fact]
    public void Scopes_are_independent()
    {
        var registry = new RunningRegistry<string>();
        registry.Add("a", "x");
        registry.Add("b", "y");
        registry.Remove("a", "x");

        Assert.Empty(registry.Snapshot("a"));
        Assert.Equal(["y"], registry.Snapshot("b"));
        Assert.Equal(["y"], registry.All());
    }
}
