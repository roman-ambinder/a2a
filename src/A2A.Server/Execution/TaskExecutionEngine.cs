using System.Text.Json;
using System.Threading.Channels;
using A2A.Server.Idempotency;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using A2A.Server.Telemetry;
using A2A.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.Server.Execution;

/// <summary>
/// Runs agents for tasks. Each event an agent produces is applied to a copy of the task, stored, appended to the event log and
/// then published (to the request that started the run, to stream subscribers and to webhooks), all under the task's lock, so
/// publish order is sequence order. In an ASP.NET Core app the DI container creates and disposes it.
/// </summary>
public sealed class TaskExecutionEngine : IDisposable, IAsyncDisposable
{
    private readonly TaskStore _tasks;
    private readonly EventStore _events;
    private readonly IAgentExecutorFactory _executors;
    private readonly TaskEventBus _bus;
    private readonly IPushNotificationSender _push;
    private readonly IdempotencyCoordinator _idempotency;
    private readonly PushConfigService _pushConfigs;
    private readonly A2AServerOptions _options;
    private readonly IAgentCardProvider _cards;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskLocks _locks = new();
    private readonly RunningRegistry<Execution> _running = new();
    private int _disposed;

    /// <summary>Creates the engine. In an ASP.NET Core app the DI container does this; the parameters are the registered services.</summary>
    public TaskExecutionEngine(
        TaskStore tasks,
        EventStore events,
        IAgentExecutorFactory executors,
        TaskEventBus bus,
        IPushNotificationSender push,
        IdempotencyCoordinator idempotency,
        PushConfigService pushConfigs,
        A2AServerOptions options,
        IAgentCardProvider cards,
        ILogger<TaskExecutionEngine>? logger = null)
    {
        _tasks = tasks;
        _events = events;
        _executors = executors;
        _bus = bus;
        _push = push;
        _idempotency = idempotency;
        _pushConfigs = pushConfigs;
        _options = options;
        _cards = cards;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Retries after a version conflict when applying one change to a task.</summary>
    internal const int MaxConflictRetries = 3;

    /// <summary>Status text of a task failed because its updates could not be stored.</summary>
    internal const string StoreFailureText = "The task failed because its updates could not be stored.";

    /// <summary>Status text of a task failed because the agent produced an event larger than <see cref="A2ALimits.MaxEventBytes"/>.</summary>
    internal const string OversizedEventText = "The agent produced an event larger than the configured limit.";

    /// <summary>The bus the engine publishes every stored event to.</summary>
    internal TaskEventBus Bus => _bus;

    /// <summary>Task ids are unique per tenant only, so in-memory state (locks, running executions, bus topics) is keyed by both.</summary>
    internal static string ScopeKey(string tenant, string taskId) => $"{tenant}\u001f{taskId}";

    /// <summary>Waits for the task's lock; dispose the result to release it.</summary>
    internal ValueTask<TaskLocks.Releaser> LockAsync(TaskKey key, CancellationToken ct) => _locks.AcquireAsync(key, ct);

    /// <summary>Completions of the executions running for a task.</summary>
    internal IReadOnlyList<Task> RunningCompletions(TaskKey key) =>
        _running.Snapshot(ScopeKey(key.Tenant, key.TaskId)).Select(e => e.Completion).ToList();

    /// <summary>Asks every execution running for the task to stop; returns them.</summary>
    internal List<Execution> CancelRunning(TaskKey key)
    {
        var running = _running.Snapshot(ScopeKey(key.Tenant, key.TaskId));
        foreach (var r in running) r.Cancel();
        return running;
    }

    /// <summary>
    /// Runs <see cref="IAgentExecutor.ExecuteAsync"/> for a message. An idempotency <paramref name="claim"/> is completed when
    /// the task is first stored or the agent replies directly, and abandoned when neither happens. <paramref name="push"/> is
    /// saved when a new task is first stored.
    /// </summary>
    internal Execution Start(AgentRequestContext context, TaskPushNotificationConfig? push, IdempotencyClaim claim) =>
        StartRaw(new TaskKey(context.Call.Tenant, context.Call.Principal, context.TaskId), context.ContextId, context.IsNewTask, context.Message,
            push, emitInitialSnapshot: !context.IsNewTask, (executor, updater, ct) => executor.ExecuteAsync(context, updater, ct), claim, context.Metadata);

    /// <summary>Runs <see cref="IAgentExecutor.CancelAsync"/> for a task. The executor gets its own copy of <paramref name="task"/>.</summary>
    internal Execution StartCancel(TaskKey key, AgentTask task, Dictionary<string, JsonElement>? metadata)
    {
        var snapshot = task.DeepCopy(); // the executor's own view: changing it never changes the stored task
        return StartRaw(key, snapshot.ContextId, isNewTask: false, userMessage: null, push: null, emitInitialSnapshot: false,
            (executor, updater, ct) => executor.CancelAsync(snapshot, updater, ct), requestMetadata: metadata);
    }

    /// <summary>
    /// Waits for the outcome of a blocking SendMessage, then returns the task, as the first request does:
    /// <list type="bullet">
    /// <item>a terminal state;</item>
    /// <item>an interrupted state reached by a status event after <paramref name="afterSequence"/> (an interrupted state from
    /// before a follow-up is not its outcome); seen in the event store or on the live bus;</item>
    /// <item>no execution running for the task any more (the agent returned without settling it, or the server restarted):
    /// nothing will change it, so its current state;</item>
    /// <item>or the request is cancelled.</item>
    /// </list>
    /// Subscribing before the first read, and checking for running executions before reading the task, means no change is missed.
    /// The task returned may share instances with the store: serialize it, never change it.
    /// </summary>
    /// <param name="key">The task.</param>
    /// <param name="afterSequence">The task's last event sequence before the message; 0 for a new task.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<AgentTask> WaitUntilSettledAsync(TaskKey key, long afterSequence, CancellationToken cancellationToken)
    {
        using var sub = _bus.Subscribe(ScopeKey(key.Tenant, key.TaskId));
        var (statusAfter, known) = afterSequence == 0
            ? (true, 0L)
            : await StatusEventAfterAsync(key, afterSequence, cancellationToken).ConfigureAwait(false);
        Task<bool>? live = null; // one pending wait at a time: the subscription has a single reader
        while (true)
        {
            var running = RunningCompletions(key);
            var record = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(key.TaskId);
            var state = record.Task.Status.State;
            if (state.IsTerminal() || (state.IsInterrupted() && statusAfter) || running.Count == 0) return record.Task;

            live ??= sub.Reader.WaitToReadAsync(cancellationToken).AsTask();
            await Task.WhenAny(running.Append(live)).ConfigureAwait(false);
            if (!live.IsCompleted) continue; // an execution ended: check again
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
                (statusAfter, known) = await StatusEventAfterAsync(key, afterSequence, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the event store holds a status event of the task with a sequence after <paramref name="afterSequence"/>, and the
    /// last sequence read (at least <paramref name="afterSequence"/>). When the store no longer has the first events after
    /// <paramref name="afterSequence"/> (trimmed), this answers true: the stored task, which the caller reads, then decides.
    /// </summary>
    private async Task<(bool Found, long Last)> StatusEventAfterAsync(TaskKey key, long afterSequence, CancellationToken ct)
    {
        var last = afterSequence;
        await foreach (var stored in _events.ReadAfterAsync(key, afterSequence, ct).ConfigureAwait(false))
        {
            if (stored.Event is StatusUpdateEvent || stored.Sequence != last + 1) return (true, stored.Sequence);
            last = stored.Sequence;
        }
        return (false, last);
    }

    /// <summary>
    /// Runs the agent for a task. An idempotency <c>claim</c> is completed when the task is first stored or the agent replies
    /// directly, and abandoned when neither happens.
    /// </summary>
    private Execution StartRaw(TaskKey key, string contextId, bool isNewTask, Message? userMessage,
        TaskPushNotificationConfig? push, bool emitInitialSnapshot, Func<IAgentExecutor, TaskUpdater, CancellationToken, Task> run,
        IdempotencyClaim? claim = null, Dictionary<string, JsonElement>? requestMetadata = null)
    {
        var taskId = key.TaskId;
        var exec = new Execution(taskId, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token), _options.Streaming.ChannelCapacity);
        var scope = ScopeKey(key.Tenant, taskId);
        _running.Add(scope, exec);
        var updater = new TaskUpdater(taskId, contextId, isNewTask, exec.Incoming.Writer, requestMetadata);
        // The first history entry of a new task, copied before the executor starts: the executor may change its message.
        var historyMessage = isNewTask && userMessage is not null ? WithTask(userMessage, taskId) : null;
        var producer = Task.Run(async () =>
        {
            try
            {
                await using var lease = await _executors.RentAsync(exec.Cts.Token).ConfigureAwait(false);
                await run(lease.Executor, updater, exec.Cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (exec.Cts.IsCancellationRequested)
            {
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
                ServerLog.ExecutorFailed(_logger, ex, taskId);
                await TryFinishAsync(updater, TaskState.Failed, "The agent failed to process the request.").ConfigureAwait(false);
            }
            finally
            {
                exec.Incoming.Writer.TryComplete();
            }
        });

        // Background writes outlive the request: the owner scope is fixed in the key.
        var consumer = Task.Run(() => ConsumeAsync(exec, key, contextId, isNewTask, historyMessage, push, emitInitialSnapshot, claim ?? IdempotencyClaim.None));
        _ = Task.WhenAll(producer, consumer).ContinueWith(_t =>
        {
            _running.Remove(scope, exec);
            exec.MarkDone();
        }, TaskScheduler.Default);
        return exec;
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
                    var current = await _tasks.GetAsync(key).ConfigureAwait(false);
                    if (current is not null)
                        exec.Emit(new SequencedEvent(await _events.LastSequenceAsync(key).ConfigureAwait(false), StreamResponse.Of(current.Task)));
                }
                finally
                {
                    snapshotGate.Release();
                }
            }

            await foreach (var produced in exec.Incoming.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                // Encoded once: the size check, the event store, streams and webhooks all use these bytes.
                var encoded = Encode(produced);
                if (encoded.Json.Length > _options.Limits.MaxEventBytes)
                {
                    // The event is never stored or sent: the task fails instead and the agent is stopped.
                    ServerLog.EventTooLarge(_logger, taskId, _options.Limits.MaxEventBytes);
                    encoded = Encode(StreamResponse.Of(FailureEvent(taskId, contextId)));
                    exec.Cancel();
                }
                var evt = encoded.Event;

                if (evt is MessageEvent reply)
                {
                    // The idempotency store owns its copy; the reply itself goes to the waiting request.
                    await _idempotency.RecordAsync(claim, new IdempotencyOutcome.DirectMessage(reply.Message.DeepCopy()), taskId).ConfigureAwait(false);
                    exec.Emit(encoded); // direct reply (sequence 0), no task, not stored
                    continue;
                }

                var gate = _locks.For(key.Tenant, taskId);
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    TaskRecord? record;
                    if (!created)
                    {
                        var now = DateTimeOffset.UtcNow;
                        record = await _tasks.CreateAsync(new TaskRecord(
                            key.Tenant, key.Owner,
                            new AgentTask
                            {
                                Id = taskId,
                                ContextId = contextId,
                                Status = new AgentTaskStatus { State = TaskState.Submitted, Timestamp = (evt as StatusUpdateEvent)?.StatusUpdate.Status.Timestamp ?? now },
                                History = historyMessage is null ? null : [historyMessage],
                            },
                            Version: 0, CreatedAt: now, UpdatedAt: now)).ConfigureAwait(false);
                        if (push is not null) await _pushConfigs.SaveInlineAsync(key, push, CancellationToken.None).ConfigureAwait(false);
                        created = true;
                        // The new task as the first event. It shares the stored instance: nobody changes either (the next
                        // update stores a copy), and the event log owns the event from here on.
                        var snapshot = EventEncoder.Encode(0, StreamResponse.Of(record.Task));
                        var first = await _events.AppendAsync(key, snapshot).ConfigureAwait(false);
                        A2AServerTelemetry.Events.Add(1);
                        Publish(exec, key, snapshot with { Sequence = first });
                        // Retries of this request now attach to the task (its first event is stored, so a replay starts complete).
                        await _idempotency.RecordAsync(claim, new IdempotencyOutcome.TaskCreated(taskId), taskId).ConfigureAwait(false);
                    }
                    else
                    {
                        record = await _tasks.GetAsync(key).ConfigureAwait(false);
                        if (record is null)
                        {
                            ServerLog.TaskDisappeared(_logger, taskId);
                            continue;
                        }
                    }

                    if (await ApplyAndStoreAsync(key, record, encoded).ConfigureAwait(false) is not { } stored) continue;

                    // Still under the task lock, so sinks see events in sequence order even with concurrent executions.
                    // Every sink is non-blocking.
                    Publish(exec, key, stored);
                    if (evt is StatusUpdateEvent { StatusUpdate.Status.State: var st } && st.IsTerminal())
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
            exec.Cancel();
            // Before the FAILED event is published: the waiting request must see the error, not that event.
            exec.Fail(new A2AException(A2AErrorCode.Internal));
            await TryFailStoredTaskAsync(key, contextId, scope).ConfigureAwait(false);
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
    private async Task TryFailStoredTaskAsync(TaskKey key, string contextId, string scope)
    {
        try
        {
            var gate = _locks.For(key.Tenant, key.TaskId);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var record = await _tasks.GetAsync(key).ConfigureAwait(false);
                if (record is null || record.Task.Status.State.IsTerminal()) return;
                var failed = Encode(StreamResponse.Of(FailureEvent(key.TaskId, contextId, StoreFailureText)));
                if (await ApplyAndStoreAsync(key, record, failed).ConfigureAwait(false) is not { } stored) return;
                _bus.Publish(scope, stored);
                if (PushDeclared) _push.Enqueue(key, stored);
                await _events.MarkTerminalAsync(key).ConfigureAwait(false);
                _bus.Complete(scope);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            ServerLog.MarkFailedFailed(_logger, ex, key.TaskId);
        }
    }

    /// <summary>
    /// Applies the event to a copy of the task, stores the copy, then appends the event (the caller holds the task lock, so
    /// sequences follow the apply order and the log holds only applied events). After a version conflict, reloads and
    /// re-applies, up to <see cref="MaxConflictRetries"/> times. Returns null when the event was dropped (for example the task
    /// is already terminal). <paramref name="record"/> belongs to the store and is never changed. <paramref name="encoded"/> comes
    /// from <see cref="Encode"/>; the stored and returned event carries its bytes.
    /// </summary>
    private async Task<SequencedEvent?> ApplyAndStoreAsync(TaskKey key, TaskRecord record, SequencedEvent encoded)
    {
        var evt = encoded.Event;
        for (var attempt = 0; ; attempt++)
        {
            if (ApplyToCopy(record.Task, evt) is not { } next)
            {
                ServerLog.EventDropped(_logger, key.TaskId, record.Task.Status.State);
                return null;
            }
            try
            {
                await _tasks.UpdateAsync(record with { Task = next }, record.Version).ConfigureAwait(false);
                var sequence = await _events.AppendAsync(key, encoded).ConfigureAwait(false);
                A2AServerTelemetry.Events.Add(1);
                return encoded with { Sequence = sequence };
            }
            catch (ConcurrencyException) when (attempt < MaxConflictRetries)
            {
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

    /// <summary>
    /// Encodes an event the agent produced (or a failure event), once. A status update without a timestamp gets one first: the
    /// stored task, the stored event and its bytes then all carry the same time, and nothing changes the event afterwards.
    /// </summary>
    private static SequencedEvent Encode(StreamResponse evt)
    {
        if (evt is StatusUpdateEvent { StatusUpdate.Status: var status }) status.Timestamp ??= DateTimeOffset.UtcNow;
        return EventEncoder.Encode(0, evt);
    }

    private void Publish(Execution exec, TaskKey key, SequencedEvent evt)
    {
        exec.Emit(evt);
        _bus.Publish(ScopeKey(key.Tenant, key.TaskId), evt);
        if (PushDeclared) _push.Enqueue(key, evt);
    }

    /// <summary>
    /// Whether the public card declares push notifications. When it does not, no push config can exist, so events are not handed to
    /// the <see cref="IPushNotificationSender"/>.
    /// </summary>
    private bool PushDeclared => _cards.Get().Capabilities.PushNotifications == true;

    /// <summary>
    /// Returns a copy of <paramref name="current"/> (<see cref="ModelCopies.CopyForUpdate"/>) with the event applied, or null
    /// when the event must be dropped: the task is terminal, the status transition is not allowed, or the event is not a
    /// status or artifact update. Never changes <paramref name="current"/> or anything reachable from it; an appended artifact
    /// is replaced in the copy by a new artifact. The event's status message and artifact go into the copy as they are: from
    /// here on the event and the stored task share them, and the server owns both (see <see cref="TaskUpdater"/>).
    /// </summary>
    private static AgentTask? ApplyToCopy(AgentTask current, StreamResponse evt)
    {
        if (current.Status.State.IsTerminal()) return null;
        if (evt is StatusUpdateEvent { StatusUpdate: var su })
        {
            if (!TaskStateMachine.CanTransition(current.Status.State, su.Status.State)) return null;
            // su.Status.Timestamp was set before the event was encoded (Encode); the event is not changed here.
            var next = current.CopyForUpdate();
            next.Status = new AgentTaskStatus { State = su.Status.State, Message = su.Status.Message, Timestamp = su.Status.Timestamp };
            if (su.Status.Message is { } m) (next.History ??= []).Add(m);
            return next;
        }
        if (evt is ArtifactUpdateEvent { ArtifactUpdate: var au })
        {
            A2AValidator.ValidateArtifact(au.Artifact, "artifact");
            var incoming = au.Artifact;
            var next = current.CopyForUpdate();
            var artifacts = next.Artifacts ??= [];
            var idx = artifacts.FindIndex(a => a.ArtifactId == incoming.ArtifactId);
            if (idx < 0) artifacts.Add(incoming);
            else if (au.Append)
            {
                // A new artifact replaces the stored one in the copy; the stored one is never changed.
                var merged = artifacts[idx].CopyWithParts(incoming.Parts);
                merged.Name ??= incoming.Name;
                merged.Description ??= incoming.Description;
                if (incoming.Metadata is not null)
                {
                    merged.Metadata ??= new();
                    foreach (var kv in incoming.Metadata) merged.Metadata[kv.Key] = kv.Value;
                }
                artifacts[idx] = merged;
            }
            else artifacts[idx] = incoming;
            return next;
        }
        return null;
    }

    private static TaskStatusUpdateEvent FailureEvent(string taskId, string contextId, string text = OversizedEventText) => new TaskStatusUpdateEvent
    {
        TaskId = taskId,
        ContextId = contextId,
        Status = new AgentTaskStatus
        {
            State = TaskState.Failed,
            Timestamp = DateTimeOffset.UtcNow,
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
        try { _shutdown.Cancel(); }
        catch (ObjectDisposedException) { /* disposed: already cancelled */ }
    }

    /// <summary>Stops running executions and releases resources. Does not wait for the executions to end (see <see cref="WhenIdleAsync"/>). Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
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
