using A2A.Client;
using A2A.Client.Grpc;
using Grpc.Net.Client;

namespace A2A.Grpc.Tests;

/// <summary>
/// The channel pool of the gRPC transport factories: one channel per address, leased per call, and a channel no call holds
/// is disposed after an idle timeout or past an idle limit, so a host that calls many agents does not keep a channel per
/// agent forever. A client whose channel was disposed gets a new one on its next call.
/// </summary>
public sealed class GrpcChannelPoolTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A pool whose channels reach <paramref name="host" /> whatever the address, and that records disposals.</summary>
    private static (GrpcChannelPool Pool, List<Uri> Created, List<GrpcChannel> Disposed) Pool(GrpcHost? host, int maxIdle,
        TimeSpan idle, Clock clock)
    {
        var created = new List<Uri>();
        var disposed = new List<GrpcChannel>();
        var pool = new GrpcChannelPool(address =>
            {
                lock (created) created.Add(address);
                return host is null
                    ? GrpcChannel.ForAddress(address)
                    : GrpcChannel.ForAddress(host.BaseAddress, new GrpcChannelOptions { HttpHandler = host.Handler("alice") });
            },
            channel =>
            {
                lock (disposed) disposed.Add(channel);
                channel.Dispose();
            }, maxIdle, idle, clock);
        return (pool, created, disposed);
    }

    private static Uri Agent(int n) => new($"https://agent{n}.example:443/");

    [Fact]
    public void Calls_to_one_address_share_one_channel()
    {
        var (pool, created, _) = Pool(null, 4, TimeSpan.FromMinutes(5), new Clock());
        using (var a = pool.Acquire(Agent(1)))
        using (var b = pool.Acquire(Agent(1)))
            Assert.Same(a.Channel, b.Channel);
        Assert.Single(created);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void Past_the_idle_limit_the_least_recently_used_idle_channel_is_disposed()
    {
        var clock = new Clock();
        var (pool, _, disposed) = Pool(null, 2, TimeSpan.FromHours(1), clock);
        GrpcChannel first;
        using (var lease = pool.Acquire(Agent(1))) first = lease.Channel;
        clock.Now += TimeSpan.FromSeconds(1);
        pool.Acquire(Agent(2)).Dispose();
        clock.Now += TimeSpan.FromSeconds(1);
        pool.Acquire(Agent(3)).Dispose();

        Assert.Same(first, Assert.Single(disposed)); // agent 1 was idle the longest
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void An_idle_channel_is_disposed_after_the_idle_timeout()
    {
        var clock = new Clock();
        var (pool, _, disposed) = Pool(null, 64, TimeSpan.FromMinutes(5), clock);
        pool.Acquire(Agent(1)).Dispose();
        clock.Now += TimeSpan.FromMinutes(4);
        pool.Acquire(Agent(2)).Dispose();
        Assert.Empty(disposed);

        clock.Now += TimeSpan.FromMinutes(2); // agent 1 has now been idle for 6 minutes, agent 2 for 2
        pool.Acquire(Agent(3)).Dispose();
        Assert.Single(disposed);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void A_channel_a_call_holds_is_never_disposed()
    {
        var clock = new Clock();
        var (pool, _, disposed) = Pool(null, 0, TimeSpan.Zero, clock);
        var held = pool.Acquire(Agent(1));
        clock.Now += TimeSpan.FromHours(1);
        pool.Acquire(Agent(2)).Dispose(); // no idle channel is kept at all, but agent 1 is in use

        Assert.DoesNotContain(held.Channel, disposed);
        held.Dispose();
        held.Dispose(); // a second release changes nothing
        Assert.Contains(held.Channel, disposed);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public async Task A_client_whose_channel_was_disposed_gets_a_new_one_on_its_next_call()
    {
        await using var host = await GrpcHost.StartAsync();
        // No idle channel is kept: each call's channel is disposed as soon as the call ends.
        var (pool, created, disposed) = Pool(host, 0, TimeSpan.Zero, new Clock());
        var options = new A2AClientOptions();
        var client = new A2AClient(new GrpcTransport(pool.CallInvoker(host.BaseAddress), options), options, GrpcHost.Card());

        Assert.IsType<MessageResponse>(await client.SendMessageAsync("hello"));
        Assert.Single(disposed);
        Assert.IsType<MessageResponse>(await client.SendMessageAsync("hello"));
        Assert.Equal(2, created.Count);
        Assert.Equal(2, disposed.Count);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public async Task A_unary_call_releases_its_channel_when_it_ends_even_if_never_disposed()
    {
        await using var host = await GrpcHost.StartAsync();
        var (pool, _, disposed) = Pool(host, 0, TimeSpan.Zero, new Clock());
        var raw = new Lf.A2a.V1.A2AService.A2AServiceClient(pool.CallInvoker(host.BaseAddress));

        // Awaited without a using: the lease must still be released when the response (here an error) arrives.
        await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(async () =>
            await raw.GetTaskAsync(new Lf.A2a.V1.GetTaskRequest { Id = "nope" }, new global::Grpc.Core.Metadata { { "a2a-version", "1.0" } }));
        Assert.Single(disposed);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public async Task A_stream_holds_its_channel_until_it_ends()
    {
        await using var host = await GrpcHost.StartAsync();
        var (pool, _, disposed) = Pool(host, 0, TimeSpan.Zero, new Clock());
        var options = new A2AClientOptions();
        var client = new A2AClient(new GrpcTransport(pool.CallInvoker(host.BaseAddress), options), options, GrpcHost.Card());

        var events = 0;
        await foreach (var _ in client.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("task") }))
        {
            Assert.Empty(disposed); // in use while the stream is read
            events++;
        }

        Assert.True(events > 1);
        Assert.Single(disposed); // released when the stream ended
    }

    [Fact]
    public void The_factories_share_one_pool_per_channel_options()
    {
        var options = new GrpcChannelOptions();
        Assert.Same(GrpcChannelPool.For(options), GrpcChannelPool.For(options));
        Assert.Same(GrpcChannelPool.For(null), GrpcChannelPool.For(null));
        Assert.NotSame(GrpcChannelPool.For(options), GrpcChannelPool.For(new GrpcChannelOptions()));
    }
}
