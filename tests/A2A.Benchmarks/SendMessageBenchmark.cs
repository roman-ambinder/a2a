using A2A.Server;
using BenchmarkDotNet.Attributes;

namespace A2A.Benchmarks;

/// <summary>One JSON-RPC SendMessage round trip through the in-process server; the agent replies with three words.</summary>
[MemoryDiagnoser]
public class SendMessageBenchmark
{
    private BenchHost _host = null!;

    [GlobalSetup]
    public async Task Setup() => _host = await BenchHost.StartAsync<ReplyAgent>();

    [GlobalCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    [Benchmark]
    public async Task<SendMessageResponse> RoundTrip() => await _host.Client.SendMessageAsync(Message.FromUser("hello agent"));

    private sealed class ReplyAgent : IAgentExecutor
    {
        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct) =>
            await updater.ReplyAsync("hi there friend", ct);
    }
}
