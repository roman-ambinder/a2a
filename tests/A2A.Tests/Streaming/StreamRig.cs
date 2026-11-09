using System.Runtime.CompilerServices;
using System.Security.Claims;
using A2A.Server;
using A2A.Server.Agents;
using A2A.Server.Authorization;
using A2A.Server.Coordination;
using A2A.Server.Execution;
using A2A.Server.Idempotency;
using A2A.Server.Pipeline;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using A2A.Tests.Execution;

namespace A2A.Tests.Streaming;

/// <summary>A task store whose reads can run a hook after the stored value was read, or fail on demand.</summary>
internal sealed class HookedTaskStore(TaskStore inner) : TaskStore
{
    private Func<Task>? _afterGet;
    private bool _repeat;
    private int _gets;
    private int _failGets;

    /// <summary>GetAsync calls so far.</summary>
    public int Gets => Volatile.Read(ref _gets);

    /// <summary>Runs <paramref name="hook"/> once, after the next GetAsync has read the task (the caller gets the value read before the hook).</summary>
    public void AfterNextGet(Func<Task> hook)
    {
        _repeat = false;
        Volatile.Write(ref _afterGet, hook);
    }

    /// <summary>Runs <paramref name="hook"/> after every GetAsync.</summary>
    public void AfterEveryGet(Func<Task> hook)
    {
        _repeat = true;
        Volatile.Write(ref _afterGet, hook);
    }

    /// <summary>The next <paramref name="count"/> GetAsync calls throw.</summary>
    public void FailNextGets(int count) => Volatile.Write(ref _failGets, count);

    public override async ValueTask<TaskRecord?> GetAsync(TaskKey key, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _gets);
        if (Interlocked.Decrement(ref _failGets) >= 0) throw new InvalidOperationException("store unavailable");
        var record = await inner.GetAsync(key, cancellationToken).ConfigureAwait(false);
        var hook = _repeat ? Volatile.Read(ref _afterGet) : Interlocked.Exchange(ref _afterGet, null);
        if (hook is not null) await hook().ConfigureAwait(false);
        return record;
    }

    public override ValueTask<TaskRecord> CreateAsync(TaskRecord record, CancellationToken cancellationToken = default) => inner.CreateAsync(record, cancellationToken);
    public override ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion, CancellationToken cancellationToken = default) => inner.UpdateAsync(record, expectedVersion, cancellationToken);
    public override ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken cancellationToken = default) => inner.CommitAsync(commit, cancellationToken);
    public override ValueTask<TaskPage> ListAsync(TaskQuery query, CancellationToken cancellationToken = default) => inner.ListAsync(query, cancellationToken);
    public override ValueTask<ContextRecord?> GetContextAsync(string tenant, string contextId, CancellationToken cancellationToken = default) => inner.GetContextAsync(tenant, contextId, cancellationToken);
    public override ValueTask<ContextRecord> GetOrCreateContextAsync(ContextRecord record, CancellationToken cancellationToken = default) => inner.GetOrCreateContextAsync(record, cancellationToken);
    public override ValueTask<bool> DeleteAsync(TaskKey key, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, cancellationToken);
    public override ValueTask<IReadOnlyList<TaskKey>> ListExpiredAsync(DateTimeOffset terminalBefore, int max, CancellationToken cancellationToken = default) => inner.ListExpiredAsync(terminalBefore, max, cancellationToken);
    public override ValueTask<IReadOnlyList<TaskKey>> ListInterruptedBeforeAsync(DateTimeOffset before, int max, CancellationToken cancellationToken = default) => inner.ListInterruptedBeforeAsync(before, max, cancellationToken);
    public override ValueTask<TaskRecord?> GetForMaintenanceAsync(string tenant, string taskId, CancellationToken cancellationToken = default) => inner.GetForMaintenanceAsync(tenant, taskId, cancellationToken);
    public override ValueTask<bool> DeleteContextAsync(string tenant, string contextId, CancellationToken cancellationToken = default) => inner.DeleteContextAsync(tenant, contextId, cancellationToken);
    public override ValueTask<IReadOnlyList<ContextRecord>> ListOrphanContextsAsync(DateTimeOffset createdBefore, int max, CancellationToken cancellationToken = default) => inner.ListOrphanContextsAsync(createdBefore, max, cancellationToken);
}

/// <summary>An event store whose sequence read can fail on demand, and that counts those reads.</summary>
internal sealed class HookedEventStore(EventStore inner) : EventStore
{
    private int _lastReads;
    private int _failLastReads;

    /// <summary>LastSequenceAsync calls so far.</summary>
    public int LastReads => Volatile.Read(ref _lastReads);

    /// <summary>The next <paramref name="count"/> LastSequenceAsync calls throw.</summary>
    public void FailNextLastReads(int count) => Volatile.Write(ref _failLastReads, count);

    public override ValueTask<long> LastSequenceAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _lastReads);
        if (Interlocked.Decrement(ref _failLastReads) >= 0) throw new InvalidOperationException("store unavailable");
        return inner.LastSequenceAsync(task, cancellationToken);
    }

    public override ValueTask<long> AppendAsync(TaskKey task, SequencedEvent evt, CancellationToken cancellationToken = default) => inner.AppendAsync(task, evt, cancellationToken);
    public override IAsyncEnumerable<SequencedEvent> ReadAfterAsync(TaskKey task, long afterSequence, CancellationToken cancellationToken = default) => inner.ReadAfterAsync(task, afterSequence, cancellationToken);
    public override ValueTask<long?> OldestRetainedAsync(TaskKey task, CancellationToken cancellationToken = default) => inner.OldestRetainedAsync(task, cancellationToken);
    public override ValueTask MarkTerminalAsync(TaskKey task, CancellationToken cancellationToken = default) => inner.MarkTerminalAsync(task, cancellationToken);
    public override ValueTask DeleteAsync(TaskKey task, CancellationToken cancellationToken = default) => inner.DeleteAsync(task, cancellationToken);
}

/// <summary>A bus that tells the test when a stream subscribed.</summary>
internal class SignalBus(StreamingOptions options) : TaskEventBus(options)
{
    /// <summary>Runs after every Subscribe.</summary>
    public Action? OnSubscribe { get; set; }

    public override Subscription Subscribe(string taskId)
    {
        var sub = base.Subscribe(taskId);
        OnSubscribe?.Invoke();
        return sub;
    }
}

/// <summary>
/// Two engines ("instances") on the same stores and the same bus, and a streamer on the first. The second one commits as
/// "another instance": it has its own task locks. The first reads the task store through <see cref="HookedTaskStore"/>.
/// </summary>
internal sealed class StreamRig : IAsyncDisposable
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    public required A2AServerOptions Options { get; init; }
    public required InMemoryEventStore Log { get; init; }
    public required InMemoryTaskStore Inner { get; init; }
    public required HookedTaskStore Tasks { get; init; }
    public required HookedEventStore Events { get; init; }
    public required TaskEventBus Bus { get; init; }
    public required TaskExecutionEngine A { get; init; }
    public required TaskExecutionEngine B { get; init; }
    public required TaskStreamer Streamer { get; init; }

    public static StreamRig Create(Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> agent, Action<A2AServerOptions>? configure = null,
        TaskEventBus? bus = null, TimeProvider? time = null, ExecutionCoordinator? coordinator = null)
    {
        var options = new A2AServerOptions();
        options.AgentCard.Name = "Stream Rig Agent";
        options.AgentCard.Capabilities = new AgentCapabilities { Streaming = true };
        configure?.Invoke(options);
        var log = new InMemoryEventStore(options.Streaming);
        var inner = new InMemoryTaskStore(log);
        var tasks = new HookedTaskStore(inner);
        var events = new HookedEventStore(log);
        bus ??= new SignalBus(options.Streaming);
        var cards = new AgentCardProvider(options, []);
        TaskExecutionEngine Engine(TaskStore store, EventStore eventStore, ExecutionCoordinator? coord) =>
            new(store, eventStore, new DelegateFactory(agent), bus, new InMemoryPushOutbox(),
                new IdempotencyCoordinator(new InMemoryIdempotencyStore(options.Idempotency), store, options),
                new PushConfigService(new InMemoryPushConfigStore(), new EphemeralPushSecretProtector(), options), options, cards,
                logger: null, timeProvider: time, coordinator: coord);
        var a = Engine(tasks, events, coordinator);
        // Another instance: the stores underneath, its own locks and executions.
        var b = Engine(inner, log, coord: null);
        var allowAll = new AllowAllAuthorizationHandler([]);
        var streamer = new TaskStreamer(tasks, events, a, new IdempotencyCoordinator(new InMemoryIdempotencyStore(options.Idempotency), tasks, options),
            allowAll, options, cards, new SkillAuthorizer(allowAll, options.SkillAuthorization, cards, new RequestCards(cards)));
        return new StreamRig { Options = options, Log = log, Inner = inner, Tasks = tasks, Events = events, Bus = bus, A = a, B = b, Streamer = streamer };
    }

    private sealed class DelegateFactory(Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute) : IAgentExecutorFactory
    {
        public ValueTask<AgentExecutorLease> RentAsync(string tenant, CancellationToken ct) =>
            ValueTask.FromResult(new AgentExecutorLease(new DelegateAgentExecutor(execute), scope: null));
    }

    public static ServerCallContext Call() => new()
    {
        Principal = "alice",
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "Test")),
    };

    public static AgentRequestContext NewTaskContext() => new()
    {
        Message = Message.FromUser("go"), TaskId = TaskIds.New(), ContextId = TaskIds.New(), Call = Call(),
    };

    public static TaskKey KeyOf(AgentRequestContext context) => new(context.Call.Tenant, context.Call.Principal, context.TaskId);

    /// <summary>Starts the agent for a new task and detaches the request.</summary>
    public async Task<TaskKey> StartTaskAsync(AgentRequestContext? context = null)
    {
        context ??= NewTaskContext();
        var exec = await A.StartForTestAsync(context);
        exec.Detach();
        return KeyOf(context);
    }

    /// <summary>Waits until the task's log has an event with <paramref name="sequence"/>.</summary>
    public async Task WaitForSequenceAsync(TaskKey key, long sequence)
    {
        using var timeout = new CancellationTokenSource(Wait);
        while (await Log.LastSequenceAsync(key) < sequence) await Task.Delay(5, timeout.Token);
    }

    public IAsyncEnumerable<SequencedEvent> Subscribe(TaskKey key, CancellationToken ct = default) =>
        Streamer.SubscribeAsync(new SubscribeToTaskRequest { Id = key.TaskId }, Call(), resumeAfter: null, onCaughtUp: null,
            A2AOperationDescriptor.For(A2AOperation.SubscribeToTask), ct);

    public ValueTask DisposeAsync() => A.DisposeAsync();
}
