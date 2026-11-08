using System.Security.Claims;
using A2A.Server;
using A2A.Server.Execution;
using A2A.Server.Idempotency;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Microsoft.Extensions.DependencyInjection;

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
        options.Streaming.ChannelCapacity = 2;
        var tasks = new InMemoryTaskStore();
        var events = new InMemoryEventStore(options.Streaming);
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
        await using var engine = new TaskExecutionEngine(tasks, events, factory, new TaskEventBus(options.Streaming), new NoPush(),
            new IdempotencyCoordinator(new InMemoryIdempotencyStore(options.Idempotency), tasks, options),
            new PushConfigService(new InMemoryPushConfigStore(), new EphemeralPushSecretProtector(), options), options);
        var context = new AgentRequestContext
        {
            Message = Message.FromUser("go"), TaskId = TaskIds.New(), ContextId = TaskIds.New(), Call = Call(),
        };
        var key = new TaskKey(context.Call.Tenant, context.Call.Principal, context.TaskId);

        var exec = engine.Start(context, push: null, IdempotencyClaim.None);
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
            await Task.Delay(200);
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

    private sealed class DelegateFactory(Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute) : IAgentExecutorFactory
    {
        public ValueTask<AgentExecutorLease> RentAsync(CancellationToken ct) =>
            ValueTask.FromResult(new AgentExecutorLease(new DelegateAgentExecutor(execute), scope: null));
    }

    private sealed class NoPush : IPushNotificationSender
    {
        public void Enqueue(TaskKey task, SequencedEvent evt) { }
    }
}
