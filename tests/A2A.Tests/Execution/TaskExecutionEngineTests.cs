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
        public ValueTask<AgentExecutorLease> RentAsync(CancellationToken ct) =>
            ValueTask.FromResult(new AgentExecutorLease(new DelegateAgentExecutor(execute), scope: null));
    }

    private sealed class NoPush : IPushNotificationSender
    {
        public void Enqueue(TaskKey task, SequencedEvent evt) { }
    }

    private static TaskExecutionEngine Engine(TaskStore tasks, Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute,
        A2AServerOptions? options = null)
    {
        options ??= new A2AServerOptions();
        return new TaskExecutionEngine(
            tasks,
            new InMemoryEventStore(options.Streaming),
            new DelegateFactory(execute),
            new TaskEventBus(),
            new NoPush(),
            new IdempotencyCoordinator(new InMemoryIdempotencyStore(options.Idempotency), tasks, options),
            new PushConfigService(new InMemoryPushConfigStore(), new EphemeralPushSecretProtector(), options),
            options);
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
        var tasks = new InMemoryTaskStore();
        await using var engine = Engine(tasks, async (_, updater, ct) =>
        {
            await updater.StartWorkAsync("working", ct);
            await updater.CompleteAsync("done", ct);
        });
        var context = NewTask(ServerCallContext.Anonymous());

        var exec = engine.Start(context, push: null, IdempotencyClaim.None);
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
        var tasks = new InMemoryTaskStore();
        await using var engine = Engine(tasks, async (_, updater, ct) =>
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

        var exec = engine.Start(context, push: null, IdempotencyClaim.None);
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
        await using var engine = Engine(tasks, async (_, updater, ct) =>
        {
            await updater.StartWorkAsync(cancellationToken: ct);
            await updater.CompleteAsync("done", ct);
        });
        var context = NewTask(ServerCallContext.Anonymous());

        var exec = engine.Start(context, push: null, IdempotencyClaim.None);
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
}
