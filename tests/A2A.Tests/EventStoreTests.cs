using A2A.Server;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>Manually advanced clock whose timers fire when <see cref="Advance"/> passes their due time.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<FakeTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.Due is { } d && d <= _now).ToList();
            foreach (var t in due) t.Fired(_now);
        }
        foreach (var t in due) t.Invoke();
    }

    private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; private set; }
        private TimeSpan _period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                if (Due is not null && !owner._timers.Contains(this)) owner._timers.Add(this);
                return true;
            }
        }

        public void Fired(DateTimeOffset now) =>
            Due = _period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan ? now + _period : null;

        public void Invoke() => callback(state);

        public void Dispose()
        {
            lock (owner._gate) { owner._timers.Remove(this); Due = null; }
        }

        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

public class EventStoreTests
{
    private static StreamResponse Evt(string taskId) => StreamResponse.Of(new AgentTask
    {
        Id = taskId, ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working },
    });

    private static async Task<List<SequencedEvent>> ReadAllAsync(EventStore store, TaskKey key, long after = 0)
    {
        var list = new List<SequencedEvent>();
        await foreach (var e in store.ReadAfterAsync(key, after)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Sequences_start_at_1_and_increase_per_task()
    {
        var store = new InMemoryEventStore(new StreamingOptions());
        var a = new TaskKey("", "alice", "a");
        var b = new TaskKey("", "alice", "b");
        var seqA = new List<long>();
        var seqB = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            seqA.Add(await store.AppendAsync(a, Evt("a")));
            seqB.Add(await store.AppendAsync(b, Evt("b")));
        }
        Assert.Equal([1L, 2, 3], seqA);
        Assert.Equal([1L, 2, 3], seqB);
        Assert.Equal(3, await store.LastSequenceAsync(a));
        Assert.Equal(0, await store.LastSequenceAsync(new TaskKey("", "alice", "none")));
        Assert.Equal([2L, 3], (await ReadAllAsync(store, a, after: 1)).Select(e => e.Sequence));
    }

    [Fact]
    public async Task Sequences_are_scoped_by_owner()
    {
        var store = new InMemoryEventStore(new StreamingOptions());
        await store.AppendAsync(new TaskKey("", "alice", "t"), Evt("t"));
        Assert.Equal(1, await store.AppendAsync(new TaskKey("", "bob", "t"), Evt("t")));
        Assert.Single(await ReadAllAsync(store, new TaskKey("", "bob", "t")));
    }

    [Fact]
    public async Task Buffer_trims_to_replay_size()
    {
        var store = new InMemoryEventStore(new StreamingOptions { ReplayBufferSize = 3 });
        var key = new TaskKey("", "alice", "t");
        Assert.Null(await store.OldestRetainedAsync(key));
        for (var i = 0; i < 5; i++) await store.AppendAsync(key, Evt("t"));
        Assert.Equal(3, await store.OldestRetainedAsync(key));
        Assert.Equal(5, await store.LastSequenceAsync(key));
        Assert.Equal([3L, 4, 5], (await ReadAllAsync(store, key)).Select(e => e.Sequence));
    }

    [Fact]
    public async Task Terminal_buffer_evicted_after_retention()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryEventStore(new StreamingOptions { TerminalRetention = TimeSpan.FromMinutes(10) }, clock);
        var key = new TaskKey("", "alice", "t");
        await store.AppendAsync(key, Evt("t"));
        await store.MarkTerminalAsync(key);

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(1, await store.OldestRetainedAsync(key));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await store.OldestRetainedAsync(key));
        Assert.Empty(await ReadAllAsync(store, key));
    }

    [Theory]
    [InlineData("JSONRPC")]
    [InlineData("HTTP+JSON")]
    public async Task Push_sequence_header_matches_event_sequence(string binding)
    {
        var route = binding.Replace("+", "-");
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var config = new SendMessageConfiguration
        {
            TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = $"http://hooks.test/{route}", Token = "t" },
        };
        var task = (await client.SendMessageAsync(Message.FromUser("task"), config)).RequireTask();
        await host.WaitIdleAsync();

        var headerSeqs = host.Webhooks.Calls.Where(c => c.Request.RequestUri!.AbsolutePath == $"/{route}")
            .Select(c => long.Parse(c.Request.Headers.GetValues(A2AProtocol.Headers.NotificationSequence).Single())).ToList();
        var store = host.App.Services.GetRequiredService<EventStore>();
        var stored = await ReadAllAsync(store, new TaskKey("", "alice", task.Id));

        Assert.Equal(5, headerSeqs.Count);
        Assert.Equal(stored.Select(e => e.Sequence), headerSeqs);
        Assert.Equal([1L, 2, 3, 4, 5], headerSeqs);
    }
}
