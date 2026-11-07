using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using A2A.Json;
using A2A.Server;
using A2A.Server.Coordination;
using A2A.Server.Push;
using A2A.Server.Retention;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace A2A.Tests;

/// <summary>
/// The retention sweep: purges expired terminal tasks with their data and orphan contexts, cancels tasks that waited too long
/// for input, and fails tasks whose execution died. The clock is a <see cref="FakeTimeProvider"/> that starts at the real
/// time (status timestamps use the real clock); while an execution runs, it moves in steps shorter than the lease TTL.
/// </summary>
public sealed class RetentionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatStep = new CoordinationOptions().Heartbeat;

    private static ServerCallContext Call(string user = "alice") => new()
    {
        Principal = user,
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user)], "Test")),
    };

    private static TaskKey Key(string taskId, string owner = "alice") => new("", owner, taskId);

    private static FakeTimeProvider NewClock() => new(DateTimeOffset.UtcNow);

    /// <summary>A host on <paramref name="clock"/> whose event logs outlive the test (so only the purge removes events).</summary>
    private static Task<TestAgentHost> StartAsync(FakeTimeProvider clock, Action<A2AServerOptions>? configure = null, Action<IServiceCollection>? services = null) =>
        TestAgentHost.StartAsync(o =>
        {
            o.Streaming.TerminalRetention = TimeSpan.FromDays(30);
            configure?.Invoke(o);
        }, services: s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            services?.Invoke(s);
        });

    /// <summary>A sweeper over the host's services (not hosted, so only the test runs sweeps).</summary>
    private static A2ARetentionService Sweeper(TestAgentHost host, Action<A2ARetentionOptions>? configure = null)
    {
        var options = new A2ARetentionOptions();
        configure?.Invoke(options);
        return ActivatorUtilities.CreateInstance<A2ARetentionService>(host.App.Services, Options.Create(options));
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting until {what}.");
            await Task.Delay(10);
        }
    }

    private static async Task<string> SendAsync(TestAgentHost host, string text, TaskPushNotificationConfig? push = null, string user = "alice")
    {
        var response = await host.Handler.SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser(text),
            Configuration = new SendMessageConfiguration { ReturnImmediately = true, TaskPushNotificationConfig = push },
        }, Call(user));
        return response.RequireTask().Id;
    }

    private static async Task<TaskState?> StateAsync(TestAgentHost host, TaskKey key) =>
        (await host.App.Services.GetRequiredService<TaskStore>().GetAsync(key))?.Task.Status.State;

    private static async Task<List<SequencedEvent>> StoredEventsAsync(TestAgentHost host, TaskKey key)
    {
        var events = new List<SequencedEvent>();
        await foreach (var e in host.App.Services.GetRequiredService<EventStore>().ReadAfterAsync(key, 0)) events.Add(e);
        return events;
    }

    /// <summary>
    /// A completed task with two push configs: deliveries to <c>/retry</c> fail with 500 (they stay in the outbox, due again
    /// later on the fake clock), deliveries to <c>/gone</c> fail with 400 (dead letters).
    /// </summary>
    private static async Task<TaskKey> CompletedTaskWithPushDataAsync(TestAgentHost host)
    {
        host.Webhooks.Status = r => r.RequestUri!.AbsolutePath switch
        {
            "/retry" => HttpStatusCode.InternalServerError,
            "/gone" => HttpStatusCode.BadRequest,
            _ => HttpStatusCode.OK,
        };
        var taskId = await SendAsync(host, "input", new TaskPushNotificationConfig { Url = "http://hooks.test/retry" });
        await host.Handler.WhenIdleAsync();
        await host.Handler.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = taskId, Url = "http://hooks.test/gone" }, Call());
        await host.Handler.SendMessageAsync(new SendMessageRequest { Message = new Message { MessageId = Message.NewId(), Role = Role.User, TaskId = taskId, Parts = [Part.FromText("finish")] } }, Call());
        await host.Handler.WhenIdleAsync();
        var key = Key(taskId);
        Assert.Equal(TaskState.Completed, await StateAsync(host, key));
        await EventuallyAsync(async () => (await host.PushOutbox.ListDeadLettersAsync("", 100)).Count > 0 && host.PushOutbox.Count > 0,
            "the task has dead letters and pending deliveries");
        return key;
    }

    private static async Task AssertPurgedAsync(TestAgentHost host, TaskKey key)
    {
        var sp = host.App.Services;
        Assert.Null(await sp.GetRequiredService<TaskStore>().GetAsync(key));
        Assert.Empty(await StoredEventsAsync(host, key));
        Assert.Equal(0, await sp.GetRequiredService<EventStore>().LastSequenceAsync(key));
        Assert.Empty(await sp.GetRequiredService<PushConfigStore>().ListAsync(key));
        Assert.Equal(0, host.PushOutbox.Count);
        Assert.Empty(await host.PushOutbox.ListDeadLettersAsync("", 100));
    }

    [Fact]
    public async Task Expired_terminal_task_is_purged_with_its_data()
    {
        var clock = NewClock();
        await using var host = await StartAsync(clock);
        var key = await CompletedTaskWithPushDataAsync(host);
        Assert.NotEmpty(await StoredEventsAsync(host, key));
        Assert.Equal(2, (await host.App.Services.GetRequiredService<PushConfigStore>().ListAsync(key)).Count);

        clock.Advance(TimeSpan.FromDays(8)); // past TaskRetention (7 days); nothing runs
        await Sweeper(host).RunOnceAsync(CancellationToken.None);

        await AssertPurgedAsync(host, key);
    }

    [Fact]
    public async Task Recent_and_running_tasks_are_kept()
    {
        var clock = NewClock();
        await using var host = await StartAsync(clock, o => o.InterruptedTimeout = TimeSpan.FromSeconds(30));
        var done = Key(await SendAsync(host, "task"));
        var running = Key(await SendAsync(host, "slow"));
        var waitingRunning = Key(await SendAsync(host, "auth")); // AUTH_REQUIRED while its agent still runs
        await EventuallyAsync(async () => await StateAsync(host, running) == TaskState.Working && await StateAsync(host, waitingRunning) == TaskState.AuthRequired,
            "the running tasks started");
        await EventuallyAsync(async () => await StateAsync(host, done) == TaskState.Completed, "the first task completed");

        // A minute in heartbeat steps: leases stay alive, the auth task waits longer than InterruptedTimeout.
        for (var i = 0; i < 6; i++) clock.Advance(HeartbeatStep);
        var sweeper = Sweeper(host, o => o.TaskRetention = TimeSpan.FromDays(7));
        await sweeper.RunOnceAsync(CancellationToken.None);

        Assert.Equal(TaskState.Completed, await StateAsync(host, done)); // not old enough
        Assert.NotEmpty(await StoredEventsAsync(host, done));
        Assert.Equal(TaskState.Working, await StateAsync(host, running)); // never failed while its lease lives
        Assert.Equal(TaskState.AuthRequired, await StateAsync(host, waitingRunning)); // interrupted but running: not cancelled
        var coordinator = host.App.Services.GetRequiredService<ExecutionCoordinator>();
        Assert.True(await coordinator.IsRunningAsync(running));
        Assert.Empty(await coordinator.ListExpiredAsync(10));
        ScenarioAgent.AuthSignals.GetOrAdd(waitingRunning.TaskId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    [Fact]
    public async Task Interrupted_task_times_out_to_canceled()
    {
        var clock = NewClock();
        await using var host = await StartAsync(clock);
        var taskId = await SendAsync(host, "input", new TaskPushNotificationConfig { Url = "http://hooks.test/hook" });
        await host.WaitIdleAsync();
        var key = Key(taskId);
        Assert.Equal(TaskState.InputRequired, await StateAsync(host, key));
        var before = host.Webhooks.Calls.Count;

        using var cts = new CancellationTokenSource(Wait);
        var received = new List<SequencedEvent>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriber = Task.Run(async () =>
        {
            await foreach (var e in host.Handler.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId }, Call()).WithCancellation(cts.Token))
            {
                received.Add(e);
                first.TrySetResult();
            }
        });
        await first.Task.WaitAsync(Wait);

        clock.Advance(TimeSpan.FromHours(23)); // not yet: InterruptedTimeout is 24 hours
        await Sweeper(host).RunOnceAsync(CancellationToken.None);
        Assert.Equal(TaskState.InputRequired, await StateAsync(host, key));

        clock.Advance(TimeSpan.FromHours(2));
        await Sweeper(host).RunOnceAsync(CancellationToken.None);

        await subscriber.WaitAsync(Wait); // the stream ends with the final event
        var status = Assert.IsType<StatusUpdateEvent>(received[^1].Event).StatusUpdate.Status;
        Assert.Equal(TaskState.Canceled, status.State);
        Assert.Equal("The task waited too long for input.", status.Message!.GetText());
        Assert.Equal(TaskState.Canceled, await StateAsync(host, key));
        Assert.Equal(received[^1].Sequence, (await StoredEventsAsync(host, key))[^1].Sequence);

        await host.WaitForPushIdleAsync();
        var hook = host.Webhooks.Calls.Skip(before).Select(c => A2AJson.Deserialize<StreamResponse>(c.Body)).OfType<StatusUpdateEvent>().Last();
        Assert.Equal(TaskState.Canceled, hook.StatusUpdate.Status.State);
        Assert.Equal("The task waited too long for input.", hook.StatusUpdate.Status.Message!.GetText());
    }

    [Fact]
    public async Task Interrupted_timeout_null_turns_the_timeout_off()
    {
        var clock = NewClock();
        await using var host = await StartAsync(clock, o => o.InterruptedTimeout = null);
        var key = Key(await SendAsync(host, "input"));
        await host.WaitIdleAsync();

        clock.Advance(TimeSpan.FromDays(3));
        await Sweeper(host).RunOnceAsync(CancellationToken.None);

        Assert.Equal(TaskState.InputRequired, await StateAsync(host, key));
    }

    /// <summary>
    /// Review Focus 1: instance A dies mid-execution (its heartbeats stop). Its lease expires, the sweep on instance B fails
    /// the task with "The agent stopped unexpectedly.", and a subscriber on B sees it.
    /// </summary>
    [Fact]
    public async Task Dead_execution_marks_task_failed()
    {
        var clock = NewClock();
        var events = new InMemoryEventStore(new StreamingOptions { TerminalRetention = TimeSpan.FromDays(30) }, clock);
        var outbox = new InMemoryPushOutbox(clock);
        var tasks = new InMemoryTaskStore(events, outbox);
        var configs = new InMemoryPushConfigStore();
        var shared = new InMemoryExecutionCoordinator(new CoordinationOptions(), clock);
        var dying = new DyingCoordinator(shared);
        void Shared(IServiceCollection s)
        {
            s.AddSingleton<TaskStore>(tasks);
            s.AddSingleton<EventStore>(events);
            s.AddSingleton<PushOutbox>(outbox);
            s.AddSingleton<PushConfigStore>(configs);
        }
        await using var a = await StartAsync(clock, services: s => { Shared(s); s.AddSingleton<ExecutionCoordinator>(dying); });
        await using var b = await StartAsync(clock, services: s => { Shared(s); s.AddSingleton<ExecutionCoordinator>(shared); });

        var taskId = await SendAsync(a, "slow");
        var key = Key(taskId);
        await EventuallyAsync(async () => await StateAsync(b, key) == TaskState.Working, "the agent on A is working");
        Assert.True(await shared.IsRunningAsync(key));

        using var cts = new CancellationTokenSource(Wait);
        var received = new List<SequencedEvent>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriber = Task.Run(async () =>
        {
            await foreach (var e in b.Handler.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId }, Call()).WithCancellation(cts.Token))
            {
                received.Add(e);
                first.TrySetResult();
            }
        });
        await first.Task.WaitAsync(Wait);

        dying.Die();
        var sweeper = Sweeper(b);
        clock.Advance(HeartbeatStep);
        await sweeper.RunOnceAsync(CancellationToken.None);
        Assert.Equal(TaskState.Working, await StateAsync(b, key)); // the lease has not expired yet

        clock.Advance(new CoordinationOptions().LeaseTtl); // deliberate expiry: no heartbeat renewed it
        Assert.False(await shared.IsRunningAsync(key));
        Assert.Single(await shared.ListExpiredAsync(10));
        await sweeper.RunOnceAsync(CancellationToken.None);

        await subscriber.WaitAsync(Wait);
        var status = Assert.IsType<StatusUpdateEvent>(received[^1].Event).StatusUpdate.Status;
        Assert.Equal(TaskState.Failed, status.State);
        Assert.Equal("The agent stopped unexpectedly.", status.Message!.GetText());
        Assert.Equal(TaskState.Failed, await StateAsync(b, key));
        Assert.Empty(await shared.ListExpiredAsync(10)); // the swept lease is gone
    }

    [Fact]
    public async Task Expired_lease_of_a_task_that_runs_elsewhere_is_only_unregistered()
    {
        var clock = NewClock();
        await using var host = await StartAsync(clock);
        var key = Key(await SendAsync(host, "slow"));
        await EventuallyAsync(async () => await StateAsync(host, key) == TaskState.Working, "the agent is working");
        var coordinator = host.App.Services.GetRequiredService<ExecutionCoordinator>();
        await coordinator.RegisterAsync(key, "ghost"); // an execution that died; the live one keeps heartbeating

        for (var i = 0; i < 4; i++) clock.Advance(HeartbeatStep); // 40 s: past the 30 s TTL of the ghost only
        Assert.Single(await coordinator.ListExpiredAsync(10));
        await Sweeper(host).RunOnceAsync(CancellationToken.None);

        Assert.Equal(TaskState.Working, await StateAsync(host, key));
        Assert.True(await coordinator.IsRunningAsync(key));
        Assert.Empty(await coordinator.ListExpiredAsync(10));
    }

    [Fact]
    public async Task System_status_commit_skips_a_task_that_changed()
    {
        var clock = NewClock();
        await using var host = await StartAsync(clock);
        var key = Key(await SendAsync(host, "input"));
        await host.WaitIdleAsync();
        var record = (await host.App.Services.GetRequiredService<TaskStore>().GetAsync(key))!;

        // Seen at an older version (a follow-up committed since): left alone.
        Assert.False(await host.Handler.CommitSystemStatusAsync(key, TaskState.Canceled, "late", record.Version - 1));
        Assert.Equal(TaskState.InputRequired, await StateAsync(host, key));

        Assert.True(await host.Handler.CommitSystemStatusAsync(key, TaskState.Canceled, "now", record.Version));
        Assert.Equal(TaskState.Canceled, await StateAsync(host, key));
        Assert.False(await host.Handler.CommitSystemStatusAsync(key, TaskState.Failed, "terminal already"));
    }

    [Fact]
    public async Task Orphan_contexts_are_removed()
    {
        var clock = NewClock();
        await using var host = await StartAsync(clock, o => o.InterruptedTimeout = null);
        var store = host.App.Services.GetRequiredService<TaskStore>();
        var done = Key(await SendAsync(host, "task"));
        var waiting = Key(await SendAsync(host, "input"));
        await host.WaitIdleAsync();
        var doneContext = (await store.GetAsync(done))!.Task.ContextId;
        var usedContext = (await store.GetAsync(waiting))!.Task.ContextId;

        clock.Advance(TimeSpan.FromDays(8));
        await store.GetOrCreateContextAsync(new ContextRecord("", "alice", "young", clock.GetUtcNow())); // no task, but new
        await Sweeper(host).RunOnceAsync(CancellationToken.None);

        Assert.Null(await store.GetAsync(done));
        Assert.Null(await store.GetContextAsync("", doneContext)); // its last task was purged: orphan, removed
        Assert.NotNull(await store.GetContextAsync("", usedContext)); // a task still uses it
        Assert.NotNull(await store.GetContextAsync("", "young")); // younger than OrphanContextRetention
    }

    [Fact]
    public async Task Only_one_instance_sweeps()
    {
        var clock = NewClock();
        await using var host = await StartAsync(clock);
        var key = Key(await SendAsync(host, "task"));
        await host.WaitIdleAsync();
        clock.Advance(TimeSpan.FromDays(8));

        var first = Sweeper(host);
        var second = Sweeper(host); // another instance: same coordinator
        Assert.True(await first.SweepAsync(CancellationToken.None));
        Assert.False(await second.SweepAsync(CancellationToken.None));
        Assert.False(await first.SweepAsync(CancellationToken.None)); // once per interval, also for the holder
        Assert.Null(await host.App.Services.GetRequiredService<TaskStore>().GetAsync(key));

        clock.Advance(new A2ARetentionOptions().SweepInterval);
        Assert.True(await second.SweepAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Admin_delete_removes_everything()
    {
        var clock = NewClock();
        await using var host = await StartAsync(clock);
        var key = await CompletedTaskWithPushDataAsync(host);
        var admin = host.App.Services.GetRequiredService<A2ATaskAdmin>();

        Assert.False(await admin.DeleteTaskAsync(key with { Owner = "bob" })); // another owner's key: missing, nothing deleted
        Assert.NotNull(await host.App.Services.GetRequiredService<TaskStore>().GetAsync(key));

        Assert.True(await admin.DeleteTaskAsync(key));
        await AssertPurgedAsync(host, key);
        Assert.False(await admin.DeleteTaskAsync(key));
    }

    [Fact]
    public async Task Admin_delete_cancels_a_running_task_first()
    {
        var clock = NewClock();
        await using var host = await StartAsync(clock);
        var key = Key(await SendAsync(host, "slow"));
        await EventuallyAsync(async () => await StateAsync(host, key) == TaskState.Working, "the agent is working");

        Assert.True(await host.App.Services.GetRequiredService<A2ATaskAdmin>().DeleteTaskAsync(key));

        await host.Handler.WhenIdleAsync().WaitAsync(Wait); // the agent was cancelled
        Assert.Null(await StateAsync(host, key));
        Assert.Empty(await StoredEventsAsync(host, key));
        Assert.False(await host.App.Services.GetRequiredService<ExecutionCoordinator>().IsRunningAsync(key));
    }

    [Fact]
    public async Task Purge_stopped_midway_is_finished_by_the_next_sweep()
    {
        var clock = NewClock();
        var configs = new FailOncePushConfigStore();
        await using var host = await StartAsync(clock, services: s => s.AddSingleton<PushConfigStore>(configs));
        var key = Key(await SendAsync(host, "task", new TaskPushNotificationConfig { Url = "http://hooks.test/hook" }));
        await host.WaitIdleAsync();
        clock.Advance(TimeSpan.FromDays(8));

        configs.FailNextDeleteAll = true;
        var sweeper = Sweeper(host);
        await sweeper.RunOnceAsync(CancellationToken.None); // logs the failure, does not throw
        Assert.NotNull(await host.App.Services.GetRequiredService<TaskStore>().GetAsync(key)); // the task row goes last

        await sweeper.RunOnceAsync(CancellationToken.None);
        await AssertPurgedAsync(host, key);
    }

    [Theory]
    [InlineData("SweepInterval")]
    [InlineData("BatchSizeLow")]
    [InlineData("BatchSizeHigh")]
    [InlineData("TaskRetention")]
    [InlineData("OrphanContextRetention")]
    [InlineData("InterruptedTimeout")]
    public async Task Invalid_options_are_rejected(string what)
    {
        var clock = NewClock();
        await using var host = await StartAsync(clock, o =>
        {
            if (what == "InterruptedTimeout") o.InterruptedTimeout = TimeSpan.Zero;
        });
        var sweeper = Sweeper(host, o =>
        {
            switch (what)
            {
                case "SweepInterval": o.SweepInterval = TimeSpan.Zero; break;
                case "BatchSizeLow": o.BatchSize = 0; break;
                case "BatchSizeHigh": o.BatchSize = 10_001; break;
                case "TaskRetention": o.TaskRetention = TimeSpan.Zero; break;
                case "OrphanContextRetention": o.OrphanContextRetention = TimeSpan.FromSeconds(-1); break;
            }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => sweeper.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Hosted_sweeper_sweeps_every_interval()
    {
        var clock = NewClock();
        await using var host = await StartAsync(clock, services: s => s.AddA2ARetention());
        var key = Key(await SendAsync(host, "task"));
        await host.WaitIdleAsync();

        clock.Advance(TimeSpan.FromDays(8)); // the interval timer fires once, eight days on; nothing runs
        await EventuallyAsync(async () => await StateAsync(host, key) is null, "the hosted sweep purged the expired task");
    }

    [Fact]
    public void AddA2ARetention_registers_a_hosted_sweeper_and_the_admin()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddA2AServer().AddAgentExecutor<ScenarioAgent>();
        services.AddA2ARetention(o => o.BatchSize = 7);
        using var sp = services.BuildServiceProvider();

        var sweeper = sp.GetRequiredService<A2ARetentionService>();
        Assert.Contains(sp.GetServices<IHostedService>(), h => ReferenceEquals(h, sweeper));
        Assert.Equal(7, sp.GetRequiredService<IOptions<A2ARetentionOptions>>().Value.BatchSize);
        Assert.NotNull(sp.GetRequiredService<A2ATaskAdmin>());
    }

    /// <summary>Delegates to a shared coordinator; after <see cref="Die"/> its heartbeats stop, like a dead instance's.</summary>
    private sealed class DyingCoordinator(ExecutionCoordinator inner) : ExecutionCoordinator
    {
        private volatile bool _dead;

        public void Die() => _dead = true;

        public override ValueTask RegisterAsync(TaskKey task, string executionId, CancellationToken ct = default) => inner.RegisterAsync(task, executionId, ct);
        public override ValueTask HeartbeatAsync(TaskKey task, string executionId, CancellationToken ct = default) =>
            _dead ? ValueTask.CompletedTask : inner.HeartbeatAsync(task, executionId, ct);
        public override ValueTask UnregisterAsync(TaskKey task, string executionId, CancellationToken ct = default) =>
            _dead ? ValueTask.CompletedTask : inner.UnregisterAsync(task, executionId, ct);
        public override ValueTask<bool> IsRunningAsync(TaskKey task, CancellationToken ct = default) => inner.IsRunningAsync(task, ct);
        public override ValueTask RequestCancelAsync(TaskKey task, CancellationToken ct = default) => inner.RequestCancelAsync(task, ct);
        public override async IAsyncEnumerable<TaskKey> CancelRequestsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var key in inner.CancelRequestsAsync(ct)) yield return key;
        }
        public override ValueTask<IReadOnlyList<(TaskKey Task, string ExecutionId)>> ListExpiredAsync(int max, CancellationToken ct = default) => inner.ListExpiredAsync(max, ct);
        public override ValueTask<bool> TryAcquireSweepLeaseAsync(string name, TimeSpan duration, CancellationToken ct = default) => inner.TryAcquireSweepLeaseAsync(name, duration, ct);
    }

    /// <summary>An in-memory push config store whose next <see cref="DeleteAllAsync"/> fails when asked to.</summary>
    private sealed class FailOncePushConfigStore : PushConfigStore
    {
        private readonly InMemoryPushConfigStore _inner = new();

        public bool FailNextDeleteAll { get; set; }

        public override ValueTask SaveAsync(PushConfigRecord record, CancellationToken ct = default) => _inner.SaveAsync(record, ct);
        public override ValueTask<bool> SaveIfUnderLimitAsync(PushConfigRecord record, int limit, CancellationToken ct = default) => _inner.SaveIfUnderLimitAsync(record, limit, ct);
        public override ValueTask<PushConfigRecord?> GetAsync(TaskKey task, string configId, CancellationToken ct = default) => _inner.GetAsync(task, configId, ct);
        public override ValueTask<IReadOnlyList<PushConfigRecord>> ListAsync(TaskKey task, CancellationToken ct = default) => _inner.ListAsync(task, ct);
        public override ValueTask<int> CountAsync(TaskKey task, CancellationToken ct = default) => _inner.CountAsync(task, ct);
        public override ValueTask<bool> DeleteAsync(TaskKey task, string configId, CancellationToken ct = default) => _inner.DeleteAsync(task, configId, ct);
        public override ValueTask DeleteAllAsync(TaskKey task, CancellationToken ct = default)
        {
            if (!FailNextDeleteAll) return _inner.DeleteAllAsync(task, ct);
            FailNextDeleteAll = false;
            throw new InvalidOperationException("push config store down");
        }
    }
}
