using System.Collections.Concurrent;
using A2A.Client;
using A2A.Server;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>
/// Task safety: <see cref="A2AServerOptions.MaxTaskDuration"/>, <see cref="A2AServerOptions.MaxTurnsPerTask"/> and
/// <see cref="A2AClientOptions.OnMessageReceived"/>.
/// </summary>
public class TaskSafetyTests
{
    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    public static TheoryData<string> AllBindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson, ProtocolBindings.Grpc };

    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(1);

    /// <summary>Real time for everything except timers with <see cref="Limit"/> as due time, which fire only on <see cref="FireAll"/>.</summary>
    private sealed class ManualLimitTimeProvider : TimeProvider
    {
        private readonly ConcurrentQueue<ManualTimer> _timers = new();

        public int Created => _timers.Count;

        public int Live => _timers.Count(t => !t.Disposed);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Limit) return System.CreateTimer(callback, state, dueTime, period);
            var timer = new ManualTimer(callback, state);
            _timers.Enqueue(timer);
            return timer;
        }

        /// <summary>Fires every timer that is not disposed.</summary>
        public void FireAll()
        {
            foreach (var t in _timers) t.Fire();
        }

        public async Task WaitForTimersAsync(int count)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Created < count) await Task.Delay(10, cts.Token);
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public volatile bool Disposed;

            public void Fire()
            {
                if (!Disposed) callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    private static Task<TestAgentHost> StartWithLimitAsync(ManualLimitTimeProvider time, bool grpc = false) =>
        TestAgentHost.StartAsync(o => o.MaxTaskDuration = Limit, services: s => s.AddSingleton<TimeProvider>(time), grpc: grpc);

    // ---- MaxTaskDuration ----

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Execution_over_time_limit_fails_task_for_stream_subscriber_and_push(string binding)
    {
        var time = new ManualLimitTimeProvider();
        await using var host = await StartWithLimitAsync(time);
        var client = host.Client(binding);
        var config = new SendMessageConfiguration
        {
            TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = $"http://hooks.test/limit-{binding}", Token = "t" },
        };

        var events = new List<StreamResponse>();
        await foreach (var evt in client.SendStreamingMessageAsync(Message.FromUser("slow"), config))
        {
            events.Add(evt);
            if (evt is StatusUpdateEvent { StatusUpdate.Status.State: TaskState.Working })
            {
                // A second stream on the same task sees the same final event.
                var taskId = ((StatusUpdateEvent)evt).StatusUpdate.TaskId;
                var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var subscriber = Task.Run(async () =>
                {
                    StreamResponse? last = null;
                    await foreach (var e in host.Client(binding).SubscribeToTaskAsync(taskId))
                    {
                        last = e;
                        connected.TrySetResult(); // the first event is the task snapshot: the subscriber is attached
                    }
                    return last;
                });
                await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
                time.FireAll();
                var lastSeen = await subscriber.WaitAsync(TimeSpan.FromSeconds(10));
                AssertTimedOut(Assert.IsType<StatusUpdateEvent>(lastSeen).StatusUpdate.Status);
            }
        }

        var final = Assert.IsType<StatusUpdateEvent>(events[^1]).StatusUpdate;
        AssertTimedOut(final.Status);
        Assert.DoesNotContain(events, e => e is StatusUpdateEvent { StatusUpdate.Status.State: TaskState.Canceled });

        await host.WaitIdleAsync();
        var stored = await client.GetTaskAsync(final.TaskId);
        AssertTimedOut(stored.Status);
        var calls = host.Webhooks.Calls.Where(c => c.Request.RequestUri!.AbsolutePath == $"/limit-{binding}").ToList();
        var lastPush = Json.A2AJson.Deserialize<StreamResponse>(calls[^1].Body);
        AssertTimedOut(Assert.IsType<StatusUpdateEvent>(lastPush).StatusUpdate.Status);
        Assert.Equal(0, time.Live); // the timer is disposed once the execution ends
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Blocking_send_returns_failed_task_when_time_limit_is_reached(string binding)
    {
        // Real clock: the default TimeProvider path.
        await using var host = await TestAgentHost.StartAsync(o => o.MaxTaskDuration = TimeSpan.FromMilliseconds(300));
        var task = (await host.Client(binding).SendMessageAsync("slow")).RequireTask();
        AssertTimedOut(task.Status);
        await host.WaitIdleAsync();
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Interrupted_task_without_running_execution_is_not_affected(string binding)
    {
        var time = new ManualLimitTimeProvider();
        await using var host = await StartWithLimitAsync(time);
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync("input")).RequireTask();
        Assert.Equal(TaskState.InputRequired, task.Status.State);
        await host.WaitIdleAsync();

        Assert.Equal(0, time.Live); // the timer of the finished execution is disposed, so firing it does nothing
        time.FireAll();
        Assert.Equal(TaskState.InputRequired, (await client.GetTaskAsync(task.Id)).Status.State);

        // A follow-up is a new execution with its own time limit.
        var done = (await client.SendMessageAsync("more", null, task.Id)).RequireTask();
        Assert.Equal(TaskState.Completed, done.Status.State);
        Assert.Equal(2, time.Created);
    }

    [Fact]
    public async Task Time_limit_is_off_by_default()
    {
        var time = new ManualLimitTimeProvider();
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddSingleton<TimeProvider>(time));
        Assert.Equal(TaskState.Completed, (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("task")).RequireTask().Status.State);
        Assert.Equal(0, time.Created);
    }

    [Theory]
    [InlineData(-1)] // TimeSpan.MaxValue
    [InlineData(60)]
    public async Task Time_limit_too_large_for_a_timer_turns_the_limit_off(int days)
    {
        var limit = days < 0 ? TimeSpan.MaxValue : TimeSpan.FromDays(days);
        await using var host = await TestAgentHost.StartAsync(o => o.MaxTaskDuration = limit);
        var client = host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { MaxRetries = 0 });

        var task = (await client.SendMessageAsync("task")).RequireTask();

        Assert.Equal(TaskState.Completed, task.Status.State);
        // The execution ended and left the running registry.
        await host.Handler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TaskState.Completed, (await client.GetTaskAsync(task.Id)).Status.State);
    }

    /// <summary>The time limit timer cannot be created; the heartbeat timers are tracked.</summary>
    private sealed class FailingLimitTimeProvider(TimeSpan heartbeat) : TimeProvider
    {
        public ConcurrentQueue<TrackedTimer> Heartbeats { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == Limit) throw new InvalidOperationException("No timer for the time limit.");
            var timer = System.CreateTimer(callback, state, dueTime, period);
            if (dueTime != heartbeat || period != heartbeat) return timer;
            var tracked = new TrackedTimer(timer);
            Heartbeats.Enqueue(tracked);
            return tracked;
        }

        public sealed class TrackedTimer(ITimer inner) : ITimer
        {
            public volatile bool Disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

            public void Dispose()
            {
                Disposed = true;
                inner.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return inner.DisposeAsync();
            }
        }
    }

    /// <summary>Rents <see cref="ScenarioAgent"/> with a scope that counts its releases.</summary>
    private sealed class CountingFactory : IAgentExecutorFactory, IAsyncDisposable
    {
        private int _rented;
        private int _released;

        public int Rented => Volatile.Read(ref _rented);

        public int Released => Volatile.Read(ref _released);

        public ValueTask<AgentExecutorLease> RentAsync(string tenant, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _rented);
            return ValueTask.FromResult(new AgentExecutorLease(new ScenarioAgent(), this));
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _released);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Failure_to_start_the_time_limit_cleans_up_the_execution()
    {
        var heartbeat = TimeSpan.FromSeconds(7);
        var time = new FailingLimitTimeProvider(heartbeat);
        var factory = new CountingFactory();
        await using var host = await TestAgentHost.StartAsync(o =>
        {
            o.MaxTaskDuration = Limit;
            o.Coordination.Heartbeat = heartbeat;
        }, services: s =>
        {
            s.AddSingleton<TimeProvider>(time);
            s.AddSingleton<IAgentExecutorFactory>(factory);
        });
        var client = host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { MaxRetries = 0 });

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("task"));
        Assert.Equal(A2AErrorCode.Internal, ex.Code);

        // Not running any more, the heartbeat stopped, and the agent's lease is released.
        await host.Handler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, factory.Rented);
        Assert.Equal(1, factory.Released);
        Assert.NotEmpty(time.Heartbeats);
        Assert.All(time.Heartbeats, t => Assert.True(t.Disposed));
    }

    private static void AssertTimedOut(AgentTaskStatus status)
    {
        Assert.Equal(TaskState.Failed, status.State);
        Assert.Equal("Task exceeded its time limit", status.Message!.GetText());
    }

    // ---- MaxTurnsPerTask ----

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Follow_up_over_turn_cap_is_rejected_before_the_agent_runs(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.MaxTurnsPerTask = 2);
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync("input")).RequireTask();
        task = (await client.SendMessageAsync("ask-again", null, task.Id)).RequireTask();
        Assert.Equal(TaskState.InputRequired, task.Status.State);

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("ask-again", null, task.Id));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
        Assert.Contains("limit of 2 messages", ex.Message);

        var streamed = await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var _ in client.SendStreamingMessageAsync(Message.FromUser("ask-again", taskId: task.Id))) { }
        });
        Assert.Equal(A2AErrorCode.UnsupportedOperation, streamed.Code);

        // The task did not change.
        await host.WaitIdleAsync();
        var stored = await client.GetTaskAsync(task.Id);
        Assert.Equal(TaskState.InputRequired, stored.Status.State);
        Assert.Equal(2, stored.History!.Count(m => m.Role == Role.User));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Turn_cap_of_one_allows_no_follow_ups(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.MaxTurnsPerTask = 1);
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync("input")).RequireTask();
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("more", null, task.Id));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
    }

    [Fact]
    public async Task Retry_of_an_accepted_turn_is_not_counted_again()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.MaxTurnsPerTask = 2);
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync("input")).RequireTask();
        var followUp = Message.FromUser("ask-again", taskId: task.Id);
        followUp.MessageId = "turn-2";
        await client.SendMessageAsync(followUp);

        var retry = Message.FromUser("ask-again", taskId: task.Id);
        retry.MessageId = "turn-2"; // same idempotency key: replayed, not a third turn
        var replayed = (await client.SendMessageAsync(retry)).RequireTask();
        Assert.Equal(task.Id, replayed.Id);
        Assert.Equal(TaskState.InputRequired, replayed.Status.State);
    }

    [Fact]
    public async Task Turn_cap_is_off_by_default()
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync("input")).RequireTask();
        for (var i = 0; i < 5; i++)
            Assert.Equal(TaskState.InputRequired, (await client.SendMessageAsync("ask-again", null, task.Id)).RequireTask().Status.State);
    }

    // ---- OnMessageReceived ----

    private static A2AClientOptions Recording(ConcurrentQueue<ReceivedMessage> seen, Func<ReceivedMessage, bool>? abortWhen = null) => new()
    {
        OnMessageReceived = (m, _) =>
        {
            seen.Enqueue(m);
            if (abortWhen?.Invoke(m) == true) throw new InvalidOperationException("turn refused");
            return ValueTask.CompletedTask;
        },
    };

    [Theory]
    [MemberData(nameof(AllBindings))]
    public async Task Hook_sees_direct_replies_and_task_status_messages(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(grpc: true);
        var seen = new ConcurrentQueue<ReceivedMessage>();
        var client = host.Client(binding, options: Recording(seen));

        await client.SendMessageAsync("hello");
        var task = (await client.SendMessageAsync("task")).RequireTask();
        await client.GetTaskAsync(task.Id);

        Assert.Collection(seen,
            m => { Assert.Equal(ReceivedMessageSource.DirectReply, m.Source); Assert.Equal("hi there", m.Message.GetText()); },
            m => { Assert.Equal(ReceivedMessageSource.TaskSnapshot, m.Source); Assert.Equal("done", m.Message.GetText()); Assert.Equal(task.Id, m.TaskId); },
            m => { Assert.Equal(ReceivedMessageSource.TaskSnapshot, m.Source); Assert.Equal("done", m.Message.GetText()); });
    }

    [Theory]
    [MemberData(nameof(AllBindings))]
    public async Task Hook_sees_streamed_status_messages_before_the_caller(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(grpc: true);
        var seen = new ConcurrentQueue<ReceivedMessage>();
        var client = host.Client(binding, options: Recording(seen));

        await foreach (var evt in client.SendStreamingMessageAsync(Message.FromUser("task")))
        {
            if (evt is StatusUpdateEvent { StatusUpdate.Status.Message: { } m })
                Assert.Contains(seen, r => ReferenceEquals(r.Message, m)); // already checked when the caller gets it
        }

        var updates = seen.Where(r => r.Source == ReceivedMessageSource.StatusUpdate).Select(r => r.Message.GetText()).ToList();
        Assert.Equal(["working", "done"], updates);

        // Direct reply over a stream.
        await foreach (var _ in client.SendStreamingMessageAsync(Message.FromUser("hello"))) { }
        Assert.Equal(ReceivedMessageSource.DirectReply, seen.Last().Source);
    }

    [Theory]
    [MemberData(nameof(AllBindings))]
    public async Task Hook_exception_aborts_the_call(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(grpc: true);
        var seen = new ConcurrentQueue<ReceivedMessage>();
        var client = host.Client(binding, options: Recording(seen, m => m.Message.GetText() == "working"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var evt in client.SendStreamingMessageAsync(Message.FromUser("task")))
                Assert.False(evt is StatusUpdateEvent { StatusUpdate.Status.Message: var m } && m?.GetText() == "working", "the refused event reached the caller");
        });
        Assert.Equal("turn refused", ex.Message);

        var unary = host.Client(binding, options: Recording(seen, m => m.Source == ReceivedMessageSource.DirectReply));
        await Assert.ThrowsAsync<InvalidOperationException>(() => unary.SendMessageAsync("hello"));
        await host.WaitIdleAsync();
    }
}
