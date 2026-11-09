using System.Threading.Channels;
using A2A.Server;
using A2A.Server.Coordination;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using A2A.Tests.Execution;

namespace A2A.Tests.Streaming;

/// <summary>
/// An idle stream looks at the store every <see cref="CoordinationOptions.StreamCheckInterval"/>, so a lost bus message or a
/// crash after the commit does not leave it silent until the stream's time limit. Time is a manual clock.
/// </summary>
public sealed class StreamCheckTests
{
    private static readonly TimeSpan Wait = StreamRig.Wait;

    // Different from every other timer of the engine (heartbeat, time limits), so the stream's timer can be told apart.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(37);

    /// <summary>The manual clock. Counts the timers that were created for <see cref="Interval"/>: those are the stream's.</summary>
    private sealed class StreamClock : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new();
        private int _streamTimers;

        /// <summary>Timers created for the check interval so far.</summary>
        public int StreamTimers => Volatile.Read(ref _streamTimers);

        public void Advance(TimeSpan by) => _inner.Advance(by);

        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == Interval) Interlocked.Increment(ref _streamTimers);
            return _inner.CreateTimer(callback, state, dueTime, period);
        }

        public async Task WaitForStreamTimersAsync(int count)
        {
            using var timeout = new CancellationTokenSource(Wait);
            while (StreamTimers < count) await Task.Delay(1, timeout.Token);
        }
    }

    /// <summary>Drops artifact events: stored, never published.</summary>
    private sealed class ArtifactLossBus(StreamingOptions options) : SignalBus(options)
    {
        public override void Publish(string taskId, SequencedEvent evt)
        {
            if (evt.Event is ArtifactUpdateEvent) return;
            base.Publish(taskId, evt);
        }
    }

    /// <summary>Drops the terminal status event and never ends the subscriptions.</summary>
    private sealed class SilentBus(StreamingOptions options) : SignalBus(options)
    {
        public override void Publish(string taskId, SequencedEvent evt)
        {
            if (evt.Event is StatusUpdateEvent { StatusUpdate.Status.State: var s } && s.IsTerminal()) return;
            base.Publish(taskId, evt);
        }

        public override void Complete(string taskId)
        {
        }
    }

    /// <summary>Reads a stream in the background, like a client does.</summary>
    private sealed class Pump : IAsyncDisposable
    {
        private readonly Channel<SequencedEvent> _events = Channel.CreateUnbounded<SequencedEvent>();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;

        public Pump(IAsyncEnumerable<SequencedEvent> stream)
        {
            _run = Task.Run(async () =>
            {
                await foreach (var e in stream.WithCancellation(_cts.Token)) _events.Writer.TryWrite(e);
            });
        }

        /// <summary>Completes when the stream ended; an error of the stream surfaces here.</summary>
        public Task Ended => _run;

        public bool IsEnded => _run.IsCompleted;

        /// <summary>Events received and not yet taken by the test.</summary>
        public int Pending => _events.Reader.Count;

        public async Task<SequencedEvent> NextAsync() => await _events.Reader.ReadAsync().AsTask().WaitAsync(Wait);

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { await _run.WaitAsync(Wait); }
            catch (OperationCanceledException) { }
            _cts.Dispose();
        }
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private int _ticks;

        public required StreamRig Rig { get; init; }
        public required StreamClock Clock { get; init; }
        public required TaskKey Key { get; init; }
        public required TaskCompletionSource Gate { get; init; }
        public Pump Stream { get; set; } = null!;

        /// <summary>
        /// One tick: waits until the stream's timer is set, advances the clock by the check interval, and waits until the stream
        /// has handled it: it set its next timer, or it ended.
        /// </summary>
        public async Task TickAsync()
        {
            await Clock.WaitForStreamTimersAsync(_ticks + 1);
            Clock.Advance(Interval);
            _ticks++;
            using var timeout = new CancellationTokenSource(Wait);
            while (!Stream.IsEnded && Clock.StreamTimers < _ticks + 1) await Task.Delay(1, timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            Gate.TrySetResult();
            await Stream.DisposeAsync();
            await Rig.DisposeAsync();
        }
    }

    /// <summary>
    /// Starts a task with <paramref name="agent"/>, waits until its log has <paramref name="settledSequence"/> events (and the
    /// agent ended when <paramref name="agentEnds"/>), and opens a stream on it. The first event (the snapshot) is read and
    /// the stream's first timer is set when this returns.
    /// </summary>
    private static async Task<Scenario> StartAsync(Func<AgentRequestContext, TaskUpdater, TaskCompletionSource, CancellationToken, Task> agent,
        long settledSequence, bool agentEnds, Func<StreamingOptions, TaskEventBus>? bus = null, Func<StreamClock, ExecutionCoordinator>? coordinator = null)
    {
        var clock = new StreamClock();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = StreamRig.Create((c, u, ct) => agent(c, u, gate, ct),
            o =>
            {
                o.Coordination.StreamCheckInterval = Interval;
                o.Coordination.LeaseTtl = TimeSpan.FromHours(1); // a lease never ends while a test advances the clock
            },
            bus is null ? null : bus(new StreamingOptions()), clock, coordinator?.Invoke(clock));
        var context = StreamRig.NewTaskContext();
        var key = StreamRig.KeyOf(context);
        var scenario = new Scenario { Rig = rig, Clock = clock, Key = key, Gate = gate };
        var exec = await rig.A.StartForTestAsync(context);
        exec.Detach();
        await rig.WaitForSequenceAsync(key, settledSequence);
        if (agentEnds) await exec.Completion.WaitAsync(Wait);
        scenario.Stream = new Pump(rig.Subscribe(key));
        await scenario.Stream.NextAsync(); // the snapshot
        await clock.WaitForStreamTimersAsync(1);
        return scenario;
    }

    /// <summary>WORKING, then the agent returns without settling the task: nothing runs it any more.</summary>
    private static async Task Abandoned(AgentRequestContext c, TaskUpdater u, TaskCompletionSource gate, CancellationToken ct) =>
        await u.StartWorkAsync(cancellationToken: ct);

    /// <summary>WORKING, then one artifact once the gate opens, then keeps running.</summary>
    private static async Task ArtifactAfterGate(AgentRequestContext c, TaskUpdater u, TaskCompletionSource gate, CancellationToken ct)
    {
        await u.StartWorkAsync(cancellationToken: ct);
        await gate.Task.WaitAsync(ct);
        await u.AddArtifactAsync([Part.FromText("x")], artifactId: "a1", cancellationToken: ct);
        await Task.Delay(Timeout.Infinite, ct);
    }

    [Fact]
    public async Task Event_the_bus_dropped_is_sent_from_the_store_after_a_tick()
    {
        await using var s = await StartAsync(ArtifactAfterGate, settledSequence: 2, agentEnds: false, bus: o => new ArtifactLossBus(o));

        s.Gate.SetResult();
        await s.Rig.WaitForSequenceAsync(s.Key, 3);
        Assert.Equal(0, s.Stream.Pending); // the bus lost it
        await s.TickAsync();

        var evt = await s.Stream.NextAsync();
        Assert.Equal(3, evt.Sequence);
        Assert.IsType<ArtifactUpdateEvent>(evt.Event);
        Assert.False(s.Stream.IsEnded);
    }

    [Fact]
    public async Task Terminal_event_the_bus_dropped_ends_the_stream_with_the_final_event_after_a_tick()
    {
        await using var s = await StartAsync(async (c, u, gate, ct) =>
        {
            await u.StartWorkAsync(cancellationToken: ct);
            await gate.Task.WaitAsync(ct);
            await u.CompleteAsync(cancellationToken: ct);
        }, settledSequence: 2, agentEnds: false, bus: o => new SilentBus(o));

        s.Gate.SetResult();
        await s.Rig.WaitForSequenceAsync(s.Key, 3);
        Assert.Equal(0, s.Stream.Pending); // the bus lost it, and does not end the topic
        Assert.False(s.Stream.IsEnded);
        await s.TickAsync();

        await s.Stream.Ended.WaitAsync(Wait);
        var evt = await s.Stream.NextAsync();
        Assert.Equal(3, evt.Sequence);
        Assert.Equal(TaskState.Completed, Assert.IsType<StatusUpdateEvent>(evt.Event).StatusUpdate.Status.State);
        Assert.Equal(0, s.Stream.Pending);
    }

    [Fact]
    public async Task Working_task_that_nothing_runs_ends_cleanly_on_the_second_tick()
    {
        await using var s = await StartAsync(Abandoned, settledSequence: 2, agentEnds: true);
        var reads = s.Rig.Tasks.Gets;

        await s.TickAsync();
        Assert.Equal(reads + 1, s.Rig.Tasks.Gets); // it did look
        Assert.False(s.Stream.IsEnded); // one tick can be a follow-up that is not registered yet

        await s.TickAsync();
        await s.Stream.Ended.WaitAsync(Wait); // a clean end, not an error
        Assert.Equal(0, s.Stream.Pending);
    }

    [Fact]
    public async Task Interrupted_task_stays_open_over_many_ticks()
    {
        await using var s = await StartAsync(async (c, u, gate, ct) => await u.RequireInputAsync("more?", ct), settledSequence: 2, agentEnds: true);
        var reads = s.Rig.Tasks.Gets;

        for (var i = 0; i < 6; i++) await s.TickAsync();

        Assert.Equal(reads + 6, s.Rig.Tasks.Gets); // every tick looked
        Assert.False(s.Stream.IsEnded);
        Assert.Equal(0, s.Stream.Pending);
    }

    [Fact]
    public async Task Failing_coordinator_does_not_end_the_stream_and_later_ticks_still_work()
    {
        FaultyCoordinator? faulty = null;
        await using var s = await StartAsync(Abandoned, settledSequence: 2, agentEnds: true,
            coordinator: clock => faulty = new FaultyCoordinator(clock) { ThrowOnIsRunning = true });

        await s.TickAsync(); // the running check throws
        Assert.False(s.Stream.IsEnded);

        faulty!.ThrowOnIsRunning = false;
        await s.TickAsync(); // nothing runs the task: the first stranded tick, which the failed tick did not count
        Assert.False(s.Stream.IsEnded);

        await s.TickAsync(); // the second one
        await s.Stream.Ended.WaitAsync(Wait);
    }

    [Fact]
    public async Task Execution_running_on_another_instance_keeps_the_stream_open()
    {
        await using var s = await StartAsync(Abandoned, settledSequence: 2, agentEnds: true,
            coordinator: clock => new FaultyCoordinator(clock) { ReportRunning = true });
        var reads = s.Rig.Tasks.Gets;

        for (var i = 0; i < 4; i++) await s.TickAsync();

        Assert.Equal(reads + 4, s.Rig.Tasks.Gets);
        Assert.False(s.Stream.IsEnded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failing_store_read_does_not_end_the_stream(bool failSequenceRead)
    {
        await using var s = await StartAsync(Abandoned, settledSequence: 2, agentEnds: true);
        if (failSequenceRead) s.Rig.Events.FailNextLastReads(1);
        else s.Rig.Tasks.FailNextGets(1);

        await s.TickAsync(); // the check fails: nothing to do
        Assert.False(s.Stream.IsEnded);

        await s.TickAsync(); // the first stranded tick
        Assert.False(s.Stream.IsEnded);

        await s.TickAsync();
        await s.Stream.Ended.WaitAsync(Wait);
    }

    [Fact]
    public async Task Busy_stream_does_not_read_the_store_on_a_tick()
    {
        await using var s = await StartAsync(ArtifactAfterGate, settledSequence: 2, agentEnds: false);

        s.Gate.SetResult();
        Assert.Equal(3, (await s.Stream.NextAsync()).Sequence); // live, from the bus
        var reads = s.Rig.Tasks.Gets;
        await s.TickAsync(); // an event arrived since the last tick: no check
        Assert.Equal(reads, s.Rig.Tasks.Gets);

        await s.TickAsync(); // quiet since: it looks now
        Assert.Equal(reads + 1, s.Rig.Tasks.Gets);
        Assert.False(s.Stream.IsEnded);
    }

    [Fact]
    public void Stream_check_interval_must_be_positive_and_finite()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), new CoordinationOptions().StreamCheckInterval);
        new CoordinationOptions { StreamCheckInterval = TimeSpan.FromMilliseconds(1) }.Validate();
        foreach (var bad in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1), Timeout.InfiniteTimeSpan })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new CoordinationOptions { StreamCheckInterval = bad }.Validate());
            Assert.Contains("StreamCheckInterval", ex.Message);
        }
    }
}
