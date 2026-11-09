using A2A.Server;
using A2A.Server.Redis;
using A2A.Server.Streaming;
using StackExchange.Redis;

namespace A2A.Stores.Tests;

/// <summary>
/// Behavior every <see cref="TaskEventBus"/> must have. A bus pair stands for two server instances; for the process-local
/// bus both are the same instance.
/// </summary>
public abstract class TaskEventBusContract
{
    /// <summary>Returns the bus events are published on and the bus subscribers read from.</summary>
    protected abstract Task<(TaskEventBus Publisher, TaskEventBus Subscriber)> CreateBusesAsync();

    protected static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    protected static SequencedEvent Evt(long seq, string taskId = "task", TaskState state = TaskState.Working) => new(seq, StreamResponse.Of(
        new TaskStatusUpdateEvent { TaskId = taskId, ContextId = "c", Status = new AgentTaskStatus { State = state } }));

    protected static async Task<List<SequencedEvent>> ReadAsync(TaskEventBus.Subscription sub, int count)
    {
        var list = new List<SequencedEvent>();
        using var cts = new CancellationTokenSource(Wait);
        while (list.Count < count) list.Add(await sub.Reader.ReadAsync(cts.Token));
        return list;
    }

    [Fact]
    public async Task Subscriber_gets_every_event_in_order()
    {
        var (pub, subBus) = await CreateBusesAsync();
        using var sub = subBus.Subscribe("t\u001ftask");
        for (var i = 1; i <= 50; i++) pub.Publish("t\u001ftask", Evt(i));
        var got = await ReadAsync(sub, 50);
        Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i), got.Select(e => e.Sequence));
        var status = Assert.IsType<StatusUpdateEvent>(got[0].Event);
        Assert.Equal("task", status.StatusUpdate.TaskId);
    }

    [Fact]
    public async Task Subscriber_gets_the_encoded_bytes_as_published()
    {
        // The server encodes an event once; a bus hands subscribers the same bytes (streams write them as they are).
        var (pub, subBus) = await CreateBusesAsync();
        using var sub = subBus.Subscribe("t\u001ftask");
        var sent = EventEncoder.Encode(3, Evt(3).Event);
        pub.Publish("t\u001ftask", sent);
        var got = Assert.Single(await ReadAsync(sub, 1));
        Assert.Equal(3, got.Sequence);
        Assert.True(sent.Json.Span.SequenceEqual(got.Json.Span), "the event's bytes arrive unchanged");
    }

    [Fact]
    public async Task Every_subscriber_gets_every_event_and_topics_are_separate()
    {
        var (pub, subBus) = await CreateBusesAsync();
        using var a = subBus.Subscribe("t\u001fx");
        using var b = subBus.Subscribe("t\u001fx");
        using var other = subBus.Subscribe("t\u001fy");
        pub.Publish("t\u001fx", Evt(1, "x"));
        pub.Publish("t\u001fx", Evt(2, "x"));
        pub.Publish("u\u001fy", Evt(1, "y")); // same task id, other tenant
        pub.Publish("t\u001fy", Evt(7, "y"));

        Assert.Equal([1L, 2], (await ReadAsync(a, 2)).Select(e => e.Sequence));
        Assert.Equal([1L, 2], (await ReadAsync(b, 2)).Select(e => e.Sequence));
        Assert.Equal([7L], (await ReadAsync(other, 1)).Select(e => e.Sequence));
    }

    [Fact]
    public async Task Complete_ends_subscriptions_after_earlier_events()
    {
        var (pub, subBus) = await CreateBusesAsync();
        using var sub = subBus.Subscribe("t\u001ftask");
        pub.Publish("t\u001ftask", Evt(1));
        pub.Publish("t\u001ftask", Evt(2, state: TaskState.Completed));
        pub.Complete("t\u001ftask");

        var all = new List<long>();
        using var cts = new CancellationTokenSource(Wait);
        await foreach (var e in sub.Reader.ReadAllAsync(cts.Token)) all.Add(e.Sequence);
        Assert.Equal([1L, 2], all);
    }

    [Fact]
    public async Task Disposed_subscription_gets_nothing_more()
    {
        var (pub, subBus) = await CreateBusesAsync();
        using var live = subBus.Subscribe("t\u001ftask");
        var gone = subBus.Subscribe("t\u001ftask");
        gone.Dispose();
        pub.Publish("t\u001ftask", Evt(1));
        Assert.Equal([1L], (await ReadAsync(live, 1)).Select(e => e.Sequence)); // delivered by now
        Assert.False(gone.Reader.TryRead(out _));
    }
}

public sealed class InMemoryTaskEventBusTests : TaskEventBusContract
{
    protected override Task<(TaskEventBus, TaskEventBus)> CreateBusesAsync()
    {
        var bus = new TaskEventBus();
        return Task.FromResult<(TaskEventBus, TaskEventBus)>((bus, bus));
    }
}

/// <summary>Two Redis buses on one Redis: one publishes, the other (another "instance") subscribes.</summary>
[Collection(ContainersCollection.Name)]
public sealed class RedisTaskEventBusTests(RedisFixture redis) : TaskEventBusContract, IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _cleanup = [];

    protected override async Task<(TaskEventBus, TaskEventBus)> CreateBusesAsync()
    {
        var prefix = $"test:{Guid.NewGuid():N}:";
        var a = await NewBusAsync(prefix);
        var b = await NewBusAsync(prefix);
        return (a, b);
    }

    private async Task<RedisTaskEventBus> NewBusAsync(string prefix)
    {
        var mux = await redis.ConnectAsync();
        var bus = new RedisTaskEventBus(mux, new StreamingOptions(), new RedisEventBusOptions { ChannelPrefix = prefix });
        await bus.StartAsync();
        _cleanup.Add(mux);
        _cleanup.Insert(0, bus);
        return bus;
    }

    [Fact]
    public async Task Local_subscriber_gets_each_event_once()
    {
        var (pub, _) = await CreateBusesAsync();
        using var local = pub.Subscribe("t\u001ftask");
        pub.Publish("t\u001ftask", Evt(1));
        pub.Publish("t\u001ftask", Evt(2));
        Assert.Equal([1L, 2], (await ReadAsync(local, 2)).Select(e => e.Sequence));

        await Task.Delay(300); // time for an echo from Redis, which must be ignored
        Assert.False(local.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Deployments_with_different_prefixes_do_not_see_each_other()
    {
        var a = await NewBusAsync($"a:{Guid.NewGuid():N}:");
        var b = await NewBusAsync($"b:{Guid.NewGuid():N}:");
        using var sub = b.Subscribe("t\u001ftask");
        a.Publish("t\u001ftask", Evt(1));
        await Task.Delay(300);
        Assert.False(sub.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Publish_does_not_throw_when_redis_is_unreachable()
    {
        redis.SkipIfUnavailable();
        var options = ConfigurationOptions.Parse("127.0.0.1:1");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 200;
        await using var mux = await ConnectionMultiplexer.ConnectAsync(options);
        var bus = new RedisTaskEventBus(mux, new StreamingOptions());
        using var local = bus.Subscribe("t\u001ftask");
        bus.Publish("t\u001ftask", Evt(1));
        bus.Complete("t\u001ftask");
        Assert.Equal([1L], (await ReadAsync(local, 1)).Select(e => e.Sequence)); // local delivery still works
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var d in _cleanup) await d.DisposeAsync();
    }
}
