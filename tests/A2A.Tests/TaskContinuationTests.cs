using System.Security.Claims;
using System.Text.Json;
using A2A.Diagnostics;
using A2A.Server;
using A2A.Server.Coordination;
using A2A.Server.Execution;
using A2A.Server.Retention;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace A2A.Tests;

/// <summary>
/// Proactive continuation: <see cref="TaskExecutionEngine.ContinueTaskAsync"/> runs a <see cref="IContinuableAgentExecutor"/>
/// on an existing task with no inbound request. Tests seed a task through a normal SendMessage, then continue it.
/// </summary>
public sealed class TaskContinuationTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static ServerCallContext Call(string user = "alice") => new()
    {
        Principal = user,
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user)], "Test")),
    };

    private static TaskKey Key(string taskId, string owner = "alice", string tenant = "") => new(tenant, owner, taskId);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Seeds tasks by the first word of the message, and continues them with <see cref="OnContinue"/>. The default
    /// continuation works, adds an artifact and completes the task.
    /// </summary>
    private sealed class TestAgent : IContinuableAgentExecutor
    {
        private int _continues;
        private int _executes;

        public int Continues => Volatile.Read(ref _continues);

        public int Executes => Volatile.Read(ref _executes);

        public TaskCompletionSource ExecuteGate { get; } = NewSignal();

        public Func<TaskContinuationContext, TaskUpdater, CancellationToken, Task> OnContinue { get; set; } = async (_, updater, ct) =>
        {
            await updater.StartWorkAsync("resuming", ct);
            await updater.AddArtifactAsync([Part.FromText("late")], name: "late", cancellationToken: ct);
            await updater.CompleteAsync("finished", ct);
        };

        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executes);
            switch (context.UserText)
            {
                case "submitted": await updater.SubmitAsync(cancellationToken); break;
                case "working": await updater.StartWorkAsync(cancellationToken: cancellationToken); break;
                case "input": await updater.RequireInputAsync("need more", cancellationToken); break;
                case "auth": await updater.RequireAuthAsync("please authorize", cancellationToken); break;
                case "fail": await updater.FailAsync("failed", cancellationToken); break;
                case "reject": await updater.RejectAsync("rejected", cancellationToken); break;
                case "cancel": await updater.CancelAsync("canceled", cancellationToken); break;
                case "gate":
                    await updater.StartWorkAsync(cancellationToken: cancellationToken);
                    await ExecuteGate.Task.WaitAsync(cancellationToken);
                    await updater.CompleteAsync("done", cancellationToken);
                    break;
                default: await updater.CompleteAsync("done", cancellationToken); break;
            }
        }

        public Task ContinueAsync(TaskContinuationContext context, TaskUpdater updater, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _continues);
            return OnContinue(context, updater, cancellationToken);
        }
    }

    /// <summary>Rents <see cref="Executor"/>, or fails with <see cref="Failure"/>.</summary>
    private sealed class FixedFactory(IAgentExecutor? executor) : IAgentExecutorFactory
    {
        public IAgentExecutor? Executor { get; set; } = executor;

        public Exception? Failure { get; set; }

        public ValueTask<AgentExecutorLease> RentAsync(string tenant, CancellationToken cancellationToken) =>
            Failure is not null ? throw Failure : ValueTask.FromResult(new AgentExecutorLease(Executor, null));
    }

    private static Task<TestAgentHost> StartAsync(FixedFactory factory, FakeTimeProvider? clock = null, Action<A2AServerOptions>? configure = null,
        Action<IServiceCollection>? services = null) =>
        TestAgentHost.StartAsync(configure, services: s =>
        {
            if (clock is not null) s.AddSingleton<TimeProvider>(clock);
            s.AddSingleton<IAgentExecutorFactory>(factory);
            services?.Invoke(s);
        });

    private static async Task<TaskKey> SeedAsync(TestAgentHost host, string text)
    {
        var response = await host.Handler.SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser(text),
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        }, Call());
        await host.Handler.WhenIdleAsync().WaitAsync(Wait);
        return Key(response.RequireTask().Id);
    }

    private static async Task<List<SequencedEvent>> StoredEventsAsync(TestAgentHost host, TaskKey key)
    {
        var events = new List<SequencedEvent>();
        await foreach (var e in host.App.Services.GetRequiredService<EventStore>().ReadAfterAsync(key, 0)) events.Add(e);
        return events;
    }

    private static async Task<long> LastSequenceAsync(TestAgentHost host, TaskKey key) =>
        (await StoredEventsAsync(host, key)).Select(e => e.Sequence).LastOrDefault();

    private static async Task<AgentTask> TaskAsync(TestAgentHost host, TaskKey key) =>
        (await host.App.Services.GetRequiredService<TaskStore>().GetAsync(key))!.Task;

    /// <summary>The task's version, last event sequence and state: equal before and after a refused continuation.</summary>
    private static async Task<(long Version, long Sequence, TaskState State)> SnapshotAsync(TestAgentHost host, TaskKey key)
    {
        var record = (await host.App.Services.GetRequiredService<TaskStore>().GetAsync(key))!;
        return (record.Version, await LastSequenceAsync(host, key), record.Task.Status.State);
    }

    private static string Describe(SequencedEvent e) => e.Event switch
    {
        StatusUpdateEvent s => s.StatusUpdate.Status.State.ToString(),
        ArtifactUpdateEvent => "artifact",
        _ => e.Event.GetType().Name,
    };

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting until {what}.");
            await Task.Delay(10);
        }
    }

    private static A2ARetentionService Sweeper(TestAgentHost host) =>
        ActivatorUtilities.CreateInstance<A2ARetentionService>(host.App.Services, Options.Create(new A2ARetentionOptions()));

    private static ValueTask<TaskContinuationResult> ContinueAsync(TestAgentHost host, TaskKey key, Dictionary<string, JsonElement>? metadata = null,
        CancellationToken ct = default) => host.Engine.ContinueTaskAsync(key, metadata, ct);

    // ---------------------------------------------------------------- the run

    [Fact]
    public async Task Continuation_needs_no_request_and_no_push_config()
    {
        var agent = new TestAgent();
        await using var host = await StartAsync(new FixedFactory(agent));
        var key = await SeedAsync(host, "input");
        var before = await LastSequenceAsync(host, key);

        var result = await ContinueAsync(host, key);
        Assert.Equal(TaskContinuationStatus.Started, result.Status);
        Assert.True(result.Started);
        await result.Completion.WaitAsync(Wait);

        Assert.Equal(TaskState.Completed, (await TaskAsync(host, key)).Status.State);
        var added = (await StoredEventsAsync(host, key)).Where(e => e.Sequence > before).Select(Describe);
        Assert.Equal(["Working", "artifact", "Completed"], added);
        Assert.Equal(0, host.PushOutbox.Count);
    }

    [Fact]
    public async Task Continuation_reaches_a_live_subscriber()
    {
        var agent = new TestAgent();
        await using var host = await StartAsync(new FixedFactory(agent));
        var key = await SeedAsync(host, "input");
        var before = await LastSequenceAsync(host, key);

        var snapshot = NewSignal(); // a fresh subscribe sends the task first, once it listens to the bus
        var received = new List<SequencedEvent>();
        var subscriber = Task.Run(async () =>
        {
            await foreach (var e in host.Handler.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = key.TaskId }, Call()))
            {
                received.Add(e);
                snapshot.TrySetResult();
            }
        });
        await snapshot.Task.WaitAsync(Wait);

        var result = await ContinueAsync(host, key);
        Assert.True(result.Started);
        await subscriber.WaitAsync(Wait); // the stream ends with the terminal event

        Assert.Equal(["Working", "artifact", "Completed"], received.Where(e => e.Sequence > before).Select(Describe));
    }

    [Fact]
    public async Task Continuation_reaches_a_resumed_stream()
    {
        var agent = new TestAgent();
        var gate = NewSignal();
        agent.OnContinue = async (_, updater, ct) =>
        {
            await updater.StartWorkAsync("resuming", ct);
            await updater.AddArtifactAsync([Part.FromText("late")], name: "late", cancellationToken: ct);
            await gate.Task.WaitAsync(ct);
            await updater.CompleteAsync("finished", ct);
        };
        await using var host = await StartAsync(new FixedFactory(agent));
        var key = await SeedAsync(host, "input");
        var before = await LastSequenceAsync(host, key);

        var result = await ContinueAsync(host, key);
        await EventuallyAsync(async () => await LastSequenceAsync(host, key) == before + 2, "the continuation stored two events");

        var received = new List<SequencedEvent>();
        var subscriber = Task.Run(async () =>
        {
            await foreach (var e in host.Handler.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = key.TaskId }, Call(), resumeAfter: before))
                received.Add(e);
        });
        gate.TrySetResult();
        await subscriber.WaitAsync(Wait);
        await result.Completion.WaitAsync(Wait);

        Assert.Equal([before + 1, before + 2, before + 3], received.Select(e => e.Sequence));
        Assert.Equal(["Working", "artifact", "Completed"], received.Select(Describe));
    }

    [Theory]
    [InlineData("submitted")]
    [InlineData("working")]
    [InlineData("input")]
    [InlineData("auth")]
    public async Task Continuation_runs_from_every_open_state(string seed)
    {
        var agent = new TestAgent();
        await using var host = await StartAsync(new FixedFactory(agent));
        var key = await SeedAsync(host, seed);
        Assert.False((await TaskAsync(host, key)).Status.State.IsTerminal());

        var result = await ContinueAsync(host, key);
        Assert.Equal(TaskContinuationStatus.Started, result.Status);
        await result.Completion.WaitAsync(Wait);

        Assert.Equal(TaskState.Completed, (await TaskAsync(host, key)).Status.State);
        Assert.Equal(1, agent.Continues);
    }

    [Fact]
    public async Task Push_is_one_subscriber_of_a_continuation()
    {
        var agent = new TestAgent();
        await using var host = await StartAsync(new FixedFactory(agent));
        var key = await SeedAsync(host, "input");
        await host.Handler.CreateTaskPushNotificationConfigAsync(
            new TaskPushNotificationConfig { TaskId = key.TaskId, Url = "http://hooks.test/hook" }, Call());
        var before = host.Webhooks.Calls.Count;

        var result = await ContinueAsync(host, key);
        await result.Completion.WaitAsync(Wait);
        await host.WaitForPushIdleAsync();

        Assert.Equal(3, host.Webhooks.Calls.Count - before); // working, artifact, completed
    }

    [Fact]
    public async Task Agent_gets_a_copy_of_the_task_and_the_metadata()
    {
        var agent = new TestAgent();
        string? seenContext = null;
        Dictionary<string, JsonElement>? seenMetadata = null;
        string? updaterValue = null;
        AgentTask? agentTask = null;
        agent.OnContinue = async (context, updater, ct) =>
        {
            seenContext = context.Task.ContextId;
            seenMetadata = context.Metadata;
            updaterValue = updater.RequestMetadata?["k"].GetString();
            agentTask = context.Task;
            context.Task.ContextId = "changed-by-the-agent";
            context.Task.Status.State = TaskState.Failed;
            await updater.CompleteAsync("finished", ct);
        };
        await using var host = await StartAsync(new FixedFactory(agent));
        var key = await SeedAsync(host, "input");
        var stored = await TaskAsync(host, key);
        var metadata = new Dictionary<string, JsonElement> { ["k"] = JsonSerializer.SerializeToElement("v") };

        var result = await ContinueAsync(host, key, metadata);
        await result.Completion.WaitAsync(Wait);

        Assert.Equal(stored.ContextId, seenContext);
        Assert.Equal("v", seenMetadata!["k"].GetString());
        Assert.NotSame(metadata, seenMetadata);
        Assert.Equal("v", updaterValue);
        Assert.NotSame(stored, agentTask);
        var after = await TaskAsync(host, key);
        Assert.Equal(stored.ContextId, after.ContextId);
        Assert.Equal(TaskState.Completed, after.Status.State);
    }

    // ---------------------------------------------------------------- refusals

    [Theory]
    [InlineData("bob", "")]
    [InlineData("alice", "other-tenant")]
    public async Task Other_owner_or_tenant_reads_as_not_found(string owner, string tenant)
    {
        var agent = new TestAgent();
        await using var host = await StartAsync(new FixedFactory(agent));
        var key = await SeedAsync(host, "input");
        var before = await SnapshotAsync(host, key);

        var result = await ContinueAsync(host, key with { Owner = owner, Tenant = tenant });

        Assert.Equal(TaskContinuationStatus.TaskNotFound, result.Status);
        Assert.False(result.Started);
        Assert.True(result.Completion.IsCompletedSuccessfully);
        Assert.Equal(0, agent.Continues);
        Assert.Equal(before, await SnapshotAsync(host, key));
    }

    [Fact]
    public async Task Missing_task_is_not_found()
    {
        var agent = new TestAgent();
        await using var host = await StartAsync(new FixedFactory(agent));

        var result = await ContinueAsync(host, Key("no-such-task"));

        Assert.Equal(TaskContinuationStatus.TaskNotFound, result.Status);
        Assert.Equal(0, agent.Continues);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("fail")]
    [InlineData("reject")]
    [InlineData("cancel")]
    public async Task Terminal_task_is_refused_and_unchanged(string seed)
    {
        var agent = new TestAgent();
        await using var host = await StartAsync(new FixedFactory(agent));
        var key = await SeedAsync(host, seed);
        var before = await SnapshotAsync(host, key);
        Assert.True(before.State.IsTerminal());

        var result = await ContinueAsync(host, key);

        Assert.Equal(TaskContinuationStatus.TaskTerminal, result.Status);
        Assert.Equal(0, agent.Continues);
        Assert.Equal(before, await SnapshotAsync(host, key));
    }

    [Fact]
    public async Task Overdue_interrupted_task_is_refused_and_left_for_the_sweep()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var agent = new TestAgent();
        await using var host = await StartAsync(new FixedFactory(agent), clock, o => o.InterruptedTimeout = TimeSpan.FromHours(1));
        var key = await SeedAsync(host, "input");
        var before = await SnapshotAsync(host, key);

        clock.Advance(TimeSpan.FromHours(2));
        var result = await ContinueAsync(host, key);

        Assert.Equal(TaskContinuationStatus.InterruptedTimedOut, result.Status);
        Assert.Equal(0, agent.Continues);
        Assert.Equal(before, await SnapshotAsync(host, key));

        await Sweeper(host).RunOnceAsync(CancellationToken.None);
        Assert.Equal(TaskState.Canceled, (await TaskAsync(host, key)).Status.State);
    }

    [Fact]
    public async Task Interrupted_task_within_its_timeout_continues()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var agent = new TestAgent();
        await using var host = await StartAsync(new FixedFactory(agent), clock, o => o.InterruptedTimeout = TimeSpan.FromHours(1));
        var key = await SeedAsync(host, "input");

        clock.Advance(TimeSpan.FromMinutes(30));
        var result = await ContinueAsync(host, key);

        Assert.Equal(TaskContinuationStatus.Started, result.Status);
        await result.Completion.WaitAsync(Wait);
        Assert.Equal(TaskState.Completed, (await TaskAsync(host, key)).Status.State);
    }

    [Fact]
    public async Task No_interrupted_timeout_never_refuses()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var agent = new TestAgent();
        await using var host = await StartAsync(new FixedFactory(agent), clock, o => o.InterruptedTimeout = null);
        var key = await SeedAsync(host, "input");

        clock.Advance(TimeSpan.FromDays(365));
        var result = await ContinueAsync(host, key);

        Assert.Equal(TaskContinuationStatus.Started, result.Status);
        await result.Completion.WaitAsync(Wait);
        Assert.Equal(TaskState.Completed, (await TaskAsync(host, key)).Status.State);
    }

    [Fact]
    public async Task Running_execution_refuses_a_continuation()
    {
        var agent = new TestAgent();
        await using var host = await StartAsync(new FixedFactory(agent));
        var response = await host.Handler.SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser("gate"),
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        }, Call());
        var key = Key(response.RequireTask().Id);
        await EventuallyAsync(async () => (await TaskAsync(host, key)).Status.State == TaskState.Working, "the task is working");

        var result = await ContinueAsync(host, key);

        Assert.Equal(TaskContinuationStatus.AlreadyRunning, result.Status);
        Assert.Equal(0, agent.Continues);
        agent.ExecuteGate.TrySetResult();
        await host.Handler.WhenIdleAsync().WaitAsync(Wait);
    }

    [Fact]
    public async Task Second_continuation_during_a_run_is_refused()
    {
        var agent = new TestAgent();
        var entered = NewSignal();
        var gate = NewSignal();
        agent.OnContinue = async (_, updater, ct) =>
        {
            await updater.StartWorkAsync("resuming", ct);
            entered.TrySetResult();
            await gate.Task.WaitAsync(ct);
            await updater.CompleteAsync("finished", ct);
        };
        await using var host = await StartAsync(new FixedFactory(agent));
        var key = await SeedAsync(host, "input");

        var first = await ContinueAsync(host, key);
        await entered.Task.WaitAsync(Wait);
        var second = await ContinueAsync(host, key);
        gate.TrySetResult();
        await first.Completion.WaitAsync(Wait);

        Assert.True(first.Started);
        Assert.Equal(TaskContinuationStatus.AlreadyRunning, second.Status);
        Assert.Equal(1, agent.Continues);
        Assert.Equal(TaskState.Completed, (await TaskAsync(host, key)).Status.State);
        Assert.Contains("Completed", (await StoredEventsAsync(host, key)).Select(Describe));
    }

    [Fact]
    public async Task Continuation_after_the_previous_one_ended_starts()
    {
        var agent = new TestAgent();
        agent.OnContinue = async (_, updater, ct) =>
        {
            await updater.StartWorkAsync("resuming", ct);
            await updater.RequireInputAsync("still need more", ct);
        };
        await using var host = await StartAsync(new FixedFactory(agent));
        var key = await SeedAsync(host, "input");

        var first = await ContinueAsync(host, key);
        await first.Completion.WaitAsync(Wait);
        var second = await ContinueAsync(host, key);
        await second.Completion.WaitAsync(Wait);

        Assert.True(first.Started);
        Assert.True(second.Started);
        Assert.Equal(2, agent.Continues);
    }

    [Fact]
    public async Task Lease_held_during_the_run_and_released_after()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var agent = new TestAgent();
        var entered = NewSignal();
        var gate = NewSignal();
        agent.OnContinue = async (_, updater, ct) =>
        {
            await updater.StartWorkAsync("resuming", ct);
            entered.TrySetResult();
            await gate.Task.WaitAsync(ct);
            await updater.CompleteAsync("finished", ct);
        };
        await using var host = await StartAsync(new FixedFactory(agent), clock);
        var key = await SeedAsync(host, "input");
        var coordinator = host.App.Services.GetRequiredService<ExecutionCoordinator>();
        Assert.False(await coordinator.IsRunningAsync(key));

        var result = await ContinueAsync(host, key);
        await entered.Task.WaitAsync(Wait);
        Assert.True(await coordinator.IsRunningAsync(key));
        gate.TrySetResult();
        await result.Completion.WaitAsync(Wait);

        Assert.False(await coordinator.IsRunningAsync(key));
        clock.Advance(new CoordinationOptions().LeaseTtl + TimeSpan.FromSeconds(1));
        Assert.Empty(await coordinator.ListExpiredAsync(10));
    }

    [Fact]
    public async Task Agent_that_cannot_continue_is_not_supported_and_task_unchanged()
    {
        await using var host = await StartAsync(new FixedFactory(new ScenarioAgent()));
        var key = await SeedAsync(host, "input");
        var before = await SnapshotAsync(host, key);

        var result = await ContinueAsync(host, key);

        Assert.Equal(TaskContinuationStatus.NotSupported, result.Status);
        Assert.Equal(before, await SnapshotAsync(host, key));
    }

    [Fact]
    public async Task Tenant_without_an_agent_is_not_supported()
    {
        var agent = new TestAgent();
        var factory = new FixedFactory(agent);
        await using var host = await StartAsync(factory);
        var key = await SeedAsync(host, "input");
        var before = await SnapshotAsync(host, key);
        factory.Executor = null; // the factory now returns a default lease

        var result = await ContinueAsync(host, key);

        Assert.Equal(TaskContinuationStatus.NotSupported, result.Status);
        Assert.Equal(0, agent.Continues);
        Assert.Equal(before, await SnapshotAsync(host, key));
    }

    [Fact]
    public async Task Failing_factory_throws_and_task_unchanged()
    {
        var agent = new TestAgent();
        var factory = new FixedFactory(agent);
        await using var host = await StartAsync(factory);
        var key = await SeedAsync(host, "input");
        var before = await SnapshotAsync(host, key);
        factory.Failure = new InvalidOperationException("factory down");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ContinueAsync(host, key));

        Assert.Equal("factory down", ex.Message);
        Assert.Equal(0, agent.Continues);
        Assert.Equal(before, await SnapshotAsync(host, key));
    }

    [Fact]
    public async Task Coordinator_failure_starts_nothing()
    {
        var agent = new TestAgent();
        var coordinator = new FaultyCoordinator();
        await using var host = await StartAsync(new FixedFactory(agent), services: s => s.AddSingleton<ExecutionCoordinator>(coordinator));
        var key = await SeedAsync(host, "input");
        var before = await SnapshotAsync(host, key);
        coordinator.FailNextRegisters(1);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await ContinueAsync(host, key));

        Assert.Equal(0, agent.Continues);
        Assert.Equal(before, await SnapshotAsync(host, key));
        Assert.False(await coordinator.IsRunningAsync(key));
    }

    // ---------------------------------------------------------------- during and after the run

    [Fact]
    public async Task Agent_failure_fails_the_task()
    {
        var agent = new TestAgent();
        agent.OnContinue = (_, _, _) => throw new InvalidOperationException("boom");
        await using var host = await StartAsync(new FixedFactory(agent));
        var key = await SeedAsync(host, "input");

        var result = await ContinueAsync(host, key);
        await result.Completion.WaitAsync(Wait);

        var task = await TaskAsync(host, key);
        Assert.Equal(TaskState.Failed, task.Status.State);
        Assert.Equal("The agent failed to process the request.", task.Status.Message!.GetText());
    }

    [Fact]
    public async Task CancelTask_stops_a_continuation()
    {
        var agent = new TestAgent();
        var entered = NewSignal();
        var tokenFired = NewSignal();
        agent.OnContinue = async (_, updater, ct) =>
        {
            ct.Register(() => tokenFired.TrySetResult());
            await updater.StartWorkAsync("resuming", ct);
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        await using var host = await StartAsync(new FixedFactory(agent));
        var key = await SeedAsync(host, "input");

        var result = await ContinueAsync(host, key);
        await entered.Task.WaitAsync(Wait);
        var canceled = await host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = key.TaskId }, Call());
        await tokenFired.Task.WaitAsync(Wait);
        await result.Completion.WaitAsync(Wait);

        Assert.Equal(TaskState.Canceled, canceled.Status.State);
        Assert.Equal(TaskState.Canceled, (await TaskAsync(host, key)).Status.State);
    }

    [Fact]
    public async Task Events_after_the_task_ended_are_dropped()
    {
        var agent = new TestAgent();
        var entered = NewSignal();
        var release = NewSignal();
        agent.OnContinue = async (_, updater, _) =>
        {
            await updater.StartWorkAsync("resuming", CancellationToken.None);
            entered.TrySetResult();
            await release.Task; // ignores its token
            try { await updater.AddArtifactAsync([Part.FromText("too late")], name: "late", cancellationToken: CancellationToken.None); }
            catch (Exception) { /* the run was cancelled: the write may fail */ }
        };
        await using var host = await StartAsync(new FixedFactory(agent));
        var key = await SeedAsync(host, "input");

        var result = await ContinueAsync(host, key);
        await entered.Task.WaitAsync(Wait);
        await host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = key.TaskId }, Call());
        var ended = await SnapshotAsync(host, key);
        Assert.Equal(TaskState.Canceled, ended.State);
        release.TrySetResult();
        await result.Completion.WaitAsync(Wait);

        Assert.Equal(ended, await SnapshotAsync(host, key));
    }

    [Fact]
    public async Task Start_token_only_cancels_the_start()
    {
        var agent = new TestAgent();
        var entered = NewSignal();
        var gate = NewSignal();
        var tokenFiredAtEnd = true;
        agent.OnContinue = async (_, updater, ct) =>
        {
            await updater.StartWorkAsync("resuming", ct);
            entered.TrySetResult();
            await gate.Task.WaitAsync(Wait);
            tokenFiredAtEnd = ct.IsCancellationRequested;
            await updater.CompleteAsync("finished", CancellationToken.None);
        };
        await using var host = await StartAsync(new FixedFactory(agent));
        var key = await SeedAsync(host, "input");

        using var cts = new CancellationTokenSource();
        var result = await ContinueAsync(host, key, ct: cts.Token);
        await entered.Task.WaitAsync(Wait);
        await cts.CancelAsync();
        gate.TrySetResult();
        await result.Completion.WaitAsync(Wait);

        Assert.False(tokenFiredAtEnd);
        Assert.Equal(TaskState.Completed, (await TaskAsync(host, key)).Status.State);
    }

    [Fact]
    public async Task Cancelled_start_token_throws_and_starts_nothing()
    {
        var agent = new TestAgent();
        await using var host = await StartAsync(new FixedFactory(agent));
        var key = await SeedAsync(host, "input");
        var before = await SnapshotAsync(host, key);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ContinueAsync(host, key, ct: cts.Token));

        Assert.Equal(0, agent.Continues);
        Assert.Equal(before, await SnapshotAsync(host, key));
    }

    [Fact]
    public async Task Invalid_key_throws()
    {
        var agent = new TestAgent();
        await using var host = await StartAsync(new FixedFactory(agent));

        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await ContinueAsync(host, new TaskKey("", null!, "t1")));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await ContinueAsync(host, new TaskKey(null!, "alice", "t1")));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await ContinueAsync(host, new TaskKey("", "alice", "")));
        Assert.Equal(0, agent.Continues);
    }

    [Fact]
    public async Task Run_span_has_kind_continue()
    {
        using var trace = new TraceCollector();
        var agent = new TestAgent();
        await using var host = await StartAsync(new FixedFactory(agent));
        var key = await SeedAsync(host, "input");

        var result = await ContinueAsync(host, key);
        await result.Completion.WaitAsync(Wait);

        var run = await trace.WaitForAsync(a => a.OperationName == A2ATelemetry.SpanNames.AgentExecution
            && a.GetTagItem(A2ATelemetry.Tags.ExecutionKind)?.ToString() == "continue");
        Assert.Equal(key.TaskId, run.GetTagItem(A2ATelemetry.Tags.TaskId)?.ToString());
    }
}
