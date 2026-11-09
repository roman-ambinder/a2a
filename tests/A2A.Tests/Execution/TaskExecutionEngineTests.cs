using A2A.Server;
using A2A.Server.Execution;
using A2A.Server.Idempotency;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using A2A.Tests.Parity;

namespace A2A.Tests.Execution;

/// <summary>
/// The engine runs without a DI container: every dependency is built with <c>new</c> (spec D27, the no-DI server proof).
/// </summary>
public sealed class TaskExecutionEngineTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class DelegateFactory(Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute) : IAgentExecutorFactory
    {
        public ValueTask<AgentExecutorLease> RentAsync(string tenant, CancellationToken ct) =>
            ValueTask.FromResult(new AgentExecutorLease(new DelegateAgentExecutor(execute), scope: null));
    }


    /// <summary>A task store and the event store it commits to.</summary>
    private static (InMemoryTaskStore Tasks, InMemoryEventStore Events) Stores(A2AServerOptions? options = null)
    {
        var events = new InMemoryEventStore((options ?? new A2AServerOptions()).Streaming);
        return (new InMemoryTaskStore(events, new InMemoryPushOutbox()), events);
    }

    private static TaskExecutionEngine Engine(TaskStore tasks, EventStore events, Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute,
        A2AServerOptions? options = null, PushConfigService? pushConfigs = null)
    {
        options ??= new A2AServerOptions();
        if (string.IsNullOrEmpty(options.AgentCard.Name)) options.AgentCard.Name = "Engine Test Agent";
        return new TaskExecutionEngine(
            tasks,
            events,
            new DelegateFactory(execute),
            new TaskEventBus(),
            new InMemoryPushOutbox(),
            new IdempotencyCoordinator(new InMemoryIdempotencyStore(options.Idempotency), tasks, options),
            pushConfigs ?? new PushConfigService(new InMemoryPushConfigStore(), new EphemeralPushSecretProtector(), options),
            options,
            new AgentCardProvider(options, []));
    }

    private static AgentRequestContext NewTask(ServerCallContext call) => new()
    {
        Message = Message.FromUser("go"),
        TaskId = TaskIds.New(),
        ContextId = TaskIds.New(),
        Call = call,
    };

    private static TaskKey Key(AgentRequestContext context) => new(context.Call.Tenant, context.Call.Principal, context.TaskId);

    [Fact]
    public async Task Start_runs_executor_and_stores_terminal_state()
    {
        var (tasks, events) = Stores();
        await using var engine = Engine(tasks, events, async (_, updater, ct) =>
        {
            await updater.StartWorkAsync("working", ct);
            await updater.CompleteAsync("done", ct);
        });
        var context = NewTask(ServerCallContext.Anonymous());

        var exec = (await engine.StartForTestAsync(context));
        var seen = new List<SequencedEvent>();
        await foreach (var e in exec.Outgoing.Reader.ReadAllAsync().WithCancellation(new CancellationTokenSource(Wait).Token)) seen.Add(e);
        await exec.Completion.WaitAsync(Wait);

        // The new task, then each event, in sequence order.
        Assert.Equal([1L, 2L, 3L], seen.Select(e => e.Sequence));
        Assert.IsType<TaskEvent>(seen[0].Event);
        var record = await tasks.GetAsync(Key(context));
        Assert.NotNull(record);
        Assert.Equal(TaskState.Completed, record.Task.Status.State);
        Assert.Equal("done", record.Task.Status.Message!.GetText());
        Assert.Equal(["go", "working", "done"], record.Task.History!.Select(m => m.GetText()));
        await engine.WhenIdleAsync().WaitAsync(Wait);
    }

    [Fact]
    public async Task Oversized_event_fails_task_and_stops_executor()
    {
        var options = new A2AServerOptions();
        options.Limits.MaxEventBytes = 1024;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (tasks, events) = Stores();
        await using var engine = Engine(tasks, events, async (_, updater, ct) =>
        {
            await updater.StartWorkAsync(cancellationToken: ct);
            await updater.AddArtifactAsync([Part.FromText(new string('x', 4096))], name: "big", cancellationToken: ct);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                stopped.TrySetResult();
                throw;
            }
        }, options);
        var context = NewTask(ServerCallContext.Anonymous());

        var exec = (await engine.StartForTestAsync(context));
        await stopped.Task.WaitAsync(Wait);
        await exec.Completion.WaitAsync(Wait);

        var record = await tasks.GetAsync(Key(context));
        Assert.NotNull(record);
        Assert.Equal(TaskState.Failed, record.Task.Status.State);
        Assert.Equal(TaskExecutionEngine.OversizedEventText, record.Task.Status.Message!.GetText());
        Assert.Null(record.Task.Artifacts);
    }

    [Fact]
    public async Task Store_failure_fails_task()
    {
        var tasks = new FailOnNthUpdateStore(failOn: 2); // WORKING is stored; COMPLETED fails
        await using var engine = Engine(tasks, tasks.Events, async (_, updater, ct) =>
        {
            await updater.StartWorkAsync(cancellationToken: ct);
            await updater.CompleteAsync("done", ct);
        });
        var context = NewTask(ServerCallContext.Anonymous());

        var exec = (await engine.StartForTestAsync(context));
        // The request that started the run gets an error after the events that were stored, not the FAILED event.
        var seen = new List<SequencedEvent>();
        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var e in exec.Outgoing.Reader.ReadAllAsync().WithCancellation(new CancellationTokenSource(Wait).Token)) seen.Add(e);
        });
        Assert.Equal([1L, 2L], seen.Select(e => e.Sequence)); // the new task, then WORKING
        Assert.Equal(A2AErrorCode.Internal, ex.Code);
        Assert.True(exec.Failed);
        await exec.Completion.WaitAsync(Wait);

        // The task does not stay WORKING: it is FAILED, with fixed text.
        var record = await tasks.GetAsync(Key(context));
        Assert.NotNull(record);
        Assert.Equal(TaskState.Failed, record.Task.Status.State);
        Assert.Equal(TaskExecutionEngine.StoreFailureText, record.Task.Status.Message!.GetText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Events_queue_push_deliveries_only_when_the_card_declares_push(bool declared)
    {
        // The request resolves the tenant's card and passes its push capability to the execution; without it, no commit
        // queues a delivery, even for a task with a push config.
        var options = new A2AServerOptions();
        var events = new InMemoryEventStore(options.Streaming);
        var outbox = new InMemoryPushOutbox();
        var tasks = new InMemoryTaskStore(events, outbox);
        var configs = new InMemoryPushConfigStore();
        var context = NewTask(ServerCallContext.Anonymous());
        await configs.SaveAsync(new PushConfigRecord(Key(context), new TaskPushNotificationConfig { Id = "c1", TaskId = context.TaskId, Url = "https://hooks.example/h" }));
        await using var engine = Engine(tasks, events, async (_, updater, ct) =>
        {
            await updater.StartWorkAsync("working", ct);
            await updater.CompleteAsync("done", ct);
        }, options, new PushConfigService(configs, new EphemeralPushSecretProtector(), options));
        var exec = await engine.StartForTestAsync(context, pushEnabled: declared);
        exec.Detach();
        await exec.Completion.WaitAsync(Wait);

        if (declared) Assert.Equal(2, outbox.Count); // WORKING, COMPLETED (the new task's snapshot goes only to an inline config)
        else Assert.Equal(0, outbox.Count);
    }

    /// <summary>Records every commit the engine makes.</summary>
    private sealed class RecordingCommitStore : DelegatingTaskStore
    {
        public List<TaskCommit> Commits { get; } = [];

        public override ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default)
        {
            lock (Commits) Commits.Add(commit);
            return base.CommitAsync(commit, ct);
        }
    }

    [Fact]
    public async Task Create_commit_carries_the_idempotency_outcome_and_later_commits_do_not()
    {
        var tasks = new RecordingCommitStore();
        var options = new A2AServerOptions();
        options.AgentCard.Name = "Engine Test Agent";
        var coordinator = new IdempotencyCoordinator(new InMemoryIdempotencyStore(options.Idempotency), tasks, options);
        await using var engine = new TaskExecutionEngine(tasks, tasks.Events,
            new DelegateFactory(async (_, updater, ct) =>
            {
                await updater.StartWorkAsync("working", ct);
                await updater.CompleteAsync("done", ct);
            }),
            new TaskEventBus(), tasks.Outbox, coordinator,
            new PushConfigService(new InMemoryPushConfigStore(), new EphemeralPushSecretProtector(), options), options, new AgentCardProvider(options, []));
        var call = ServerCallContext.Anonymous();
        var context = NewTask(call);
        var request = new SendMessageRequest { Message = context.Message };
        var (claim, replay) = await coordinator.BeginAsync(request, call, CancellationToken.None);
        Assert.Null(replay);

        var agent = await engine.RentAgentAsync(call.Tenant, null, CancellationToken.None);
        var exec = await engine.StartAsync(context, push: null, claim, agent, pushEnabled: false);
        await exec.Completion.WaitAsync(Wait);
        await engine.WhenIdleAsync().WaitAsync(Wait);

        Assert.True(tasks.Commits.Count >= 3);
        var create = tasks.Commits[0];
        Assert.True(create.IsCreate);
        var idem = Assert.IsType<IdempotencyCommit>(create.Idempotency);
        Assert.Equal(new IdempotencyOutcome.TaskCreated(context.TaskId), idem.Outcome);
        Assert.Equal(new IdempotencyScope(call.Tenant, call.Principal, context.Message.MessageId), idem.Scope);
        Assert.Equal(options.Idempotency.Retention, idem.Retention);
        Assert.All(tasks.Commits.Skip(1), c => Assert.Null(c.Idempotency));
        // The in-memory task store ignores the outcome, so the engine still recorded it after the commit.
        var (_, again) = await coordinator.BeginAsync(request, call, CancellationToken.None);
        Assert.Equal(new IdempotencyOutcome.TaskCreated(context.TaskId), again);
    }

    [Fact]
    public async Task Create_commit_has_no_idempotency_outcome_without_a_claim()
    {
        var tasks = new RecordingCommitStore();
        await using var engine = Engine(tasks, tasks.Events, async (_, updater, ct) => await updater.CompleteAsync("done", ct));
        var context = NewTask(ServerCallContext.Anonymous());

        var exec = await engine.StartForTestAsync(context); // IdempotencyClaim.None
        await exec.Completion.WaitAsync(Wait);

        Assert.True(tasks.Commits[0].IsCreate);
        Assert.Null(tasks.Commits[0].Idempotency);
    }
}
