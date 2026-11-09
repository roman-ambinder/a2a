using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using A2A.Diagnostics;
using A2A.Server.Agents;
using A2A.Server.Coordination;
using A2A.Server.Idempotency;
using A2A.Server.Pipeline;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using A2A.Server.Telemetry;
using A2A.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.Server.Execution;

/// <summary>
/// Runs agents for tasks. Each event an agent produces is applied to a copy of the task and committed together with its push
/// deliveries (<see cref="TaskStore.CommitAsync"/>: the task, the event log and the push outbox in one unit), then published
/// (to the request that started the run and to stream subscribers), all under the task's lock, so publish order is sequence
/// order. In an ASP.NET Core app the DI container creates and disposes it.
/// </summary>
/// <remarks>
/// <para>
/// Each run is an <see cref="A2ATelemetry.SpanNames.AgentExecution"/> span, a child of the operation that started it; the agent's
/// own spans and the webhook deliveries of its events nest under it. With <see cref="A2AServerOptions.MaxTaskDuration"/>, a
/// run still going when the limit expires fails the task ("Task exceeded its time limit") and is cancelled.
/// </para>
/// <para>
/// Every run holds a lease with the <see cref="ExecutionCoordinator"/> while it runs, renewed every
/// <see cref="CoordinationOptions.Heartbeat"/>, so every instance that shares the coordinator sees it running; cancel requests
/// of other instances reach it through the coordinator (see the coordination members of this class).
/// </para>
/// </remarks>
public sealed partial class TaskExecutionEngine : IDisposable, IAsyncDisposable
{
    private readonly TaskStore _tasks;
    private readonly IBatchTaskCommit? _batchCommit; // _tasks, when it commits batches
    private readonly EventStore _events;
    private readonly IAgentExecutorFactory _executors;
    private readonly TaskEventBus _bus;
    private readonly PushOutbox _outbox;
    private readonly IdempotencyCoordinator _idempotency;
    private readonly PushConfigService _pushConfigs;
    private readonly A2AServerOptions _options;
    private readonly IAgentCardProvider _cards;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ArtifactObjectStore? _objects;

    /// <summary>The current time of the engine's clock, UTC. The request handler stamps accepted messages with it.</summary>
    internal DateTimeOffset UtcNow => _time.GetUtcNow();

    /// <summary>The engine's logger (streams log their own warnings with it).</summary>
    internal ILogger Logger => _logger;

    /// <summary>The engine's clock, shared with the services that must agree with it on the age of things (inbound uploads).</summary>
    internal TimeProvider Clock => _time;
    private readonly ExecutionCoordinator _coordinator;
    private readonly ExecutionAdmission _admission;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskLocks _locks = new();
    private readonly RunningRegistry<Execution> _running = new();
    private int _disposed;

    /// <summary>Creates the engine. In an ASP.NET Core app the DI container does this; the parameters are the registered services.</summary>
    /// <param name="tasks">The task store.</param>
    /// <param name="events">The event store.</param>
    /// <param name="executors">Rents the agent executor of each run.</param>
    /// <param name="bus">Live stream subscribers.</param>
    /// <param name="outbox">The push outbox that commits queue deliveries in; told when new deliveries are due.</param>
    /// <param name="idempotency">Records the outcome of a retried SendMessage.</param>
    /// <param name="pushConfigs">Saves an inline push config when its task is first stored.</param>
    /// <param name="options">Server options (limits, streaming, <see cref="A2AServerOptions.MaxTaskDuration"/>).</param>
    /// <param name="cards">The Agent Cards (whether a tenant's card declares push, for commits no request made).</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="timeProvider">
    /// The clock of <see cref="A2AServerOptions.MaxTaskDuration"/> and of the execution heartbeat. Default <see cref="TimeProvider.System"/>.
    /// </param>
    /// <param name="coordinator">
    /// Tracks running executions across instances. Default a process-local <see cref="InMemoryExecutionCoordinator"/>.
    /// </param>
    /// <param name="objects">
    /// Where large raw parts of artifacts go (<see cref="A2AServerOptions.ArtifactStorage"/>). Null keeps every part inline.
    /// </param>
    /// <exception cref="InvalidOperationException"><see cref="A2AServerOptions.Coordination"/> is invalid, or <paramref name="objects"/> is set and
    /// neither <see cref="ArtifactStorageOptions.PublicBaseUrl"/> nor <see cref="ArtifactStorageOptions.PublicBaseUrlResolver"/> is.</exception>
    public TaskExecutionEngine(
        TaskStore tasks,
        EventStore events,
        IAgentExecutorFactory executors,
        TaskEventBus bus,
        PushOutbox outbox,
        IdempotencyCoordinator idempotency,
        PushConfigService pushConfigs,
        A2AServerOptions options,
        IAgentCardProvider cards,
        ILogger<TaskExecutionEngine>? logger = null,
        TimeProvider? timeProvider = null,
        ExecutionCoordinator? coordinator = null,
        ArtifactObjectStore? objects = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (objects is not null && options.ArtifactStorage.PublicBaseUrl is null && options.ArtifactStorage.PublicBaseUrlResolver is null)
            throw new InvalidOperationException("An ArtifactObjectStore is registered, but A2AServerOptions.ArtifactStorage.PublicBaseUrl (or PublicBaseUrlResolver) is not set. Artifact links need a base URL.");
        _objects = objects;
        _tasks = tasks;
        _batchCommit = tasks as IBatchTaskCommit;
        _events = events;
        _executors = executors;
        _bus = bus;
        _outbox = outbox;
        _idempotency = idempotency;
        _pushConfigs = pushConfigs;
        _options = options;
        _cards = cards;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
        options.Coordination.Validate();
        _coordinator = coordinator ?? new InMemoryExecutionCoordinator(options.Coordination, _time);
        _admission = new ExecutionAdmission(options.Limits, _time);
        StartCancelListener(_shutdown.Token);
    }

    /// <summary>
    /// Retries after a version conflict when applying one change to a task. Conflicts come from other instances (writers here
    /// share the task lock), and one that writes back to back can win several races in a row.
    /// </summary>
    internal const int MaxConflictRetries = 10;

    /// <summary>
    /// Waits before retry number <paramref name="attempt"/> + 1 after a version conflict: 5 ms doubling per attempt, at most
    /// 200 ms, ±20 % jitter, so writers on different instances stop colliding in lockstep. Real time on purpose (not the
    /// injected <see cref="TimeProvider"/>): a manual test clock must not stall a store retry.
    /// </summary>
    internal static Task ConflictBackoffAsync(int attempt, CancellationToken cancellationToken)
    {
        var baseMs = Math.Min(5.0 * Math.Pow(2, Math.Min(attempt, 16)), 200.0);
#pragma warning disable CA5394 // jitter, not security
        var jittered = baseMs * (0.8 + (0.4 * Random.Shared.NextDouble()));
#pragma warning restore CA5394
        return Task.Delay(TimeSpan.FromMilliseconds(jittered), cancellationToken);
    }

    /// <summary>Status text of a task failed because its updates could not be stored.</summary>
    internal const string StoreFailureText = "The task failed because its updates could not be stored.";

    /// <summary>Status text of a task failed because a run lasted longer than <see cref="A2AServerOptions.MaxTaskDuration"/>.</summary>
    internal const string TimeLimitText = "Task exceeded its time limit";

    /// <summary>Status text of a task failed because the execution of a follow-up could not start after its message was committed.</summary>
    internal const string StartFailureText = "The task failed because the agent could not be started for the message.";

    /// <summary>Status text of a task failed because it grew past <see cref="A2ALimits.MaxTaskBytes"/> or <see cref="A2ALimits.MaxArtifactsPerTask"/>.</summary>
    internal const string OversizedTaskText = "The task grew past the configured size limit.";

    /// <summary>Status text of a task failed because the agent produced an event larger than <see cref="A2ALimits.MaxEventBytes"/>.</summary>
    internal const string OversizedEventText = "The agent produced an event larger than the configured limit.";

    /// <summary>Status text of a task failed because it used more than <see cref="A2ALimits.MaxTaskTokens"/> or <see cref="A2ALimits.MaxTaskCostUnits"/>.</summary>
    internal const string BudgetExceededText = "Task exceeded its budget";

    /// <summary>The execution slots (running and queued starts). For tests.</summary>
    internal ExecutionAdmission Admission => _admission;

    /// <summary>The bus the engine publishes every stored event to.</summary>
    internal TaskEventBus Bus => _bus;

    /// <summary>Task ids are unique per tenant only, so in-memory state (locks, running executions, bus topics) is keyed by both.</summary>
    internal static string ScopeKey(string tenant, string taskId) => $"{tenant}\u001f{taskId}";

    /// <summary>Waits for the task's lock; dispose the result to release it.</summary>
    internal ValueTask<TaskLocks.Releaser> LockAsync(TaskKey key, CancellationToken ct) => _locks.AcquireAsync(key, ct);

    /// <summary>The index of the task's lock among the shared striped locks (tasks on the same stripe share a lock). For tests.</summary>
    internal int LockStripe(string tenant, string taskId) => _locks.Stripe(tenant, taskId);

    /// <summary>Completions of the executions running for a task on this instance.</summary>
    internal List<Task> RunningCompletions(TaskKey key) =>
        _running.Snapshot(ScopeKey(key.Tenant, key.TaskId)).Where(e => e.Key == key).Select(e => e.Completion).ToList();

    /// <summary>
    /// Rents the agent of <paramref name="tenant"/> for one execution (see <see cref="RentedAgent"/>). A factory that throws
    /// does not throw here: the execution fails with that error. A tenant without an agent gets <paramref name="fallback"/>.
    /// </summary>
    internal ValueTask<RentedAgent> RentAgentAsync(string tenant, IAgentExecutor? fallback, CancellationToken ct) =>
        RentedAgent.RentAsync(_executors, tenant, fallback, ct);

    /// <summary>
    /// Takes an execution slot for a run for <paramref name="tenant"/> (see <see cref="ExecutionAdmission"/>), waiting in the
    /// queue when none is free. While it waits, the idempotency <paramref name="claim"/> is renewed every
    /// <see cref="CoordinationOptions.Heartbeat"/>: only a running execution renews it otherwise. Give the slot to the agent
    /// (<see cref="RentedAgent.AttachSlot"/>) or dispose it.
    /// </summary>
    /// <exception cref="A2AException">RATE_LIMITED, <c>reason = execution_capacity</c>: no slot, no queue room, or the wait ended.</exception>
    internal ValueTask<ExecutionSlot> AdmitAsync(string tenant, IdempotencyClaim claim, string? taskId, CancellationToken ct) =>
        _admission.EnterAsync(tenant, ReferenceEquals(claim, IdempotencyClaim.None) ? null : () => _time.CreateTimer(
            static state => { var (engine, c, id) = ((TaskExecutionEngine, IdempotencyClaim, string?))state!; _ = engine.RenewQueuedClaimAsync(c, id); },
            (this, claim, taskId), _options.Coordination.Heartbeat, _options.Coordination.Heartbeat), ct);

    private async Task RenewQueuedClaimAsync(IdempotencyClaim claim, string? taskId)
    {
        try { await claim.RenewAsync().ConfigureAwait(false); }
        catch (Exception ex) { ServerLog.ClaimRenewFailed(_logger, ex, taskId ?? string.Empty); }
    }

    /// <summary>
    /// <see cref="RentAgentAsync"/> for a message: a tenant without an agent is <see cref="A2AErrorCode.UnsupportedOperation"/>
    /// (<see cref="RequestCards.NoAgentText"/>), before anything is claimed or stored.
    /// </summary>
    internal async ValueTask<RentedAgent> RequireAgentAsync(string tenant, CancellationToken ct)
    {
        var agent = await RentAgentAsync(tenant, null, ct).ConfigureAwait(false);
        if (agent.Executor is not null || agent.Error is not null) return agent;
        await agent.DisposeAsync().ConfigureAwait(false);
        throw new A2AException(A2AErrorCode.UnsupportedOperation, RequestCards.NoAgentText);
    }

    /// <summary>
    /// Runs <see cref="IAgentExecutor.ExecuteAsync"/> for a message. An idempotency <paramref name="claim"/> is completed when
    /// the task is first stored or the agent replies directly, and abandoned when neither happens. <paramref name="push"/> is
    /// saved when a new task is first stored. The execution takes <paramref name="agent"/>'s lease. <paramref name="pushEnabled"/>:
    /// whether the tenant's card declares push notifications (resolved by the request), which decides the push deliveries.
    /// </summary>
    internal Task<Execution> StartAsync(AgentRequestContext context, TaskPushNotificationConfig? push, IdempotencyClaim claim, RentedAgent agent,
        bool pushEnabled) =>
        StartRawAsync(new TaskKey(context.Call.Tenant, context.Call.Principal, context.TaskId), context.ContextId, context.IsNewTask, context.Message,
            push, emitInitialSnapshot: !context.IsNewTask, (executor, updater, ct) =>
            {
                updater.SeedArtifacts(context.Task?.Artifacts); // the artifacts earlier runs left
                updater.SeedUsage(context.Task?.Metadata); // the usage earlier runs stored
                return executor.ExecuteAsync(context, updater, ct);
            }, agent, pushEnabled,
            claim, context.Metadata, _options.MaxTaskDuration, ExecuteKind);

    /// <summary>Runs <see cref="IAgentExecutor.CancelAsync"/> for a task. The executor gets its own copy of <paramref name="task"/>.</summary>
    internal Task<Execution> StartCancelAsync(TaskKey key, AgentTask task, Dictionary<string, JsonElement>? metadata, RentedAgent agent, bool pushEnabled)
    {
        var snapshot = task.DeepCopy(); // the executor's own view: changing it never changes the stored task
        return StartRawAsync(key, snapshot.ContextId, isNewTask: false, userMessage: null, push: null, emitInitialSnapshot: false,
            (executor, updater, ct) =>
            {
                updater.SeedArtifacts(snapshot.Artifacts);
                updater.SeedUsage(snapshot.Metadata);
                return executor.CancelAsync(snapshot, updater, ct);
            }, agent, pushEnabled, requestMetadata: metadata, kind: CancelKind);
    }

    private const string ExecuteKind = "execute";
    private const string CancelKind = "cancel";

    /// <summary>
    /// Waits for the outcome of a blocking SendMessage, then returns the task, as the first request does:
    /// <list type="bullet">
    /// <item>a terminal state;</item>
    /// <item>an interrupted state reached by a status event after <paramref name="afterSequence"/> (an interrupted state from
    /// before a follow-up is not its outcome); seen in the event store or on the live bus. When the store no longer holds the
    /// first events after <paramref name="afterSequence"/> (trimmed), the stored task decides: an interrupted state counts
    /// once its status timestamp differs from <paramref name="acceptedStatusTimestamp"/>;</item>
    /// <item>no execution running for the task any more, on any instance (the agent returned without settling it, or its
    /// instance stopped): nothing will change it, so its current state;</item>
    /// <item>or the request is cancelled.</item>
    /// </list>
    /// Subscribing before the first read, and checking for running executions before reading the task, means no change is missed.
    /// The task returned may share instances with the store: serialize it, never change it.
    /// </summary>
    /// <param name="key">The task.</param>
    /// <param name="afterSequence">The task's last event sequence before the message; 0 for a new task.</param>
    /// <param name="acceptedStatusTimestamp">
    /// For a follow-up, the timestamp of the task's status when the message was accepted
    /// (<see cref="IdempotencyOutcome.TaskCreated.AcceptedStatusTimestamp"/>); null when unknown, and then a trimmed store
    /// leaves the decision to the stored task alone.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<AgentTask> WaitUntilSettledAsync(TaskKey key, long afterSequence, DateTimeOffset? acceptedStatusTimestamp,
        CancellationToken cancellationToken)
    {
        using var sub = _bus.Subscribe(ScopeKey(key.Tenant, key.TaskId));
        var (statusAfter, trimmed, known) = afterSequence == 0
            ? (true, false, 0L)
            : await StatusEventAfterAsync(key, afterSequence, cancellationToken).ConfigureAwait(false);
        Task<bool>? live = null; // one pending wait at a time: the subscription has a single reader
        while (true)
        {
            var running = await RunningWaitsAsync(key, cancellationToken).ConfigureAwait(false);
            var record = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(key.TaskId);
            var state = record.Task.Status.State;
            var outcome = statusAfter || (trimmed && StatusChangedSince(record.Task.Status, acceptedStatusTimestamp));
            if (state.IsTerminal() || (state.IsInterrupted() && outcome) || running is null) return record.Task;

            live ??= sub.Reader.WaitToReadAsync(cancellationToken).AsTask();
            await Task.WhenAny(running.Append(live)).ConfigureAwait(false);
            if (!live.IsCompleted) continue; // an execution ended, or time to ask the coordinator again
            if (!await live.ConfigureAwait(false))
            {
                // The bus ended the topic (the task finished): one last read.
                return (await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(key.TaskId)).Task;
            }
            live = null;
            var missed = false;
            while (sub.Reader.TryRead(out var evt))
            {
                if (statusAfter) continue;
                if (evt.Sequence > known + 1) missed = true; // the bounded subscription dropped events
                known = Math.Max(known, evt.Sequence);
                if (evt.Sequence > afterSequence && evt.Event is StatusUpdateEvent) statusAfter = true;
            }
            // Every event is stored before it is published: look for the dropped ones in the store.
            if (missed && !statusAfter)
                (statusAfter, trimmed, known) = await StatusEventAfterAsync(key, afterSequence, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the event store holds a status event of the task with a sequence after <paramref name="afterSequence"/>, and the
    /// last sequence read (at least <paramref name="afterSequence"/>). <c>Trimmed</c> is true when the store no longer has the
    /// first events after <paramref name="afterSequence"/>: without a status event among the rest, the caller then decides from
    /// the stored task (<see cref="StatusChangedSince"/>).
    /// </summary>
    private async Task<(bool Found, bool Trimmed, long Last)> StatusEventAfterAsync(TaskKey key, long afterSequence, CancellationToken ct)
    {
        var last = afterSequence;
        var trimmed = false;
        await foreach (var stored in _events.ReadAfterAsync(key, afterSequence, ct).ConfigureAwait(false))
        {
            if (stored.Sequence != last + 1) trimmed = true;
            if (stored.Event is StatusUpdateEvent) return (true, trimmed, stored.Sequence);
            last = stored.Sequence;
        }
        return (false, trimmed, last);
    }

    /// <summary>
    /// Whether <paramref name="status"/> is a different status from the one the task had when a follow-up was accepted, known by
    /// its timestamp <paramref name="acceptedTimestamp"/>. Every status the engine commits has a timestamp, so a status set
    /// after the follow-up has a new one. Null (not known) answers true: the stored task decides alone.
    /// </summary>
    internal static bool StatusChangedSince(AgentTaskStatus status, DateTimeOffset? acceptedTimestamp) =>
        acceptedTimestamp is null || status.Timestamp != acceptedTimestamp;

    /// <summary>
    /// Runs the agent for a task. An idempotency <c>claim</c> is completed when the task is first stored or the agent replies
    /// directly, and abandoned when neither happens. With a <paramref name="timeLimit"/>, a run still going when it expires
    /// fails the task (<see cref="TimeLimitText"/>) and is cancelled.
    /// </summary>
    /// <remarks>
    /// The execution is registered here first, then with the <see cref="ExecutionCoordinator"/> before the agent starts, so
    /// every instance sees it running. Every <see cref="CoordinationOptions.Heartbeat"/> it renews its lease and its idempotency
    /// claim (while the claim is unsettled). It unregisters once the agent returned and every event is stored, before the
    /// execution counts as done here. Coordinator failures are logged and never stop the execution.
    /// </remarks>
    private async Task<Execution> StartRawAsync(TaskKey key, string contextId, bool isNewTask, Message? userMessage,
        TaskPushNotificationConfig? push, bool emitInitialSnapshot, Func<IAgentExecutor, TaskUpdater, CancellationToken, Task> run, RentedAgent agent,
        bool pushEnabled, IdempotencyClaim? claim = null, Dictionary<string, JsonElement>? requestMetadata = null, TimeSpan? timeLimit = null,
        string kind = ExecuteKind)
    {
        var taskId = key.TaskId;
        claim ??= IdempotencyClaim.None;
        // A CancelTask run counts its usage but has no budget, like it has no time limit.
        var usage = new TaskUsageMeter(kind == CancelKind ? null : _options.Limits);
        var exec = new Execution(key, Guid.NewGuid().ToString("N"), kind == CancelKind,
            // A CancelTask run is not stopped by the shutdown: it records a cancellation somebody asked for, also one that arrives
            // while the host stops (its token would be cancelled from the start, and it could not write the CANCELED status).
            kind == CancelKind ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token),
            _options.Streaming.ChannelCapacity)
        {
            // A child of the operation's span; ends when the agent has returned and every event is stored.
            Activity = ServerTelemetry.StartExecution(kind, key.Tenant, taskId, contextId),
            Claim = claim,
            PushEnabled = pushEnabled,
            TaskStored = !isNewTask,
            Usage = usage,
        };
        var scope = ScopeKey(key.Tenant, taskId);
        var offload = _objects is null ? null : new ArtifactOffload(_objects, _options.ArtifactStorage, key.Tenant, taskId);
        TaskUpdater updater = null!;
        updater = new TaskUpdater(taskId, contextId, isNewTask, exec.Incoming.Writer, requestMetadata, offload, usage,
            () => BudgetExceededAsync(exec, updater));
        // The first history entry of a new task, copied before the executor starts: the executor may change its message.
        var historyMessage = isNewTask && userMessage is not null ? WithTask(userMessage, taskId) : null;
        // Here first, then cluster-wide: whenever the coordinator lists the execution, this instance has it too.
        _running.Add(scope, exec);
        ITimer? heartbeat = null;
        ITimer? timer;
        try
        {
            await RegisterAsync(exec).ConfigureAwait(false);
            heartbeat = _time.CreateTimer(static state => { var (engine, e) = ((TaskExecutionEngine, Execution))state!; _ = engine.HeartbeatAsync(e); },
                (this, exec), _options.Coordination.Heartbeat, _options.Coordination.Heartbeat);
            // Started before the agent, so the time spent waiting for a thread counts too. Disposed when the agent returns.
            // A limit too large for a timer is off (TimeLimits.IsTimeLimit).
            timer = timeLimit is { } limit && TimeLimits.IsTimeLimit(limit)
                ? _time.CreateTimer(static state => { var (engine, e, u) = ((TaskExecutionEngine, Execution, TaskUpdater))state!; _ = engine.TimeOutAsync(e, u); },
                    (this, exec, updater), limit, Timeout.InfiniteTimeSpan)
                : null;
        }
        catch (Exception ex)
        {
            // The agent never started: undo the registration, stop the heartbeat and release the agent's lease, or the
            // execution would stay running (and its lease renewed) for good.
            A2ATelemetry.RecordError(exec.Activity, ex);
            if (heartbeat is not null) await heartbeat.DisposeAsync().ConfigureAwait(false);
            await UnregisterAsync(exec).ConfigureAwait(false);
            _running.Remove(scope, exec);
            exec.EndActivity();
            exec.MarkDone();
            await agent.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        // From here the execution owns the agent's lease and disposes it when the agent returns.
        var lease = agent.TakeLease();
        var slot = agent.TakeSlot();
        var producer = Task.Run(async () =>
        {
            if (exec.Activity is not null) Activity.Current = exec.Activity; // the agent's own spans nest under the run
            try
            {
                await using (lease ?? default)
                {
                    agent.Error?.Throw(); // the factory failed: as if the agent had thrown
                    var executor = agent.Executor ?? throw new InvalidOperationException($"No agent executor is configured for tenant '{key.Tenant}'.");
                    await run(executor, updater, exec.Cts.Token).ConfigureAwait(false);
                }
                exec.TryMarkReturned();
            }
            catch (Exception) when (!exec.TryMarkReturned())
            {
                // The task already failed with the time limit text; the agent's late error or cancellation is expected.
            }
            catch (OperationCanceledException) when (exec.Cts.IsCancellationRequested)
            {
                exec.Activity?.SetTag(A2ATelemetry.ErrorTypeCanceledTag, true);
                // Only the engine's shutdown fired the token: nobody cancelled the task, so it stays as it is (non-terminal) for
                // the lease expiry, the sweep or a continuation.
                if (exec.CancelRequested)
                    await TryFinishAsync(updater, TaskState.Canceled, null).ConfigureAwait(false);
            }
            catch (ChannelClosedException ex) when (exec.Failed)
            {
                // The task's events could not be stored, which is already reported; the engine stopped reading and closed the
                // agent's channel, so the agent's next write failed. Not an agent failure.
                ServerLog.ExecutorStoppedAfterStoreFailure(_logger, ex, taskId);
            }
            catch (Exception ex)
            {
                A2ATelemetry.RecordError(exec.Activity, ex);
                ServerLog.ExecutorFailed(_logger, ex, taskId);
                await TryFinishAsync(updater, TaskState.Failed, "The agent failed to process the request.").ConfigureAwait(false);
            }
            finally
            {
                updater.ReleaseHashState();
                if (timer is not null) await timer.DisposeAsync().ConfigureAwait(false);
                exec.Incoming.Writer.TryComplete();
            }
        });

        // Background writes outlive the request: the owner scope is fixed in the key.
        var consumer = Task.Run(() =>
        {
            if (exec.Activity is not null) Activity.Current = exec.Activity; // webhook deliveries of the run's events nest under it
            return ConsumeAsync(exec, key, contextId, isNewTask, historyMessage, push, emitInitialSnapshot, claim);
        });
        _ = FinishAsync(exec, scope, producer, consumer, heartbeat, slot);
        return exec;
    }

    /// <summary>After the agent returned and every event is stored: ends the lease (here and with the coordinator), then the execution.</summary>
    private async Task FinishAsync(Execution exec, string scope, Task producer, Task consumer, ITimer heartbeat, ExecutionSlot? slot)
    {
        try
        {
            try { await Task.WhenAll(producer, consumer).ConfigureAwait(false); }
            catch (Exception ex) { ServerLog.ExecutionEndedWithError(_logger, ex, exec.TaskId); }
            await heartbeat.DisposeAsync().ConfigureAwait(false);
            // Stopped only by the engine's shutdown with the task still open: keep the lease, so it expires and the sweep or a
            // continuation recovers the task (as after a crash). Deleting it would strand the task as WORKING.
            await UnregisterAsync(exec, keepLease: await EndedByShutdownLeavingTaskOpenAsync(exec).ConfigureAwait(false)).ConfigureAwait(false);
            _running.Remove(scope, exec);
        }
        finally
        {
            // After the agent returned and every event is stored: the slot is held for the whole run.
            slot?.Dispose();
        }
        exec.EndActivity();
        exec.MarkDone();
    }

    private async Task<bool> EndedByShutdownLeavingTaskOpenAsync(Execution exec)
    {
        if (exec.CancelRequested || !_shutdown.IsCancellationRequested || !exec.Registered) return false;
        try
        {
            var record = await _tasks.GetAsync(exec.Key, CancellationToken.None).ConfigureAwait(false);
            return record is not null && !record.Task.Status.State.IsTerminal();
        }
        catch (Exception)
        {
            return true; // unknown: a lease that expires is safe, a deleted one may strand the task
        }
    }

    /// <summary>
    /// The run lasted longer than <see cref="A2AServerOptions.MaxTaskDuration"/>: fails the task (the FAILED event is queued
    /// before the token fires, so it wins over a CANCELED from the agent's cancellation), then cancels the agent.
    /// </summary>
    private async Task TimeOutAsync(Execution exec, TaskUpdater updater)
    {
        if (!exec.TryMarkStopped()) return; // the agent returned first
        if (exec.Activity is { } activity)
        {
            activity.SetTag(A2ATelemetry.Tags.TimedOut, true);
            activity.SetTag(A2ATelemetry.Tags.ErrorType, "TimeLimit");
            activity.SetStatus(ActivityStatusCode.Error, "TimeLimit");
        }
        ServerLog.TaskTimedOut(_logger, exec.TaskId, _options.MaxTaskDuration);
        await TryFinishAsync(updater, TaskState.Failed, TimeLimitText).ConfigureAwait(false);
        CancelAll([exec]);
    }

    /// <summary>
    /// A usage report took the task past its budget (see <see cref="TaskUpdater.ReportUsageAsync"/>): fails the task (the FAILED
    /// event is queued before the token fires, so it wins over a CANCELED from the agent's cancellation), then cancels the agent.
    /// Goes through the same outcome compare-and-swap as the time limit, so the agent's late error or cancel is swallowed.
    /// </summary>
    private async ValueTask BudgetExceededAsync(Execution exec, TaskUpdater updater)
    {
        if (!exec.TryMarkStopped()) return; // the agent returned first, or the time limit stopped the run already
        if (exec.Activity is { } activity)
        {
            activity.SetTag(A2ATelemetry.Tags.ErrorType, "BudgetExceeded");
            activity.SetStatus(ActivityStatusCode.Error, "BudgetExceeded");
        }
        ServerTelemetry.BudgetExceeded.Add(1);
        ServerLog.TaskBudgetExceeded(_logger, exec.TaskId);
        await TryFinishAsync(updater, TaskState.Failed, BudgetExceededText).ConfigureAwait(false);
        CancelAll([exec]);
    }

    private static async Task TryFinishAsync(TaskUpdater updater, TaskState state, string? text)
    {
        if (updater.IsTerminal || updater.HasReplied) return;
        try { await updater.UpdateStatusAsync(state, text is null ? null : updater.NewAgentMessage(text)).ConfigureAwait(false); }
        catch (Exception) { /* channel closed or terminal already */ }
    }

    private async Task ConsumeAsync(Execution exec, TaskKey key, string contextId, bool isNewTask, Message? historyMessage,
        TaskPushNotificationConfig? push, bool emitInitialSnapshot, IdempotencyClaim claim)
    {
        var taskId = key.TaskId;
        var scope = ScopeKey(key.Tenant, taskId);
        var created = !isNewTask;
        try
        {
            if (emitInitialSnapshot)
            {
                // Snapshot of the existing task, stamped with the latest sequence. It is not a new event, so it is not appended.
                var snapshotGate = _locks.For(key.Tenant, taskId);
                await snapshotGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var (current, sequence) = await ReadTaskAtSequenceAsync(key, CancellationToken.None).ConfigureAwait(false);
                    if (current is not null)
                        exec.Emit(new SequencedEvent(sequence, StreamResponse.Of(current.Task)));
                }
                finally
                {
                    snapshotGate.Release();
                }
            }

            var gate = _locks.For(key.Tenant, taskId);
            var reader = exec.Incoming.Reader;
            // Events read from the channel for a batch but not stored by it: they come before anything still in the channel.
            Queue<StreamResponse>? pending = null;
            while (true)
            {
                StreamResponse produced;
                if (pending is { Count: > 0 }) produced = pending.Dequeue();
                else if (!reader.TryRead(out produced!))
                {
                    if (!await reader.WaitToReadAsync().ConfigureAwait(false)) break;
                    continue;
                }
                // Encoded once: the size check, the event store, streams and webhooks all use these bytes.
                var encoded = Encode(produced);
                if (encoded.Json.Length > _options.Limits.MaxEventBytes)
                {
                    // The event is never stored or sent: the task fails instead and the agent is stopped.
                    ServerLog.EventTooLarge(_logger, taskId, _options.Limits.MaxEventBytes);
                    encoded = Encode(StreamResponse.Of(FailureEvent(taskId, contextId)));
                    CancelAll([exec]);
                }
                var evt = encoded.Event;

                if (evt is MessageEvent reply)
                {
                    // The idempotency store owns its copy; the reply itself goes to the waiting request.
                    await _idempotency.RecordAsync(claim, new IdempotencyOutcome.DirectMessage(reply.Message.DeepCopy()), taskId).ConfigureAwait(false);
                    exec.Emit(encoded); // direct reply (sequence 0), no task, not stored
                    continue;
                }

                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    TaskRecord? record;
                    if (!created)
                    {
                        var now = DateTimeOffset.UtcNow;
                        var initial = new TaskRecord(
                            key.Tenant, key.Owner,
                            new AgentTask
                            {
                                Id = taskId,
                                ContextId = contextId,
                                Status = new AgentTaskStatus { State = TaskState.Submitted, Timestamp = (evt as StatusUpdateEvent)?.StatusUpdate.Status.Timestamp ?? now },
                                History = historyMessage is null ? null : [historyMessage],
                                Metadata = historyMessage is not null && ActorChainValidator.WellFormedChain(historyMessage) is { } chain
                                    ? new() { [A2A.Delegation.ActorChain.MetadataKey] = chain }
                                    : null,
                            },
                            Version: 0, CreatedAt: now, UpdatedAt: now);
                        // The task, its first event (the snapshot) and the snapshot's push delivery are committed together. The
                        // push config is saved first, so the delivery service always finds the config of a queued delivery. The
                        // snapshot shares the stored instance: nobody changes either (the next update stores a copy).
                        var snapshot = EventEncoder.Encode(0, StreamResponse.Of(initial.Task));
                        IReadOnlyList<PushDelivery> deliveries = push is null || !exec.PushEnabled ? [] : [NewDelivery(key, push.Id!)];
                        if (push is not null) await _pushConfigs.SaveInlineAsync(key, push, CancellationToken.None).ConfigureAwait(false);
                        CommitResult committed;
                        try
                        {
                            committed = await _tasks.CommitAsync(new TaskCommit(initial, 0, snapshot, deliveries)
                            {
                                // A store that shares a transaction with the idempotency store writes the outcome with the task.
                                Idempotency = claim.ForCommit(new IdempotencyOutcome.TaskCreated(taskId)),
                            }).ConfigureAwait(false);
                        }
                        catch when (push is not null)
                        {
                            await _pushConfigs.TryDeleteAsync(key, push.Id!).ConfigureAwait(false); // no task, so no config
                            throw;
                        }
                        if (deliveries.Count > 0) _outbox.NotifyPending();
                        ServerTelemetry.Events.Add(1);
                        record = committed.Record;
                        created = true;
                        exec.TaskStored = true;
                        Publish(exec, scope, snapshot with { Sequence = committed.Sequence });
                        // Retries of this request now attach to the task (its first event is stored, so a replay starts complete).
                        // Needed for stores that cannot write the outcome in the commit; where they can, this changes nothing.
                        await _idempotency.RecordAsync(claim, new IdempotencyOutcome.TaskCreated(taskId), taskId).ConfigureAwait(false);
                    }
                    else
                    {
                        record = exec.Committed ?? await _tasks.GetAsync(key).ConfigureAwait(false);
                        if (record is null)
                        {
                            ServerLog.TaskDisappeared(_logger, taskId);
                            continue;
                        }
                    }
                    var mayBeStale = ReferenceEquals(record, exec.Committed);

                    // Artifact chunks queued behind this one are applied to one copy of the task and committed together.
                    // Only when nothing read earlier is still pending: the channel's events come after those.
                    if (created && _batchCommit is not null && pending is not { Count: > 0 } && IsBatchable(evt)
                        && reader.TryPeek(out var queued) && IsBatchable(queued))
                    {
                        var batch = new List<SequencedEvent>(8) { encoded };
                        while (batch.Count < MaxCommitBatch && reader.TryPeek(out queued) && IsBatchable(queued) && reader.TryRead(out queued))
                        {
                            var next = Encode(queued);
                            if (next.Json.Length > _options.Limits.MaxEventBytes)
                            {
                                (pending ??= new()).Enqueue(queued); // the single path fails the task for it
                                break;
                            }
                            batch.Add(next);
                        }
                        var storedCount = await ApplyBatchAndStoreAsync(key, record, batch, exec, scope).ConfigureAwait(false);
                        if (storedCount > 0 || batch.Count > 1)
                        {
                            // The events the batch did not store go first, then those already waiting, in order.
                            var rest = new Queue<StreamResponse>();
                            for (var i = Math.Max(storedCount, 0); i < batch.Count; i++) rest.Enqueue(batch[i].Event);
                            if (pending is not null) while (pending.Count > 0) rest.Enqueue(pending.Dequeue());
                            pending = rest;
                            continue;
                        }
                    }

                    if (await ApplyAndStoreAsync(key, record, encoded, exec.PushEnabled, exec: exec, recordMayBeStale: mayBeStale).ConfigureAwait(false) is not { } stored) continue;

                    // Still under the task lock, so sinks see events in sequence order even with concurrent executions.
                    // Every sink is non-blocking.
                    Publish(exec, scope, stored);
                    // The stored event is not the produced one when the task grew past its cap.
                    if (exec.Activity is not null && stored.Event is StatusUpdateEvent { StatusUpdate.Status.State: var recorded })
                        exec.Activity.SetTag(A2ATelemetry.Tags.TaskState, A2ATelemetry.TaskStateValue(recorded));
                    if (stored.Event is StatusUpdateEvent { StatusUpdate.Status.State: var st } && st.IsTerminal())
                    {
                        await _events.MarkTerminalAsync(key).ConfigureAwait(false);
                        _bus.Complete(scope);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
        }
        catch (Exception ex)
        {
            ServerLog.ApplyEventsFailed(_logger, ex, taskId);
            A2ATelemetry.RecordError(exec.Activity, ex);
            CancelAll([exec]);
            // Before the FAILED event is published: the waiting request must see the error, not that event.
            exec.Fail(new A2AException(A2AErrorCode.Internal));
            await TryFailStoredTaskAsync(key, exec.PushEnabled).ConfigureAwait(false);
        }
        finally
        {
            // No task and no reply (for example the agent produced nothing): retries may run the request again.
            await _idempotency.AbandonAsync(claim, taskId).ConfigureAwait(false);
            // A storage failure (above) is an error for the request that is waiting, never a clean end of its stream.
            exec.Outgoing.Writer.TryComplete();
            // Nobody reads the agent's events any more: an agent waiting for room in the bounded channel gets an error
            // instead of waiting forever. (A no-op after a normal end: the agent completed the channel.)
            exec.Incoming.Writer.TryComplete();
        }
    }

    /// <summary>
    /// After events could not be stored, marks the task FAILED so it is not left in a non-terminal state with no agent running.
    /// Best effort: if the store is still failing, the task stays as it is. Subscribers and webhooks are told; the request that
    /// was running gets the error instead.
    /// </summary>
    private async Task TryFailStoredTaskAsync(TaskKey key, bool pushEnabled)
    {
        try
        {
            // Its own execution is still running here, so the commit must not wait for it to end.
            await CommitSystemStatusCoreAsync(key, TaskState.Failed, StoreFailureText, precondition: null, evenIfRunningHere: true, pushEnabled,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServerLog.MarkFailedFailed(_logger, ex, key.TaskId);
        }
    }

    /// <summary>
    /// After a follow-up's message was committed but its execution could not start, marks the task FAILED
    /// (<see cref="StartFailureText"/>) so it does not wait for good for an agent that never runs. Only while the task still has
    /// <paramref name="acceptedStatus"/> (nothing changed it since) and no execution of it runs here. Best effort: a failure is
    /// logged, and the caller reports the start error.
    /// </summary>
    internal async Task TryFailUnstartedAsync(TaskKey key, AgentTaskStatus acceptedStatus)
    {
        try
        {
            await CommitSystemStatusAsync(key, TaskState.Failed, StartFailureText,
                record => record.Task.Status.State == acceptedStatus.State && record.Task.Status.Timestamp == acceptedStatus.Timestamp,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServerLog.MarkUnstartedFailedFailed(_logger, ex, key.TaskId);
        }
    }

    /// <summary>
    /// Commits a status change that no agent made (the retention sweep: interrupted-state timeout, dead execution) through the
    /// normal path: under the task lock, applied and committed with its push deliveries, then published to subscribers; a
    /// terminal state also ends the task's streams. The status message is an agent message with <paramref name="text"/>.
    /// Nothing is written while an execution of the task runs on this instance (it may not be registered with the
    /// coordinator yet, or its registration failed).
    /// </summary>
    /// <param name="key">The task.</param>
    /// <param name="state">The new state. Must be a valid transition from the current one, or nothing is written.</param>
    /// <param name="text">The status message text.</param>
    /// <param name="precondition">
    /// When set, the change applies only while the stored record (read under the lock, and again after a version conflict)
    /// satisfies it. Use it to require the version the caller looked at, so a task that changed since is left alone.
    /// </param>
    /// <param name="ct">Cancels waiting for the task lock and the first read.</param>
    /// <returns>
    /// True when the change was committed; false when the task is missing, terminal, runs here, fails the precondition, or the
    /// transition is invalid.
    /// </returns>
    internal Task<bool> CommitSystemStatusAsync(TaskKey key, TaskState state, string text, Func<TaskRecord, bool>? precondition = null, CancellationToken ct = default) =>
        CommitSystemStatusCoreAsync(key, state, text, precondition, evenIfRunningHere: false, pushEnabled: null, ct);

    // pushEnabled: the push capability of the task's tenant; null resolves it from the tenant, before the task lock.
    private async Task<bool> CommitSystemStatusCoreAsync(TaskKey key, TaskState state, string text, Func<TaskRecord, bool>? precondition,
        bool evenIfRunningHere, bool? pushEnabled, CancellationToken ct)
    {
        // Never ask the card provider under the task lock: the locks are shared by many tasks.
        var push = pushEnabled ?? await PushEnabledForTenantAsync(key, ct).ConfigureAwait(false);
        var scope = ScopeKey(key.Tenant, key.TaskId);
        using (await _locks.AcquireAsync(key, ct).ConfigureAwait(false))
        {
            if (!evenIfRunningHere && _running.Snapshot(scope).Any(exec => exec.Key == key)) return false;
            var record = await _tasks.GetAsync(key, ct).ConfigureAwait(false);
            if (record is null || record.Task.Status.State.IsTerminal()) return false;
            var evt = Encode(StreamResponse.Of(StatusEvent(key.TaskId, record.Task.ContextId, state, text, _time.GetUtcNow())));
            if (await ApplyAndStoreAsync(key, record, evt, push, precondition).ConfigureAwait(false) is not { } stored) return false;
            _bus.Publish(scope, stored);
            if (state.IsTerminal())
            {
                await _events.MarkTerminalAsync(key, CancellationToken.None).ConfigureAwait(false);
                _bus.Complete(scope);
            }
            return true;
        }
    }

    /// <summary>
    /// The push capability of the task's tenant for a commit no request made (the retention sweep), from the tenant alone. No
    /// agent for the tenant means no push. A failing provider fails closed: no push deliveries for this commit (logged).
    /// </summary>
    private async ValueTask<bool> PushEnabledForTenantAsync(TaskKey key, CancellationToken ct)
    {
        try
        {
            var card = await _cards.GetAsync(AgentRequestScope.ForTenant(key.Tenant), ct).ConfigureAwait(false);
            return card?.Capabilities.PushNotifications == true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ServerLog.TenantCardFailed(_logger, ex, key.TaskId);
            return false;
        }
    }

    /// <summary>
    /// Applies the event to a copy of the task, then commits the copy, the event and its push deliveries as one unit
    /// (<see cref="TaskStore.CommitAsync"/>; the caller holds the task lock, so the log holds only applied events, in the order
    /// their changes were stored, even across instances). After a version conflict, waits (<see cref="ConflictBackoffAsync"/>),
    /// reloads and re-applies, up to <see cref="MaxConflictRetries"/> times. Returns null when the event was dropped (the task is
    /// already terminal, or the record, also a reloaded one, fails <paramref name="precondition"/>). <paramref name="record"/>
    /// belongs to the store and is never changed. <paramref name="encoded"/> comes from <see cref="Encode"/>; the stored and
    /// returned event, and every push delivery, carry its bytes.
    /// </summary>
    private async ValueTask<SequencedEvent?> ApplyAndStoreAsync(TaskKey key, TaskRecord record, SequencedEvent encoded, bool pushEnabled,
        Func<TaskRecord, bool>? precondition = null, Execution? exec = null, bool recordMayBeStale = false)
    {
        if (exec is not null) exec.Committed = null; // set again only by a successful commit
        var evt = encoded.Event;
        var deliveries = await DeliveriesAsync(key, pushEnabled).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            if (precondition is not null && !precondition(record))
            {
                ServerLog.EventDropped(_logger, key.TaskId, record.Task.Status.State);
                return null;
            }
            // Two executions of one task can race: only here, on the fresh record, is the order final.
            if (evt is ArtifactUpdateEvent { ArtifactUpdate: { Append: true } sealedAppend } && !record.Task.Status.State.IsTerminal()
                && ArtifactSeal.IsSealed(record.Task.Artifacts, sealedAppend.Artifact.ArtifactId))
            {
                ServerLog.ArtifactAppendAfterSeal(_logger, key.TaskId, sealedAppend.Artifact.ArtifactId);
                return null;
            }
            if (ApplyToCopy(record.Task, evt) is not { } next)
            {
                ServerLog.EventDropped(_logger, key.TaskId, record.Task.Status.State);
                return null;
            }
            // A terminal state is exempt, so the failure event that replaces an oversized one can always be stored.
            // An event with no message, no metadata and no artifact adds no content, so the walk is skipped for it.
            if (!next.Status.State.IsTerminal() && IsOverCap(record.Task, next, evt, exec))
            {
                ServerLog.TaskTooLarge(_logger, key.TaskId, _options.Limits.MaxTaskBytes, _options.Limits.MaxArtifactsPerTask);
                encoded = Encode(StreamResponse.Of(FailureEvent(key.TaskId, record.Task.ContextId, OversizedTaskText)));
                evt = encoded.Event;
                if (exec is not null) CancelAll([exec]);
                if (ApplyToCopy(record.Task, evt) is not { } failed)
                {
                    ServerLog.EventDropped(_logger, key.TaskId, record.Task.Status.State);
                    return null;
                }
                next = failed;
            }
            // After the final next is decided (also the failure event of an oversized event), before the commit; on a conflict
            // retry this runs again on a fresh record. The usage is marked stored only once the commit succeeded.
            var usage = exec?.Usage.Pending() ?? default;
            TaskUsageMeter.Apply(next, usage);
            try
            {
                var committed = await _tasks.CommitAsync(new TaskCommit(record with { Task = next }, record.Version, encoded, deliveries)).ConfigureAwait(false);
                exec?.Usage.MarkStored(usage);
                if (exec is not null)
                {
                    exec.Committed = committed.Record;
                    // A store may return its own instance of the task (one it read back): the size carried over still holds for it.
                    if (ReferenceEquals(exec.MeasuredTask, next)) exec.MeasuredTask = committed.Record.Task;
                }
                if (deliveries.Count > 0) _outbox.NotifyPending();
                ServerTelemetry.Events.Add(1);
                // A webhook saved after the listing above is not among the deliveries. For an event that ends or interrupts the
                // task nothing follows, so list again: a config registered before this commit has read the task (see
                // CatchUpAsync) and will not see a later state, but is found here. No lock: it saved before it read, and it
                // read before this commit, so it saved before this listing.
                if (pushEnabled && _options.PushNotifications.CatchUpOnRegistration && PushCatchUp.IsFinalForNow(committed.Record.Task.Status.State))
                    await CatchUpLateConfigsAsync(key, deliveries, committed).ConfigureAwait(false);
                return encoded with { Sequence = committed.Sequence };
            }
            catch (ConcurrencyException) when (attempt < MaxConflictRetries)
            {
                if (recordMayBeStale)
                {
                    // The record was this run's last commit, not a fresh read: another writer may have committed since. Reload
                    // at once, without a backoff and without using up a retry.
                    recordMayBeStale = false;
                    attempt--;
                }
                // Sleeps under the striped task lock (inherited from main), so tasks on the same stripe wait too. Acceptable:
                // conflicts come only from other instances (writers here share the lock), so this is rare and short.
                else await ConflictBackoffAsync(attempt, CancellationToken.None).ConfigureAwait(false);
                var fresh = await _tasks.GetAsync(key).ConfigureAwait(false);
                if (fresh is null)
                {
                    ServerLog.TaskDisappeared(_logger, key.TaskId);
                    return null;
                }
                record = fresh;
            }
        }
    }

    /// <summary>Most events in one batch commit.</summary>
    internal const int MaxCommitBatch = 64;

    /// <summary>
    /// Whether an event may go into a batch commit: an append of parts that adds nothing else (no name, description or
    /// metadata) and does not seal the artifact. Its effect on the task and on the task's size is then known without
    /// walking the task. Anything else is stored on its own.
    /// </summary>
    private static bool IsBatchable(StreamResponse evt) =>
        evt is ArtifactUpdateEvent { ArtifactUpdate: { LastChunk: false } } && TaskSize.AppendedBytes(evt) is not null;

    /// <summary>
    /// Applies the leading events of <paramref name="batch"/> (all batchable) to one copy of the task and commits them together
    /// (<see cref="IBatchTaskCommit"/>). Stops at the first event that the single path must handle: the task is terminal, the
    /// artifact is missing or sealed, the artifact is invalid, or the task would grow past its cap. Returns how many leading
    /// events were stored and published; 0 when fewer than two could be batched, or the commit lost a version race (nothing
    /// was stored). The caller stores the rest one by one. The caller holds the task lock.
    /// </summary>
    private async ValueTask<int> ApplyBatchAndStoreAsync(TaskKey key, TaskRecord record, List<SequencedEvent> batch, Execution exec, string scope)
    {
        if (batch.Count < 2 || record.Task.Status.State.IsTerminal()) return 0;
        var size = ReferenceEquals(exec.MeasuredTask, record.Task) ? exec.MeasuredBytes : TaskSize.Measure(record.Task);
        AgentTask? next = null;
        HashSet<int>? copied = null; // artifacts already replaced by a copy owned by this batch
        var count = 0;
        foreach (var e in batch)
        {
            var au = ((ArtifactUpdateEvent)e.Event).ArtifactUpdate;
            var artifacts = (next ?? record.Task).Artifacts;
            var index = IndexOfArtifact(artifacts, au.Artifact.ArtifactId);
            if (index < 0 || ArtifactSeal.IsSealed(artifacts![index])) break;
            try { A2AValidator.ValidateArtifact(au.Artifact, "artifact", allowEmptyParts: true); }
            catch (A2AException) { break; } // the single path refuses it
            var added = TaskSize.AppendedBytes(e.Event)!.Value;
            if (size + added > _options.Limits.MaxTaskBytes) break; // the single path fails the task for it

            next ??= record.Task.CopyForUpdate();
            if ((copied ??= []).Add(index))
            {
                // As in ApplyToCopy: the stored artifact is never changed; the copy belongs to this batch.
                var merged = next.Artifacts![index].CopyWithParts(au.Artifact.Parts);
                ArtifactSeal.Set(merged, false);
                next.Artifacts[index] = merged;
            }
            else next.Artifacts![index].Parts.AddRange(au.Artifact.Parts);
            size += added;
            count++;
        }
        if (count < 2) return 0;

        var usage = exec.Usage.Pending();
        TaskUsageMeter.Apply(next!, usage);
        var configs = await DeliveriesAsync(key, exec.PushEnabled).ConfigureAwait(false);
        var events = new BatchedEvent[count];
        for (var i = 0; i < count; i++)
        {
            IReadOnlyList<PushDelivery> deliveries = configs;
            if (i > 0 && configs.Count > 0)
            {
                var fresh = new PushDelivery[configs.Count]; // a delivery id per event
                for (var j = 0; j < fresh.Length; j++) fresh[j] = NewDelivery(key, configs[j].ConfigId);
                deliveries = fresh;
            }
            events[i] = new BatchedEvent(batch[i], deliveries);
        }
        BatchCommitResult committed;
        try
        {
            committed = await _batchCommit!.CommitBatchAsync(new TaskBatchCommit(record with { Task = next! }, record.Version, events)).ConfigureAwait(false);
        }
        catch (ConcurrencyException)
        {
            exec.Committed = null; // the single path reloads the task
            return 0;
        }
        exec.Usage.MarkStored(usage);
        exec.Committed = committed.Record;
        exec.MeasuredTask = committed.Record.Task;
        exec.MeasuredBytes = size;
        if (configs.Count > 0) _outbox.NotifyPending();
        ServerTelemetry.Events.Add(count);
        // Still under the task lock, so sinks see events in sequence order.
        for (var i = 0; i < count; i++) Publish(exec, scope, batch[i] with { Sequence = committed.FirstSequence + i });
        return count;
    }

    /// <summary>
    /// Whether <paramref name="next"/> (the task after <paramref name="evt"/>) is over a cap. An event with no message, no metadata and
    /// no artifact adds no content, so it is not measured. With <paramref name="exec"/>, the size is carried from one stored task
    /// to the next: an append of parts is the old size plus the parts, anything else walks the task.
    /// </summary>
    private bool IsOverCap(AgentTask previous, AgentTask next, StreamResponse evt, Execution? exec)
    {
        var known = exec is not null && ReferenceEquals(exec.MeasuredTask, previous);
        if (evt is StatusUpdateEvent { StatusUpdate: { Status.Message: null, Metadata: null or { Count: 0 } } })
        {
            if (known) exec!.MeasuredTask = next; // nothing added: same size
            return false;
        }
        if (next.Artifacts?.Count > _options.Limits.MaxArtifactsPerTask) return true;
        var size = known && TaskSize.AppendedBytes(evt) is { } added ? exec!.MeasuredBytes + added : TaskSize.Measure(next);
        if (exec is not null)
        {
            exec.MeasuredTask = next;
            exec.MeasuredBytes = size;
        }
        return size > _options.Limits.MaxTaskBytes;
    }

    /// <summary>
    /// Queues the catch-up delivery (the stored task at the commit's sequence) for each config that was saved after
    /// <paramref name="queued"/> was listed. Best effort: the event is committed, so a failure here is logged, not thrown.
    /// </summary>
    private async ValueTask CatchUpLateConfigsAsync(TaskKey key, IReadOnlyList<PushDelivery> queued, CommitResult committed)
    {
        try
        {
            var configs = await _pushConfigs.Store.ListAsync(key).ConfigureAwait(false);
            var late = configs.Where(c => queued.All(d => !string.Equals(d.ConfigId, c.Config.Id, StringComparison.Ordinal))).ToList();
            foreach (var config in late)
                await QueueCatchUpAsync(key, config.Config.Id!, committed.Record.Task, committed.Sequence, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServerLog.PushCatchUpFailed(_logger, ex, key.TaskId);
        }
    }

    /// <summary>
    /// Reads the task and a sequence it is at least as new as. The task lock is local to one instance, so another instance can
    /// commit between the two reads. Each try reads the last sequence, the task, then the last sequence again, and stops when
    /// the task is missing or the sequence did not change (at most 5 tries). The result is always the sequence read BEFORE the
    /// last task read, also when the tries run out: every event up to it was committed before the task was read, so the task
    /// holds it. The worst case is a live event the stream sends twice, never one it loses.
    /// </summary>
    internal async ValueTask<(TaskRecord? Record, long Sequence)> ReadTaskAtSequenceAsync(TaskKey key, CancellationToken ct)
    {
        TaskRecord? record = null;
        long before = 0;
        for (var i = 0; i < 5; i++)
        {
            before = await _events.LastSequenceAsync(key, ct).ConfigureAwait(false);
            record = await _tasks.GetAsync(key, ct).ConfigureAwait(false);
            if (record is null) break;
            if (await _events.LastSequenceAsync(key, ct).ConfigureAwait(false) == before) break;
        }
        return (record, before);
    }

    /// <summary>
    /// A webhook was just registered (its config is saved): when the task is terminal or interrupted, queues one delivery of the
    /// task snapshot for that config, at the task's latest event sequence (see <see cref="ReadTaskAtSequenceAsync"/>). Does nothing for a
    /// running task (its next event reaches the config), a missing task, or when
    /// <see cref="PushNotificationOptions.CatchUpOnRegistration"/> is off.
    /// </summary>
    internal async ValueTask CatchUpAsync(TaskKey key, string configId, CancellationToken cancellationToken)
    {
        if (!_options.PushNotifications.CatchUpOnRegistration) return;
        var (record, sequence) = await ReadTaskAtSequenceAsync(key, cancellationToken).ConfigureAwait(false);
        if (record is null || !PushCatchUp.IsFinalForNow(record.Task.Status.State)) return;
        await QueueCatchUpAsync(key, configId, record.Task, sequence, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queues the catch-up delivery of <paramref name="task"/> at <paramref name="sequence"/> for one config; a no-op when it is queued already.</summary>
    private async ValueTask QueueCatchUpAsync(TaskKey key, string configId, AgentTask task, long sequence, CancellationToken cancellationToken)
    {
        var payload = EventEncoder.Encode(sequence, StreamResponse.Of(task)).Json;
        if (await _outbox.EnqueueAsync(PushCatchUp.NewDelivery(key, configId, sequence), payload, cancellationToken).ConfigureAwait(false))
            _outbox.NotifyPending();
    }

    /// <summary>
    /// One push delivery per push config of the task, for the event about to be committed. None when the card of the task's
    /// tenant has no push notifications (<paramref name="pushEnabled"/>, resolved before the task lock).
    /// </summary>
    private async ValueTask<IReadOnlyList<PushDelivery>> DeliveriesAsync(TaskKey key, bool pushEnabled)
    {
        if (!pushEnabled) return [];
        var configs = await _pushConfigs.Store.ListAsync(key).ConfigureAwait(false);
        if (configs.Count == 0) return [];
        var deliveries = new PushDelivery[configs.Count];
        for (var i = 0; i < configs.Count; i++) deliveries[i] = NewDelivery(key, configs[i].Config.Id!);
        return deliveries;
    }

    /// <summary>
    /// A new delivery (the store sets its sequence) with a fresh delivery id, the same on every attempt, and the trace context
    /// of the current agent run, so its delivery span joins that trace.
    /// </summary>
    private static PushDelivery NewDelivery(TaskKey key, string configId)
    {
        string? traceParent = null;
        A2ATelemetry.InjectTraceContext(Activity.Current, (name, value) =>
        {
            if (name == A2ATelemetry.TraceParentHeader) traceParent = value;
        });
        return new PushDelivery(key, configId, 0, Guid.NewGuid().ToString("N")) { TraceParent = traceParent };
    }

    /// <summary>
    /// Encodes an event the agent produced (or a failure event), once. A status update without a timestamp gets one first: the
    /// stored task, the stored event and its bytes then all carry the same time, and nothing changes the event afterwards.
    /// </summary>
    private static SequencedEvent Encode(StreamResponse evt)
    {
        if (evt is StatusUpdateEvent { StatusUpdate.Status: var status })
        {
            status.Timestamp ??= DateTimeOffset.UtcNow;
            // The message goes into the history with the time of the status. A copy: the agent's message and its metadata stay as they are.
            if (status.Message is { } message) status.Message = MessageStamp.Copy(message, status.Timestamp.Value);
        }
        return EventEncoder.Encode(0, evt);
    }

    /// <summary>Sends a stored event to the request that started the run and to subscribers. Its webhooks were queued by its commit.</summary>
    private void Publish(Execution exec, string scope, SequencedEvent evt)
    {
        exec.Emit(evt);
        _bus.Publish(scope, evt);
    }

    /// <summary>
    /// Returns a copy of <paramref name="current"/> (<see cref="ModelCopies.CopyForUpdate"/>) with the event applied, or null
    /// when the event must be dropped: the task is terminal, the status transition is not allowed, or the event is not a
    /// status or artifact update. Never changes <paramref name="current"/> or anything reachable from it; an appended artifact
    /// is replaced in the copy by a new artifact. The event's status message and artifact go into the copy as they are: from
    /// here on the event and the stored task share them, and the server owns both (see <see cref="TaskUpdater"/>).
    /// The event's own <c>Metadata</c> (status or artifact update event) is shallow-merged into the task's metadata, last write
    /// wins; <see cref="MergeMetadata"/> says which keys are skipped.
    /// </summary>
    internal static AgentTask? ApplyToCopy(AgentTask current, StreamResponse evt)
    {
        if (current.Status.State.IsTerminal()) return null;
        if (evt is StatusUpdateEvent { StatusUpdate: var su })
        {
            if (!TaskStateMachine.CanTransition(current.Status.State, su.Status.State)) return null;
            // su.Status.Timestamp was set before the event was encoded (Encode); the event is not changed here.
            var next = current.CopyForUpdate();
            next.Status = new AgentTaskStatus { State = su.Status.State, Message = su.Status.Message, Timestamp = su.Status.Timestamp };
            if (su.Status.Message is { } m) (next.History ??= []).Add(m);
            MergeMetadata(next, su.Metadata);
            return next;
        }
        if (evt is ArtifactUpdateEvent { ArtifactUpdate: var au })
        {
            var incoming = au.Artifact;
            var existing = IndexOfArtifact(current.Artifacts, incoming.ArtifactId);
            // An empty part list is valid only to seal an artifact or update its metadata: an append to one that exists.
            A2AValidator.ValidateArtifact(incoming, "artifact", allowEmptyParts: au.Append && existing >= 0);
            var next = current.CopyForUpdate();
            var artifacts = next.Artifacts ??= [];
            if (existing < 0) artifacts.Add(ArtifactSeal.Stamp(incoming, au.LastChunk));
            else if (au.Append)
            {
                // A new artifact replaces the stored one in the copy; the stored one is never changed.
                var merged = artifacts[existing].CopyWithParts(incoming.Parts);
                merged.Name ??= incoming.Name;
                merged.Description ??= incoming.Description;
                if (incoming.Metadata is not null)
                {
                    merged.Metadata ??= new();
                    foreach (var kv in incoming.Metadata)
                    {
                        if (kv.Key == A2AProtocol.ArtifactSealedKey) continue; // only the server sets it
                        merged.Metadata[kv.Key] = kv.Value;
                    }
                }
                ArtifactSeal.Set(merged, au.LastChunk);
                artifacts[existing] = merged;
            }
            else artifacts[existing] = ArtifactSeal.Stamp(incoming, au.LastChunk); // replaces, also a sealed artifact
            MergeMetadata(next, au.Metadata);
            return next;
        }
        return null;
    }

    /// <summary>The index of the artifact with this id, or -1. A loop: no closure per event.</summary>
    private static int IndexOfArtifact(List<Artifact>? artifacts, string artifactId)
    {
        if (artifacts is null) return -1;
        for (var i = 0; i < artifacts.Count; i++)
            if (artifacts[i].ArtifactId == artifactId) return i;
        return -1;
    }

    /// <summary>
    /// Shallow merge of an event's metadata into the task copy: a key adds or overwrites, a key not in the update stays, null is a
    /// value. Values are cloned. The server-owned keys are skipped, so an agent cannot reset an approval or fake a cancel.
    /// Nothing is created when nothing is merged.
    /// </summary>
    private static void MergeMetadata(AgentTask next, Dictionary<string, JsonElement>? update)
    {
        if (update is null) return;
        foreach (var kv in update)
        {
            if (kv.Key is A2AProtocol.ApprovalConsumedKey or A2AProtocol.CancelRequestedKey or A2A.Delegation.ActorChain.MetadataKey
                or A2A.Elicitations.Elicitation.AnsweredKey or A2ARequestHandler.PendingStartKey or TaskUsageMeter.MetadataKey) continue;
            (next.Metadata ??= [])[kv.Key] = kv.Value.Clone();
        }
    }

    private static TaskStatusUpdateEvent FailureEvent(string taskId, string contextId, string text = OversizedEventText) =>
        StatusEvent(taskId, contextId, TaskState.Failed, text, DateTimeOffset.UtcNow);

    private static TaskStatusUpdateEvent StatusEvent(string taskId, string contextId, TaskState state, string text, DateTimeOffset at) => new()
    {
        TaskId = taskId,
        ContextId = contextId,
        Status = new AgentTaskStatus
        {
            State = state,
            Timestamp = at,
            Message = new Message
            {
                MessageId = Message.NewId(), Role = Role.Agent, Parts = [Part.FromText(text)], TaskId = taskId, ContextId = contextId,
            },
        },
    };

    /// <summary>A copy of <paramref name="m"/> bound to the task, for the stored history.</summary>
    private static Message WithTask(Message m, string taskId)
    {
        var copy = m.DeepCopy();
        copy.TaskId = taskId;
        return copy;
    }

    /// <summary>Waits for all running executions to finish. For tests and graceful shutdown.</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            var all = _running.All().Select(e => e.Completion).ToList();
            if (all.Count == 0) return;
            await Task.WhenAll(all).ConfigureAwait(false);
        }
    }

    /// <summary>Asks every running execution to stop, without disposing the engine. Safe to call more than once, and after <see cref="Dispose"/>.</summary>
    internal void Shutdown()
    {
        _admission.Close();
        try { _shutdown.Cancel(); }
        catch (ObjectDisposedException) { /* disposed: already cancelled */ }
    }

    /// <summary>Stops running executions and releases resources. Does not wait for the executions to end (see <see cref="WhenIdleAsync"/>). Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _admission.Close();
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    /// <summary>As <see cref="Dispose"/>.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
