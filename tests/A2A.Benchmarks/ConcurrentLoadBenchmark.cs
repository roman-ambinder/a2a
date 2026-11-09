using A2A.Server;
using BenchmarkDotNet.Attributes;

namespace A2A.Benchmarks;

/// <summary>
/// Many callers at once against one shared in-process server: 64 parallel SendMessage round trips, and 16 parallel
/// SendStreamingMessage streams of 100 events each. Results are per request and per stream. Time is noisy; allocations are the gate.
/// </summary>
[MemoryDiagnoser]
public class ConcurrentLoadBenchmark
{
    private const int MessageCallers = 64;
    private const int StreamCallers = 16;
    private const int EventsPerStream = 100;
    private BenchHost _messageHost = null!;
    private BenchHost _streamHost = null!;

    // The task has 102 events (snapshot, WORKING, 99 artifact updates, COMPLETED). A buffer larger than the task keeps every
    // event, so a slow runner cannot change what a stream receives (see StreamingTaskBenchmark).
    // The default limits fit this load: 16 streams from one caller are under MaxConcurrentStreamsPerPrincipal (50), each stream
    // has its own task (MaxConcurrentStreamsPerTask is 10 per task), and 64 agents are under MaxConcurrentExecutions (1024).
    [GlobalSetup]
    public async Task Setup()
    {
        _messageHost = await BenchHost.StartAsync<ReplyAgent>();
        _streamHost = await BenchHost.StartAsync<ManyEventsAgent>(o => o.Streaming.ReplayBufferSize = EventsPerStream * 10);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _messageHost.DisposeAsync();
        await _streamHost.DisposeAsync();
    }

    [Benchmark(OperationsPerInvoke = MessageCallers)]
    public async Task ConcurrentSendMessage()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = new Task<SendMessageResponse>[MessageCallers];
        for (var i = 0; i < callers.Length; i++)
            callers[i] = Task.Run(async () =>
            {
                await gate.Task;
                return await _messageHost.Client.SendMessageAsync(Message.FromUser("hello agent"));
            });
        gate.SetResult();
        await Task.WhenAll(callers);
    }

    [Benchmark(OperationsPerInvoke = StreamCallers)]
    public async Task ConcurrentStreams()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = new Task<int>[StreamCallers];
        for (var i = 0; i < callers.Length; i++)
            callers[i] = Task.Run(async () =>
            {
                await gate.Task;
                var count = 0;
                await foreach (var _ in _streamHost.Client.SendStreamingMessageAsync(Message.FromUser("go"))) count++;
                if (count < EventsPerStream) throw new InvalidOperationException($"Expected at least {EventsPerStream} events, got {count}.");
                return count;
            });
        gate.SetResult();
        await Task.WhenAll(callers);
    }

    private sealed class ReplyAgent : IAgentExecutor
    {
        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct) =>
            await updater.ReplyAsync("hi there friend", ct);
    }

    private sealed class ManyEventsAgent : IAgentExecutor
    {
        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            await updater.StartWorkAsync(cancellationToken: ct);
            var id = await updater.AddArtifactAsync([Part.FromText("chunk")], name: "out", cancellationToken: ct);
            for (var i = 0; i < EventsPerStream - 2; i++)
                await updater.AddArtifactAsync([Part.FromText("chunk")], artifactId: id, append: true, cancellationToken: ct);
            await updater.CompleteAsync("done", ct);
        }
    }
}
