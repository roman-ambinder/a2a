using System.Runtime.CompilerServices;
using System.Security.Claims;
using A2A.Server;
using A2A.Server.Agents;
using A2A.Server.Authorization;
using A2A.Server.Execution;
using A2A.Server.Idempotency;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using A2A.Tests.Execution;
using A2A.Tests.Parity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace A2A.Tests.Streaming;

/// <summary>
/// Every per-task channel is bounded (spec D12): a bus subscriber that falls behind loses queued events and fills the gap
/// from the event store, the agent is throttled when the engine is behind, and no channel write waits under the task lock.
/// </summary>
public sealed class BoundedChannelTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static ServerCallContext Call() => new()
    {
        Principal = "alice",
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "Test")),
    };

    private static TaskKey Key(string taskId) => new("", "alice", taskId);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task<TestAgentHost> StartAsync(int capacity, Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> agent,
        Action<A2AServerOptions>? configure = null) =>
        TestAgentHost.StartAsync(o =>
        {
            o.Streaming.ChannelCapacity = capacity;
            configure?.Invoke(o);
        }, services: s => s.AddSingleton<IAgentExecutor>(new DelegateAgentExecutor(agent)));

    private static async Task WaitForSequenceAsync(EventStore events, TaskKey key, long sequence)
    {
        using var timeout = new CancellationTokenSource(Wait);
        while (await events.LastSequenceAsync(key) < sequence) await Task.Delay(5, timeout.Token);
    }

    private static void AssertContiguous(IReadOnlyList<long> sequences, long first, long last)
    {
        Assert.Equal(first, sequences[0]);
        Assert.Equal(last, sequences[^1]);
        for (var i = 1; i < sequences.Count; i++) Assert.Equal(sequences[i - 1] + 1, sequences[i]);
    }

    private static bool IsState(StreamResponse evt, TaskState state) =>
        evt is StatusUpdateEvent { StatusUpdate.Status.State: var s } && s == state;

    [Fact]
    public async Task Slow_subscriber_receives_all_events_in_order()
    {
        var go = Signal();
        await using var host = await StartAsync(4, async (_, u, ct) =>
        {
            await u.StartWorkAsync(cancellationToken: ct);
            await go.Task.WaitAsync(ct);
            for (var i = 0; i < 200; i++) await u.AddArtifactAsync([Part.FromText("x")], artifactId: $"a{i}", cancellationToken: ct);
            await u.CompleteAsync(cancellationToken: ct);
        });

        await using var send = host.Handler.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("go") }, Call())
            .GetAsyncEnumerator();
        Assert.True(await send.MoveNextAsync());
        var taskId = Assert.IsType<TaskEvent>(send.Current.Event).Task.Id;
        var sent = new List<long> { send.Current.Sequence };

        await using var subscribe = host.Handler.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId }, Call()).GetAsyncEnumerator();
        Assert.True(await subscribe.MoveNextAsync());
        var subscribed = new List<long> { subscribe.Current.Sequence };

        go.SetResult();
        // Both readers are slower than the agent, so their bounded channels overflow many times over.
        var sendReader = Task.Run(async () =>
        {
            while (await send.MoveNextAsync())
            {
                sent.Add(send.Current.Sequence);
                await Task.Delay(5);
            }
        });
        var subscribeReader = Task.Run(async () =>
        {
            while (await subscribe.MoveNextAsync())
            {
                subscribed.Add(subscribe.Current.Sequence);
                await Task.Delay(5);
            }
        });
        await Task.WhenAll(sendReader, subscribeReader).WaitAsync(TimeSpan.FromSeconds(30));

        // The new task, WORKING, 200 artifacts, COMPLETED: every sequence, once, in order.
        AssertContiguous(sent, 1, 203);
        AssertContiguous(subscribed, subscribed[0], 203);
        Assert.Equal(200, (await host.Handler.GetTaskAsync(new GetTaskRequest { Id = taskId }, Call())).Artifacts!.Count);
    }

    [Fact]
    public async Task Lagging_subscriber_still_gets_the_latest_event()
    {
        // A subscriber that fell behind must still get the task's newest event when no further event follows: here the task
        // waits for input and the agent emits nothing more until the test ends.
        var go = Signal();
        var release = Signal();
        await using var host = await StartAsync(4, async (_, u, ct) =>
        {
            await u.StartWorkAsync(cancellationToken: ct);
            await go.Task.WaitAsync(ct);
            for (var i = 0; i < 50; i++) await u.AddArtifactAsync([Part.FromText("x")], artifactId: $"a{i}", cancellationToken: ct);
            await u.RequireInputAsync("more?", ct);
            await release.Task.WaitAsync(ct);
        });
        try
        {
            var response = await host.Handler.SendMessageAsync(new SendMessageRequest
            {
                Message = Message.FromUser("go"),
                Configuration = new SendMessageConfiguration { ReturnImmediately = true },
            }, Call());
            var taskId = response.RequireTask().Id;

            using var timeout = new CancellationTokenSource(Wait);
            await using var subscribe = host.Handler.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId }, Call(),
                cancellationToken: timeout.Token).GetAsyncEnumerator();
            Assert.True(await subscribe.MoveNextAsync());
            var seen = new List<long> { subscribe.Current.Sequence };

            go.SetResult();
            // The subscriber does not read until INPUT_REQUIRED is stored (sequence 53): its channel overflowed meanwhile.
            await WaitForSequenceAsync(host.App.Services.GetRequiredService<EventStore>(), Key(taskId), 53);
            try
            {
                while (true)
                {
                    Assert.True(await subscribe.MoveNextAsync());
                    seen.Add(subscribe.Current.Sequence);
                    if (IsState(subscribe.Current.Event, TaskState.InputRequired)) break;
                }
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"The subscriber never got INPUT_REQUIRED (sequence 53); its last sequence was {seen[^1]}.");
            }
            AssertContiguous(seen, seen[0], 53);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Fast_agent_is_throttled_not_dropped()
    {
        var options = new A2AServerOptions();
        options.AgentCard.Name = "Bounded Channel Test Agent";
        options.Streaming.ChannelCapacity = 2;
        var events = new InMemoryEventStore(options.Streaming);
        var tasks = new InMemoryTaskStore(events);
        var resume = Signal();
        var completed = 0;
        var factory = new DelegateFactory(async (_, u, ct) =>
        {
            await u.StartWorkAsync(cancellationToken: ct);
            for (var i = 1; i <= 10; i++)
            {
                if (i == 7) await resume.Task;
                await u.AddArtifactAsync([Part.FromText("x")], artifactId: $"a{i}", cancellationToken: ct);
                Interlocked.Increment(ref completed);
            }
            await u.CompleteAsync(cancellationToken: ct);
        });
        await using var engine = new TaskExecutionEngine(tasks, events, factory, new TaskEventBus(options.Streaming), new InMemoryPushOutbox(),
            new IdempotencyCoordinator(new InMemoryIdempotencyStore(options.Idempotency), tasks, options),
            new PushConfigService(new InMemoryPushConfigStore(), new EphemeralPushSecretProtector(), options), options,
            new AgentCardProvider(options, []));
        var context = new AgentRequestContext
        {
            Message = Message.FromUser("go"), TaskId = TaskIds.New(), ContextId = TaskIds.New(), Call = Call(),
        };
        var key = new TaskKey(context.Call.Tenant, context.Call.Principal, context.TaskId);

        var exec = (await engine.StartForTestAsync(context));
        exec.Detach(); // nobody reads the outgoing events here

        // The new task, WORKING and six artifacts are stored (sequence 8); then the engine's consumer is paused by holding the
        // task lock: it takes artifact 7 and waits for the lock, artifacts 8 and 9 fill the capacity of 2, and the 10th
        // AddArtifactAsync waits for room.
        await WaitForSequenceAsync(events, key, 8);
        using (await engine.LockAsync(key, CancellationToken.None))
        {
            resume.SetResult();
            using (var timeout = new CancellationTokenSource(Wait))
                while (Volatile.Read(ref completed) < 9) await Task.Delay(5, timeout.Token);
            await Task.Delay(200); // negative check: the 10th call stays blocked in this time
            Assert.Equal(9, Volatile.Read(ref completed));
            Assert.Equal(8, await events.LastSequenceAsync(key));
        }

        // The consumer resumes: the 10th call completes, and nothing was dropped.
        await exec.Completion.WaitAsync(Wait);
        Assert.Equal(10, Volatile.Read(ref completed));
        var stored = new List<long>();
        await foreach (var e in events.ReadAfterAsync(key, 0)) stored.Add(e.Sequence);
        AssertContiguous(stored, 1, 13);
        var record = await tasks.GetAsync(key);
        Assert.Equal(10, record!.Task.Artifacts!.Count);
        Assert.Equal(TaskState.Completed, record.Task.Status.State);
    }

    [Fact]
    public async Task Default_capacity_is_256()
    {
        Assert.Equal(256, new StreamingOptions().ChannelCapacity);
        Assert.Equal(256, new TaskEventBus().Capacity);
        Assert.Equal(4, new TaskEventBus(new StreamingOptions { ChannelCapacity = 4 }).Capacity);

        // The container builds the bus from the configured options.
        await using var host = await TestAgentHost.StartAsync(o => o.Streaming.ChannelCapacity = 7);
        Assert.Equal(7, host.App.Services.GetRequiredService<TaskEventBus>().Capacity);
    }

    [Fact]
    public async Task Slow_stream_reader_does_not_block_get_task()
    {
        var stop = Signal();
        await using var host = await StartAsync(2, async (_, u, ct) =>
        {
            await u.StartWorkAsync(cancellationToken: ct);
            for (var i = 0; i < 5000 && !stop.Task.IsCompleted; i++)
            {
                await u.AddArtifactAsync([Part.FromText("x")], artifactId: $"a{i}", cancellationToken: ct);
                await Task.Delay(1, ct);
            }
            await u.CompleteAsync(cancellationToken: ct);
        }, o => o.Streaming.ReplayBufferSize = 10_000);
        var events = host.App.Services.GetRequiredService<EventStore>();

        // The send stream's reader takes the first event and then stalls, as a client that stopped reading would.
        await using var send = host.Handler.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("go") }, Call())
            .GetAsyncEnumerator();
        Assert.True(await send.MoveNextAsync());
        var taskId = Assert.IsType<TaskEvent>(send.Current.Event).Task.Id;
        var sent = new List<long> { send.Current.Sequence };
        try
        {
            // The engine keeps storing events far beyond the channel capacity of 2...
            await WaitForSequenceAsync(events, Key(taskId), 50);

            // ...and operations on the same task are not held up: GetTask, and SubscribeToTask (which takes the task lock).
            var task = await host.Handler.GetTaskAsync(new GetTaskRequest { Id = taskId }, Call()).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(TaskState.Working, task.Status.State);
            await using (var subscribe = host.Handler.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId }, Call()).GetAsyncEnumerator())
                Assert.True(await subscribe.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

            // The agent is still emitting.
            var now = await events.LastSequenceAsync(Key(taskId));
            await WaitForSequenceAsync(events, Key(taskId), now + 10);
        }
        finally
        {
            stop.TrySetResult();
        }

        // The stalled reader resumes and still gets every event, in order: the dropped ones come from the event store.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await send.MoveNextAsync().AsTask().WaitAsync(timeout.Token)) sent.Add(send.Current.Sequence);
        var last = await events.LastSequenceAsync(Key(taskId));
        AssertContiguous(sent, 1, last);
        Assert.True(last > 50);
    }

    [Fact]
    public async Task Blocking_send_returns_the_outcome_with_a_tiny_capacity()
    {
        var release = Signal();
        await using var host = await StartAsync(1, async (_, u, ct) =>
        {
            await u.StartWorkAsync(cancellationToken: ct);
            for (var i = 0; i < 100; i++) await u.AddArtifactAsync([Part.FromText("x")], artifactId: $"a{i}", cancellationToken: ct);
            await u.RequireInputAsync("more?", ct);
            await release.Task.WaitAsync(ct); // still running: only the INPUT_REQUIRED event ends the wait
        });
        try
        {
            var response = await host.Handler.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("go") }, Call())
                .WaitAsync(Wait);
            var task = response.RequireTask();
            Assert.Equal(TaskState.InputRequired, task.Status.State);
            Assert.Equal(100, task.Artifacts!.Count);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // The blocking waits read the bus only for outcomes; a jump in sequences sends them to the store (and, when the store was
    // trimmed, to the stored task).
    // ---------------------------------------------------------------------------------------------------------------------

    /// <summary>An engine and a streamer built with <c>new</c>, sharing one bus of the configured capacity.</summary>
    private sealed record Rig(TaskExecutionEngine Engine, TaskStreamer Streamer, TaskEventBus Bus) : IAsyncDisposable
    {
        public static Rig Create(A2AServerOptions options, TaskStore tasks, EventStore events,
            Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> agent, ILogger<TaskExecutionEngine>? logger = null)
        {
            var bus = new TaskEventBus(options.Streaming);
            var idempotency = new IdempotencyCoordinator(new InMemoryIdempotencyStore(options.Idempotency), tasks, options);
            var engine = new TaskExecutionEngine(tasks, events, new DelegateFactory(agent), bus, new InMemoryPushOutbox(), idempotency,
                new PushConfigService(new InMemoryPushConfigStore(), new EphemeralPushSecretProtector(), options), options,
                new AgentCardProvider(options, []), logger);
            var allowAll = new AllowAllAuthorizationHandler([]);
            var cards = new AgentCardProvider(options, []);
            var streamer = new TaskStreamer(tasks, events, engine, idempotency, allowAll, options, cards,
                new SkillAuthorizer(allowAll, options.SkillAuthorization, cards, new RequestCards(cards)));
            return new Rig(engine, streamer, bus);
        }

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    private static AgentRequestContext NewTaskContext() => new()
    {
        Message = Message.FromUser("go"), TaskId = TaskIds.New(), ContextId = TaskIds.New(), Call = Call(),
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blocking_wait_finds_an_outcome_the_bus_dropped(bool trimmed)
    {
        // Capacity 1: the request's own channel keeps only the new task (sequence 1), the bus only the newest event. The
        // INPUT_REQUIRED outcome (sequence 3) reaches neither, and the agent emits nothing more until released.
        var options = new A2AServerOptions();
        options.AgentCard.Name = "Bounded Channel Test Agent";
        options.Streaming.ChannelCapacity = 1;
        if (trimmed) options.Streaming.ReplayBufferSize = 2; // the store keeps only the two artifacts after it
        var events = new InMemoryEventStore(options.Streaming);
        var tasks = new InMemoryTaskStore(events);
        var release = Signal();
        await using var rig = Rig.Create(options, tasks, events, async (_, u, ct) =>
        {
            await u.StartWorkAsync(cancellationToken: ct);
            await u.RequireInputAsync("more?", ct);
            if (trimmed)
            {
                await u.AddArtifactAsync([Part.FromText("x")], artifactId: "a1", cancellationToken: ct);
                await u.AddArtifactAsync([Part.FromText("x")], artifactId: "a2", cancellationToken: ct);
            }
            else await u.StartWorkAsync(cancellationToken: ct); // WORKING again: a status, but not an outcome
            await release.Task;
        });
        var context = NewTaskContext();
        var key = new TaskKey(context.Call.Tenant, context.Call.Principal, context.TaskId);
        try
        {
            using var sub = rig.Bus.Subscribe(TaskExecutionEngine.ScopeKey(key.Tenant, key.TaskId));
            var exec = (await rig.Engine.StartForTestAsync(context));
            await WaitForSequenceAsync(events, key, trimmed ? 5 : 4);

            var reply = await rig.Streamer.WaitForOutcomeAsync(exec, key, sub, afterSequence: 0, acceptedStatusTimestamp: null, returnImmediately: false,
                CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(reply); // the response is the task
            Assert.False(release.Task.IsCompleted); // the agent is still running: only the INPUT_REQUIRED outcome ended the wait
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>An event store whose next read, once armed, returns what the store held at that moment, but only when released.</summary>
    private sealed class StaleReadEventStore(EventStore inner) : EventStore
    {
        private int _armed;

        public TaskCompletionSource ReadStarted { get; } = Signal();
        public TaskCompletionSource ReleaseRead { get; } = Signal();

        public void Arm() => Volatile.Write(ref _armed, 1);

        public override async IAsyncEnumerable<SequencedEvent> ReadAfterAsync(TaskKey task, long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                var snapshot = new List<SequencedEvent>();
                await foreach (var e in inner.ReadAfterAsync(task, afterSequence, cancellationToken)) snapshot.Add(e);
                ReadStarted.TrySetResult();
                await ReleaseRead.Task.WaitAsync(cancellationToken);
                foreach (var e in snapshot) yield return e;
                yield break;
            }
            await foreach (var e in inner.ReadAfterAsync(task, afterSequence, cancellationToken)) yield return e;
        }

        public override ValueTask<long> AppendAsync(TaskKey task, SequencedEvent evt, CancellationToken cancellationToken = default) =>
            inner.AppendAsync(task, evt, cancellationToken);

        public override ValueTask<long?> OldestRetainedAsync(TaskKey task, CancellationToken cancellationToken = default) =>
            inner.OldestRetainedAsync(task, cancellationToken);

        public override ValueTask<long> LastSequenceAsync(TaskKey task, CancellationToken cancellationToken = default) =>
            inner.LastSequenceAsync(task, cancellationToken);

        public override ValueTask MarkTerminalAsync(TaskKey task, CancellationToken cancellationToken = default) =>
            inner.MarkTerminalAsync(task, cancellationToken);

        public override ValueTask DeleteAsync(TaskKey task, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(task, cancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replay_wait_finds_a_status_the_bus_dropped(bool trimmed)
    {
        var options = new A2AServerOptions();
        options.AgentCard.Name = "Bounded Channel Test Agent";
        options.Streaming.ChannelCapacity = 1;
        if (trimmed) options.Streaming.ReplayBufferSize = 2;
        var log = new InMemoryEventStore(options.Streaming);
        var tasks = new InMemoryTaskStore(log);
        var events = new StaleReadEventStore(log);
        var go = Signal();
        var release = Signal();
        await using var rig = Rig.Create(options, tasks, events, async (context, u, ct) =>
        {
            if (context.IsNewTask)
            {
                await u.StartWorkAsync(cancellationToken: ct);
                await u.RequireInputAsync("more?", ct);
                return;
            }
            // The follow-up, once the wait has read the store: WORKING (the status after the message), INPUT_REQUIRED, then
            // artifacts; then it keeps running.
            await go.Task;
            await u.StartWorkAsync(cancellationToken: ct);
            await u.RequireInputAsync("again?", ct);
            await u.AddArtifactAsync([Part.FromText("x")], artifactId: "a1", cancellationToken: ct);
            if (trimmed) await u.AddArtifactAsync([Part.FromText("x")], artifactId: "a2", cancellationToken: ct);
            await release.Task;
        });
        var first = NewTaskContext();
        var key = new TaskKey(first.Call.Tenant, first.Call.Principal, first.TaskId);
        try
        {
            var exec = (await rig.Engine.StartForTestAsync(first));
            exec.Detach();
            await exec.Completion.WaitAsync(Wait);
            var afterSequence = await events.LastSequenceAsync(key); // 3: the task is INPUT_REQUIRED
            var acceptedAt = (await tasks.GetAsync(key))!.Task.Status.Timestamp;

            var followUp = new AgentRequestContext
            {
                Message = Message.FromUser("more"), TaskId = first.TaskId, ContextId = first.ContextId, Call = first.Call,
                Task = (await tasks.GetAsync(key))!.Task.DeepCopy(),
            };
            (await rig.Engine.StartForTestAsync(followUp)).Detach();

            // The wait subscribes, then reads the store before the follow-up's events exist; that read is held back until
            // they are all published, and the bus (capacity 1) kept only the last artifact.
            events.Arm();
            var wait = rig.Engine.WaitUntilSettledAsync(key, afterSequence, acceptedAt, CancellationToken.None).AsTask();
            await events.ReadStarted.Task.WaitAsync(Wait);
            go.SetResult();
            await WaitForSequenceAsync(events, key, trimmed ? 7 : 6);
            events.ReleaseRead.SetResult();

            var task = await wait.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TaskState.InputRequired, task.Status.State);
            Assert.Equal("again?", task.Status.Message!.GetText());
            Assert.False(release.Task.IsCompleted);
        }
        finally
        {
            go.TrySetResult();
            events.ReleaseRead.TrySetResult();
            release.TrySetResult();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // A follow-up does not change the task's status. When the waiter fell behind and the store was trimmed, the stored task
    // still has the INPUT_REQUIRED from before the follow-up: that is not the follow-up's outcome until the status changes.
    // ---------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A task left INPUT_REQUIRED ("more?") by its first execution, and a follow-up execution that emits three artifacts (the
    /// store keeps two, the bus one), then waits for <paramref name="go"/>, asks "again?" and keeps running until
    /// <paramref name="release"/>. Returns the key, the sequence and the status timestamp at accept.
    /// </summary>
    private static async Task<(TaskKey Key, long AfterSequence, DateTimeOffset? AcceptedAt, A2A.Server.Execution.Execution FollowUp)> StartTrimmedFollowUpAsync(
        Rig rig, InMemoryTaskStore tasks, EventStore events, AgentRequestContext first)
    {
        var key = new TaskKey(first.Call.Tenant, first.Call.Principal, first.TaskId);
        var exec = await rig.Engine.StartForTestAsync(first);
        exec.Detach();
        await exec.Completion.WaitAsync(Wait);
        var afterSequence = await events.LastSequenceAsync(key); // 3: WORKING, INPUT_REQUIRED after the task
        var record = (await tasks.GetAsync(key))!;
        Assert.Equal(TaskState.InputRequired, record.Task.Status.State);
        var followUp = new AgentRequestContext
        {
            Message = Message.FromUser("more"), TaskId = first.TaskId, ContextId = first.ContextId, Call = first.Call,
            Task = record.Task.DeepCopy(),
        };
        var running = await rig.Engine.StartForTestAsync(followUp);
        await WaitForSequenceAsync(events, key, afterSequence + 3); // the three artifacts are stored (and the first one trimmed)
        return (key, afterSequence, record.Task.Status.Timestamp, running);
    }

    private static Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> TrimmedFollowUpAgent(TaskCompletionSource go, TaskCompletionSource release) =>
        async (context, u, ct) =>
        {
            if (context.IsNewTask)
            {
                await u.StartWorkAsync(cancellationToken: ct);
                await u.RequireInputAsync("more?", ct);
                return;
            }
            for (var i = 0; i < 3; i++) await u.AddArtifactAsync([Part.FromText("x")], artifactId: $"a{i}", cancellationToken: ct);
            await go.Task.WaitAsync(ct);
            await u.RequireInputAsync("again?", ct);
            await release.Task.WaitAsync(ct);
        };

    private static (A2AServerOptions Options, InMemoryEventStore Events, InMemoryTaskStore Tasks) TrimmedStores()
    {
        var options = new A2AServerOptions();
        options.AgentCard.Name = "Bounded Channel Test Agent";
        options.Streaming.ChannelCapacity = 1;
        options.Streaming.ReplayBufferSize = 2; // fewer than the follow-up's artifacts
        var events = new InMemoryEventStore(options.Streaming);
        return (options, events, new InMemoryTaskStore(events));
    }

    [Fact]
    public async Task Blocking_follow_up_wait_on_a_trimmed_store_ignores_the_status_from_before_the_follow_up()
    {
        var (options, events, tasks) = TrimmedStores();
        var go = Signal();
        var release = Signal();
        await using var rig = Rig.Create(options, tasks, events, TrimmedFollowUpAgent(go, release));
        var first = NewTaskContext();
        try
        {
            using var sub = rig.Bus.Subscribe(TaskExecutionEngine.ScopeKey(first.Call.Tenant, first.TaskId));
            var (key, afterSequence, acceptedAt, followUp) = await StartTrimmedFollowUpAsync(rig, tasks, events, first);

            // The bus kept only the last artifact: a gap, and the store no longer has the first event after afterSequence.
            var wait = rig.Streamer.WaitForOutcomeAsync(followUp, key, sub, afterSequence, acceptedAt, returnImmediately: false, CancellationToken.None);
            await Task.Delay(200); // negative check: the wait does not finish on the old status in this time
            Assert.False(wait.IsCompleted, "the INPUT_REQUIRED from before the follow-up was taken as its outcome");

            go.SetResult();
            Assert.Null(await wait.WaitAsync(Wait)); // the response is the task
            var task = (await tasks.GetAsync(key))!.Task;
            Assert.Equal("again?", task.Status.Message!.GetText());
            Assert.False(release.Task.IsCompleted); // still running: the new INPUT_REQUIRED ended the wait
        }
        finally
        {
            go.TrySetResult();
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Replay_follow_up_wait_on_a_trimmed_store_ignores_the_status_from_before_the_follow_up()
    {
        var (options, events, tasks) = TrimmedStores();
        var go = Signal();
        var release = Signal();
        await using var rig = Rig.Create(options, tasks, events, TrimmedFollowUpAgent(go, release));
        var first = NewTaskContext();
        try
        {
            var (key, afterSequence, acceptedAt, followUp) = await StartTrimmedFollowUpAsync(rig, tasks, events, first);
            followUp.Detach();

            var wait = rig.Engine.WaitUntilSettledAsync(key, afterSequence, acceptedAt, CancellationToken.None).AsTask();
            await Task.Delay(200); // negative check: the wait does not finish on the old status in this time
            Assert.False(wait.IsCompleted, "the INPUT_REQUIRED from before the follow-up was taken as its outcome");

            go.SetResult();
            var task = await wait.WaitAsync(Wait);
            Assert.Equal(TaskState.InputRequired, task.Status.State);
            Assert.Equal("again?", task.Status.Message!.GetText());
            Assert.False(release.Task.IsCompleted);
        }
        finally
        {
            go.TrySetResult();
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Send_stream_after_a_storage_failure_still_sends_every_stored_event()
    {
        // Capacity 1: while the reader stalls, the stream's own channel keeps one event and drops the rest. COMPLETED (the 7th
        // update) cannot be stored; the stream then sends every stored event (1..7) and the error, never the FAILED event.
        var tasks = new FailOnNthUpdateStore(failOn: 7);
        // The failing store is registered after the host's default store, so it wins.
        await using var failing = await TestAgentHost.StartAsync(o => o.Streaming.ChannelCapacity = 1, services: s =>
        {
            s.AddTaskStore(tasks);
            s.AddSingleton<IAgentExecutor>(new DelegateAgentExecutor(async (_, u, ct) =>
            {
                await u.StartWorkAsync(cancellationToken: ct);
                for (var i = 0; i < 5; i++) await u.AddArtifactAsync([Part.FromText("x")], artifactId: $"a{i}", cancellationToken: ct);
                await u.CompleteAsync(cancellationToken: ct);
            }));
        });

        await using var send = failing.Handler.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("go") }, Call())
            .GetAsyncEnumerator();
        Assert.True(await send.MoveNextAsync());
        var taskId = Assert.IsType<TaskEvent>(send.Current.Event).Task.Id;
        var sent = new List<long> { send.Current.Sequence };

        // Stall until the engine has marked the task FAILED (after the storage error).
        using (var timeout = new CancellationTokenSource(Wait))
            while ((await tasks.GetAsync(Key(taskId)))!.Task.Status.State != TaskState.Failed) await Task.Delay(5, timeout.Token);

        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
        {
            while (await send.MoveNextAsync()) sent.Add(send.Current.Sequence);
        });
        Assert.Equal(A2AErrorCode.Internal, ex.Code);
        AssertContiguous(sent, 1, 7); // the new task, WORKING, five artifacts; not the FAILED event (8)
    }

    private sealed class CaptureLogger : ILogger<TaskExecutionEngine>
    {
        public List<(LogLevel Level, int EventId)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, eventId.Id));
        }
    }

    [Fact]
    public async Task Agent_stopped_by_a_storage_failure_is_not_logged_as_failed()
    {
        // The second update fails. The engine stops reading and closes the agent's channel; the agent, which ignores
        // cancellation, gets ChannelClosedException on its next write. That is logged at Debug, not as an agent failure.
        var options = new A2AServerOptions();
        options.AgentCard.Name = "Bounded Channel Test Agent";
        options.Streaming.ChannelCapacity = 1;
        var tasks = new FailOnNthUpdateStore(failOn: 2);
        var logger = new CaptureLogger();
        var closed = Signal();
        await using var rig = Rig.Create(options, tasks, tasks.Events, async (_, u, _) =>
        {
            await u.StartWorkAsync(cancellationToken: CancellationToken.None);
            try
            {
                for (var i = 0; i < 100; i++)
                    await u.AddArtifactAsync([Part.FromText("x")], artifactId: $"a{i}", cancellationToken: CancellationToken.None);
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                closed.TrySetResult();
                throw;
            }
        }, logger);

        var exec = (await rig.Engine.StartForTestAsync(NewTaskContext()));
        exec.Detach();
        await exec.Completion.WaitAsync(Wait);

        Assert.True(closed.Task.IsCompleted);
        lock (logger.Entries)
        {
            Assert.DoesNotContain(logger.Entries, e => e.EventId == 200); // "Agent executor failed"
            Assert.Contains(logger.Entries, e => e.EventId == 206 && e.Level == LogLevel.Debug);
            Assert.Contains(logger.Entries, e => e.EventId == 203); // the storage failure itself is still an error
        }
    }

    private sealed class DelegateFactory(Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute) : IAgentExecutorFactory
    {
        public ValueTask<AgentExecutorLease> RentAsync(string tenant, CancellationToken ct) =>
            ValueTask.FromResult(new AgentExecutorLease(new DelegateAgentExecutor(execute), scope: null));
    }

}
