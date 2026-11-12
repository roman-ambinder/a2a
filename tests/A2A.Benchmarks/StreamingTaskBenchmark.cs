using A2A.Client;
using A2A.Server;
using BenchmarkDotNet.Attributes;

namespace A2A.Benchmarks;

/// <summary>A streaming task whose agent emits 1,000 artifact-append events; the client reads the whole SSE stream.</summary>
[MemoryDiagnoser]
public class StreamingTaskBenchmark
{
    private const int Events = 1000;
    private BenchHost _host = null!;

    // The task has 1,002 events (the snapshot, WORKING, 999 artifact updates, COMPLETED). A stream that falls behind fills the
    // gap from the replay buffer. With the default buffer (1,000) the first events can be gone by then, and a stream that stalled
    // right after its first event gets a snapshot of the finished task and ends after 2 events: the run would measure that, and
    // fail. A buffer larger than the task keeps every event, so a slow runner cannot change what is measured.
    [GlobalSetup]
    public async Task Setup() =>
        _host = await BenchHost.StartAsync<ManyEventsAgent>(o => o.Streaming.ReplayBufferSize = Events * 2);

    [GlobalCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    [Benchmark]
    public async Task<int> ThousandEvents()
    {
        var count = 0;
        await foreach (var _ in _host.Client.SendStreamingMessageAsync(Message.FromUser("go"))) count++;
        if (count < Events) throw new InvalidOperationException($"Expected at least {Events} events, got {count}.");
        return count;
    }

    private sealed class ManyEventsAgent : IAgentExecutor
    {
        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            await updater.StartWorkAsync(cancellationToken: ct);
            var id = await updater.AddArtifactAsync([Part.FromText("chunk")], name: "out", cancellationToken: ct);
            for (var i = 0; i < Events - 2; i++)
                await updater.AddArtifactAsync([Part.FromText("chunk")], artifactId: id, append: true, cancellationToken: ct);
            await updater.CompleteAsync("done", ct);
        }
    }
}
