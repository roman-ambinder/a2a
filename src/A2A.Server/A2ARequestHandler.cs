using System.Diagnostics;
using System.Runtime.CompilerServices;
using A2A.Server.Execution;
using A2A.Server.Idempotency;
using A2A.Server.Pipeline;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using A2A.Validation;
using A2A.Server.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.Server;

/// <summary>
/// Transport-agnostic implementation of every A2A operation. Protocol bindings (JSON-RPC, REST) parse the wire format,
/// build a <see cref="ServerCallContext"/>, and call these methods. Errors are thrown as <see cref="A2AException"/>.
/// The tasks, messages and events these methods return may share instances with the stores: serialize them, never change them.
/// </summary>
public sealed class A2ARequestHandler : IDisposable
{
    private readonly TaskStore _tasks;
    private readonly EventStore _events;
    private readonly IdempotencyCoordinator _idempotency;
    private readonly A2AAuthorizationHandler _authorization;
    private readonly TaskEventBus _bus;
    private readonly A2AServerOptions _options;
    private readonly ILogger _logger;
    private readonly StreamAdmission _admission;
    private readonly CallValidator _validator;
    private readonly PushConfigService _pushConfigs;
    private readonly TaskExecutionEngine _engine;

    /// <summary>Creates the handler. In an ASP.NET Core app the DI container does this; the parameters are the registered services.</summary>
    /// <remarks>The handler does not own <paramref name="engine"/>: its owner (the DI container) disposes it.</remarks>
    public A2ARequestHandler(
        TaskStore tasks,
        EventStore events,
        IdempotencyCoordinator idempotency,
        A2AAuthorizationHandler authorization,
        A2AServerOptions options,
        CallValidator validator,
        PushConfigService pushConfigs,
        TaskExecutionEngine engine,
        ILogger<A2ARequestHandler>? logger = null)
    {
        _tasks = tasks;
        _events = events;
        _idempotency = idempotency;
        _authorization = authorization;
        _bus = engine.Bus; // the bus the engine publishes to: streams subscribe to the same one
        _options = options;
        _validator = validator;
        _pushConfigs = pushConfigs;
        _engine = engine;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _admission = new StreamAdmission(_options.Limits);
    }

    /// <summary>The public Agent Card from <see cref="A2AServerOptions.AgentCard"/>.</summary>
    public AgentCard AgentCard => _options.AgentCard;

    // =====================================================================
    // Call validation: version, extensions, authentication
    // =====================================================================

    /// <summary>Checks protocol version, required extensions, and authentication; fills <see cref="ServerCallContext.ActivatedExtensions"/>.</summary>
    public void ValidateCall(ServerCallContext call) => _validator.Validate(call);

    // =====================================================================
    // Authorization hook
    // =====================================================================

    /// <summary>
    /// Runs at the top of every operation. Refuses anonymous callers unless <see cref="A2AServerOptions.AllowAnonymous"/> is
    /// set (so direct callers that skip <see cref="ValidateCall"/> are covered), then asks the <see cref="A2AAuthorizationHandler"/>
    /// about the operation. A denial of a task-scoped operation looks like a missing task; any other denial is
    /// <see cref="A2AErrorCode.PermissionDenied"/>.
    /// </summary>
    private async Task AuthorizeAsync(ServerCallContext call, A2AOperation op, object request, string? taskId, CancellationToken ct)
    {
        if (!_options.AllowAnonymous && !call.IsAuthenticated) throw new A2AException(A2AErrorCode.Unauthenticated);
        if (await _authorization.AuthorizeAsync(call, op, request, ct).ConfigureAwait(false)) return;
        if (!string.IsNullOrEmpty(taskId)) throw A2AException.TaskNotFound(taskId);
        throw new A2AException(A2AErrorCode.PermissionDenied);
    }

    // =====================================================================
    // SendMessage / SendStreamingMessage
    // =====================================================================

    /// <summary>
    /// Sends a message. Unless <see cref="SendMessageConfiguration.ReturnImmediately"/> is set, waits until the task is
    /// terminal or interrupted, by an event of this execution or of any other execution of the task (for example CancelTask),
    /// or until no execution of the task is running any more.
    /// </summary>
    public async Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.SendMessage, request, request.Message?.TaskId, cancellationToken).ConfigureAwait(false);
        var (claim, replay) = await _idempotency.BeginAsync(request, call, cancellationToken).ConfigureAwait(false);
        if (replay is not null) return await _idempotency.ReplayAsync(replay, request, call, _engine.WaitUntilSettledAsync, cancellationToken).ConfigureAwait(false);
        var taskId = string.IsNullOrEmpty(request.Message?.TaskId) ? TaskIds.New() : request.Message.TaskId;
        var key = Key(call, taskId);
        var scope = ScopeKey(call.Tenant, taskId);
        // Subscribe before the agent starts, so no event of another execution is missed.
        using var sub = _bus.Subscribe(scope);
        var (exec, afterSequence) = await AcceptAsync(request, call, claim, taskId, cancellationToken).ConfigureAwait(false);
        var returnImmediately = request.Configuration?.ReturnImmediately == true;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var own = exec.Outgoing.Reader;
            Task<bool>? ownWait = null, busWait = null; // one pending wait per reader: both have a single reader
            var ownDone = false;
            var started = false;
            while (true)
            {
                if (exec.Failed) throw new A2AException(A2AErrorCode.Internal); // storage failed: an error, not the FAILED task

                // Other executions (CancelTask, a later follow-up) settle the task too.
                var settledElsewhere = false;
                while (sub.Reader.TryRead(out var other))
                    if (other.Sequence > afterSequence && other.Event is StatusUpdateEvent { StatusUpdate.Status.State: var os } && (os.IsTerminal() || os.IsInterrupted()))
                        settledElsewhere = true;
                if (settledElsewhere) break;

                if (!ownDone && own.TryRead(out var sequenced))
                {
                    var evt = sequenced.Event;
                    if (evt is MessageEvent reply) return new MessageResponse(reply.Message);
                    started = true;
                    if (returnImmediately) break;
                    if (evt is StatusUpdateEvent { StatusUpdate: var su } && (su.Status.State.IsTerminal() || su.Status.State.IsInterrupted())) break;
                    continue;
                }

                if (ownDone)
                {
                    // This execution ended. Nothing more will change the task once no execution of it is running.
                    var running = _engine.RunningCompletions(key);
                    if (!started || running.Count == 0) break;
                    busWait ??= sub.Reader.WaitToReadAsync(stop.Token).AsTask();
                    await Task.WhenAny(running.Append(busWait)).ConfigureAwait(false);
                }
                else
                {
                    ownWait ??= own.WaitToReadAsync(stop.Token).AsTask();
                    busWait ??= sub.Reader.WaitToReadAsync(stop.Token).AsTask();
                    await Task.WhenAny(ownWait, busWait).ConfigureAwait(false);
                }

                if (ownWait is { IsCompleted: true })
                {
                    ownDone = !await ownWait.ConfigureAwait(false); // a storage failure surfaces here as an error
                    ownWait = null;
                }
                if (busWait is { IsCompleted: true })
                {
                    if (!await busWait.ConfigureAwait(false)) break; // the bus ended the topic: the task is terminal
                    busWait = null;
                }
            }
        }
        finally
        {
            stop.Cancel();
            exec.Detach();
        }

        var record = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new A2AException(A2AErrorCode.InvalidAgentResponse, "The agent finished without replying or creating a task.");
        return new TaskResponse(Shape(record.Task, request.Configuration?.HistoryLength));
    }

    /// <summary>
    /// Sends a message and streams the task's events, including those of other executions of the task (for example the
    /// CANCELED event of a CancelTask), in sequence order and without repeats. The stream ends after the task's final event,
    /// or when this execution ended and no other execution of the task is running. The stream is limited like every stream: see
    /// <see cref="A2ALimits.MaxConcurrentStreamsPerTask"/>, <see cref="A2ALimits.MaxConcurrentStreamsPerPrincipal"/>,
    /// <see cref="A2ALimits.StreamReauthInterval"/> and <see cref="A2ALimits.MaxStreamDuration"/>.
    /// </summary>
    public IAsyncEnumerable<SequencedEvent> SendStreamingMessageAsync(SendMessageRequest request, ServerCallContext call, CancellationToken cancellationToken = default) =>
        LimitStreamAsync(SendStreamingCoreAsync(request, call, CancellationToken.None), call, A2AOperation.SendStreamingMessage, request, cancellationToken);

    /// <summary>
    /// Like <see cref="SendStreamingMessageAsync(SendMessageRequest, ServerCallContext, CancellationToken)"/>, but a valid
    /// <paramref name="lastEventId"/> (a <see cref="StreamCursor"/>, from the <c>Last-Event-ID</c> header) makes the call a
    /// reconnect: it resumes the task the cursor names, as a <see cref="SubscribeToTaskAsync(SubscribeToTaskRequest, ServerCallContext, long?, CancellationToken)"/>
    /// of the caller's task, and the message is not sent again. A resume is authorized as <see cref="A2AOperation.SubscribeToTask"/>
    /// with a <see cref="SubscribeToTaskRequest"/>. The header is ignored, and the message sent normally, when it does not
    /// parse or when the message names a different task than the cursor.
    /// </summary>
    public IAsyncEnumerable<SequencedEvent> SendStreamingMessageAsync(SendMessageRequest request, ServerCallContext call, string? lastEventId,
        CancellationToken cancellationToken = default) =>
        SendStreamingMessageAsync(request, call, lastEventId, onCaughtUp: null, cancellationToken);

    /// <summary>As the public overload; <paramref name="onCaughtUp"/> is passed to the resume (see <see cref="SubscribeCoreAsync"/>).</summary>
    internal IAsyncEnumerable<SequencedEvent> SendStreamingMessageAsync(SendMessageRequest request, ServerCallContext call, string? lastEventId,
        Action? onCaughtUp, CancellationToken cancellationToken)
    {
        var messageTaskId = request.Message?.TaskId;
        return StreamCursor.TryParse(lastEventId, out var taskId, out var sequence)
               && (string.IsNullOrEmpty(messageTaskId) || string.Equals(messageTaskId, taskId, StringComparison.Ordinal))
            ? SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId, Tenant = request.Tenant }, call, sequence, onCaughtUp, cancellationToken)
            : SendStreamingMessageAsync(request, call, cancellationToken);
    }

    private async IAsyncEnumerable<SequencedEvent> SendStreamingCoreAsync(SendMessageRequest request, ServerCallContext call,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.SendStreamingMessage, request, request.Message?.TaskId, cancellationToken).ConfigureAwait(false);
        RequireStreaming();
        var (claim, replay) = await _idempotency.BeginAsync(request, call, cancellationToken).ConfigureAwait(false);
        if (replay is IdempotencyOutcome.DirectMessage direct)
        {
            // The stored outcome belongs to the idempotency store; the caller gets its own copy.
            yield return new SequencedEvent(0, StreamResponse.Of(direct.Message.DeepCopy()));
            yield break;
        }
        if (replay is IdempotencyOutcome.TaskCreated created)
        {
            // The same path as a resume from cursor 0: the retained events from the start, then live events.
            var resume = new SubscribeToTaskRequest { Id = created.TaskId, Tenant = request.Tenant };
            await foreach (var replayed in SubscribeCoreAsync(resume, call, resumeAfter: 0, onCaughtUp: null, cancellationToken).ConfigureAwait(false))
                yield return replayed;
            yield break;
        }

        // Take the stream slot before the message is accepted, so a refused stream leaves no trace on the task.
        var taskId = string.IsNullOrEmpty(request.Message?.TaskId) ? TaskIds.New() : request.Message.TaskId;
        IDisposable slot;
        try
        {
            slot = _admission.Enter(Key(call, taskId), call.LimitPartition);
        }
        catch
        {
            await _idempotency.AbandonAsync(claim).ConfigureAwait(false);
            throw;
        }
        using var admitted = slot;
        var scope = ScopeKey(call.Tenant, taskId);
        // Subscribe before the agent starts, so no event of another execution is missed.
        using var sub = _bus.Subscribe(scope);
        var (exec, _) = await AcceptAsync(request, call, claim, taskId, cancellationToken).ConfigureAwait(false);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            // The first event comes from this execution: the task (new task or follow-up snapshot), or a direct reply.
            var own = exec.Outgoing.Reader;
            SequencedEvent first = default;
            var any = false;
            while (!any && await own.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                any = own.TryRead(out first);
            if (!any) throw new A2AException(A2AErrorCode.InvalidAgentResponse, "The agent finished without replying or creating a task.");
            yield return first;
            if (first.Event is MessageEvent || IsFinal(first.Event)) yield break;

            // Then the task's events from every execution (this one, CancelTask, follow-ups), in sequence order and without
            // repeats, until a final event, the end of the topic, or no execution of the task running any more.
            var key = Key(call, taskId);
            var position = new StreamPosition(first.Sequence);
            Task<bool>? ownWait = null, busWait = null; // one pending wait per reader: both have a single reader
            var ownDone = false;
            var topicEnded = false;
            while (true)
            {
                if (exec.Failed)
                {
                    // Storage failed: send what this execution still has queued, then the error (not the FAILED event on the bus).
                    while (own.TryRead(out var queued))
                    {
                        await foreach (var e in InOrderAsync(key, queued, position, cancellationToken).ConfigureAwait(false)) yield return e;
                        if (position.Final) yield break;
                    }
                    throw new A2AException(A2AErrorCode.Internal);
                }

                // The bus first: another execution's event may come before this execution's next one.
                while (sub.Reader.TryRead(out var other))
                {
                    await foreach (var e in InOrderAsync(key, other, position, cancellationToken).ConfigureAwait(false)) yield return e;
                    if (position.Final) yield break;
                }
                if (!ownDone && own.TryRead(out var mine))
                {
                    await foreach (var e in InOrderAsync(key, mine, position, cancellationToken).ConfigureAwait(false)) yield return e;
                    if (position.Final) yield break;
                    continue;
                }

                if (ownDone)
                {
                    var running = _engine.RunningCompletions(key);
                    if (running.Count == 0) break; // nothing will change the task any more
                    busWait ??= sub.Reader.WaitToReadAsync(stop.Token).AsTask();
                    await Task.WhenAny(running.Append(busWait)).ConfigureAwait(false);
                }
                else
                {
                    ownWait ??= own.WaitToReadAsync(stop.Token).AsTask();
                    busWait ??= sub.Reader.WaitToReadAsync(stop.Token).AsTask();
                    await Task.WhenAny(ownWait, busWait).ConfigureAwait(false);
                }

                if (ownWait is { IsCompleted: true })
                {
                    ownDone = !await ownWait.ConfigureAwait(false); // a storage failure surfaces here as an error
                    ownWait = null;
                }
                if (busWait is { IsCompleted: true })
                {
                    if (!await busWait.ConfigureAwait(false))
                    {
                        topicEnded = true; // the task went terminal
                        break;
                    }
                    busWait = null;
                }
            }

            // Finish from the store: every event is stored before it is published, and a lossy bus may have dropped some.
            await foreach (var stored in ReadContiguousAsync(key, position.Last, long.MaxValue, cancellationToken).ConfigureAwait(false))
            {
                yield return stored;
                position.Last = stored.Sequence;
                if (IsFinal(stored.Event)) yield break;
            }
            if (topicEnded)
            {
                // The store no longer has the final event (trimmed): end with a snapshot, never without a final event.
                var last = await ReadSnapshotAsync(key, cancellationToken).ConfigureAwait(false);
                if (last.Sequence > position.Last) yield return last;
            }
        }
        finally
        {
            stop.Cancel();
            exec.Detach();
        }
    }

    /// <summary>The last sequence a stream sent, and whether it sent the task's final event.</summary>
    private sealed class StreamPosition(long last)
    {
        public long Last { get; set; } = last;

        public bool Final { get; set; }
    }

    /// <summary>
    /// Yields <paramref name="live"/> if the stream has not sent it yet, first filling a gap before it from the store, or with
    /// a snapshot when the store no longer has the missing events. Updates <paramref name="position"/>; stops after a final event.
    /// </summary>
    private async IAsyncEnumerable<SequencedEvent> InOrderAsync(TaskKey key, SequencedEvent live, StreamPosition position,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (live.Sequence <= position.Last) yield break;
        if (live.Sequence > position.Last + 1)
        {
            await foreach (var stored in ReadContiguousAsync(key, position.Last, live.Sequence, ct).ConfigureAwait(false))
            {
                position.Last = stored.Sequence;
                position.Final = IsFinal(stored.Event);
                yield return stored;
                if (position.Final) yield break;
            }
            if (live.Sequence > position.Last + 1)
            {
                var snapshot = await ReadSnapshotAsync(key, ct).ConfigureAwait(false);
                position.Last = snapshot.Sequence;
                position.Final = IsFinal(snapshot.Event);
                yield return snapshot;
                if (position.Final || live.Sequence <= position.Last) yield break;
            }
        }
        position.Last = live.Sequence;
        position.Final = IsFinal(live.Event);
        yield return live;
    }

    /// <param name="Context">The request context for the agent.</param>
    /// <param name="PushConfig">The push config to save when a new task is created.</param>
    /// <param name="LastSequence">For a follow-up, the task's last event sequence right after the message was appended; 0 for a new task.</param>
    private sealed record Prepared(AgentRequestContext Context, TaskPushNotificationConfig? PushConfig, long LastSequence);

    /// <summary>
    /// Accepts the message and starts the agent. The idempotency claim is abandoned when the message is refused or the agent
    /// cannot start; a follow-up records its task at once (the message is already in the task's history), and a new task
    /// records it when it is first stored.
    /// </summary>
    /// <returns>The execution, and the task's last sequence before it (0 for a new task; see <see cref="Prepared.LastSequence"/>).</returns>
    private async Task<(Execution.Execution Execution, long AfterSequence)> AcceptAsync(SendMessageRequest request, ServerCallContext call, IdempotencyClaim claim, string? newTaskId, CancellationToken ct)
    {
        try
        {
            var prepared = await PrepareAsync(request, call, ct, newTaskId).ConfigureAwait(false);
            if (prepared.Context.IsNewTask) return (_engine.Start(prepared.Context, prepared.PushConfig, claim), 0);

            // Record after the execution is registered, so a blocking retry sees it running and waits for its outcome.
            var exec = _engine.Start(prepared.Context, prepared.PushConfig, IdempotencyClaim.None);
            var taskId = prepared.Context.TaskId;
            await _idempotency.RecordAsync(claim, new IdempotencyOutcome.TaskCreated(taskId, prepared.LastSequence), taskId).ConfigureAwait(false);
            return (exec, prepared.LastSequence);
        }
        catch
        {
            await _idempotency.AbandonAsync(claim).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Prepared> PrepareAsync(SendMessageRequest request, ServerCallContext call, CancellationToken ct, string? newTaskId = null)
    {
        call.BindTenant(request.Tenant);
        A2AValidator.ValidateIncoming(request);
        RequestLimitValidator.Validate(request.Message, _options.Limits);
        // A copy: the caller's message is never changed (a retry with the same instance must hash the same). This copy goes to
        // the executor; the stored history gets copies of its own.
        var message = request.Message.DeepCopy();
        var isFollowUp = !string.IsNullOrEmpty(message.TaskId);
        var taskId = isFollowUp ? message.TaskId! : newTaskId ?? TaskIds.New();
        AgentTask? existing = null;
        long lastSequence = 0;
        string contextId;

        // Check the push config before anything changes, so a refused request leaves no trace and a retry is refused the same way.
        TaskPushNotificationConfig? push = null;
        if (request.Configuration?.TaskPushNotificationConfig is { } p)
        {
            RequirePush();
            push = p.DeepCopy(); // the caller's config is never changed
            push.TaskId = taskId;
            push.Id = string.IsNullOrEmpty(push.Id) ? TaskIds.New() : push.Id;
            push.Tenant = call.Tenant;
            await _pushConfigs.ValidateInlineAsync(Key(call, taskId), push, ct).ConfigureAwait(false);
        }

        if (isFollowUp)
        {
            using (await _engine.LockAsync(Key(call, taskId), ct).ConfigureAwait(false))
            {
                for (var attempt = 0; ; attempt++)
                {
                    var record = await _tasks.GetAsync(Key(call, taskId), ct).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(taskId);
                    var state = record.Task.Status.State;
                    if (!string.IsNullOrEmpty(message.ContextId) && message.ContextId != record.Task.ContextId)
                        throw A2AException.InvalidParams("message.contextId does not match the task's contextId");
                    if (state.IsTerminal())
                        throw new A2AException(A2AErrorCode.UnsupportedOperation,
                            "The task is in a terminal state. Start a new task and list this one in referenceTaskIds.")
                        {
                            Metadata = new() { ["taskId"] = taskId },
                        };
                    if (!TaskStateMachine.AcceptsFollowUp(state, _options.FollowUpPolicy))
                        throw new A2AException(A2AErrorCode.UnsupportedOperation, $"The task does not accept messages in state {state}.")
                        {
                            Metadata = new() { ["taskId"] = taskId },
                        };
                    if (push is not null) await _pushConfigs.EnsureCapacityAsync(Key(call, taskId), push.Id!, ct).ConfigureAwait(false);
                    contextId = record.Task.ContextId;
                    message.ContextId = contextId;
                    // The stored record belongs to the store: append to a copy, with a copy of the message the executor gets.
                    var next = record.Task.CopyForUpdate();
                    (next.History ??= []).Add(message.DeepCopy());
                    try
                    {
                        await _tasks.UpdateAsync(record with { Task = next }, record.Version, ct).ConfigureAwait(false);
                        existing = next;
                        // Still under the task lock. The message is committed: a cancelled request must not stop here, or
                        // the idempotency entry would be abandoned and a retry would append the message again.
                        lastSequence = await _events.LastSequenceAsync(Key(call, taskId), CancellationToken.None).ConfigureAwait(false);
                        break;
                    }
                    catch (ConcurrencyException) when (attempt < TaskExecutionEngine.MaxConflictRetries)
                    {
                        // The task changed under us: reload, re-check the state, and append again.
                    }
                }
            }
        }
        else
        {
            contextId = await ResolveContextAsync(message.ContextId, call, ct).ConfigureAwait(false);
            message.ContextId = contextId;
        }

        // A new task saves its push config when the task is created; a follow-up saves it now (capacity checked above), past
        // cancellation for the same reason as the last sequence: its message is already committed.
        if (push is not null && existing is not null) await _pushConfigs.SaveInlineAsync(Key(call, taskId), push, CancellationToken.None).ConfigureAwait(false);

        var context = new AgentRequestContext
        {
            Message = message,
            TaskId = taskId,
            ContextId = contextId,
            Task = existing?.DeepCopy(), // the executor's own view: changing it never changes the stored task
            Configuration = request.Configuration,
            Metadata = request.Metadata,
            Call = call,
        };
        return new Prepared(context, existing is null ? push : null, lastSequence);
    }

    /// <summary>
    /// Returns the context id for a new task and makes sure the context belongs to the caller. A context owned by
    /// someone else looks like a missing one.
    /// </summary>
    private async Task<string> ResolveContextAsync(string? requested, ServerCallContext call, CancellationToken ct)
    {
        const string NotFound = "contextId not found";
        if (string.IsNullOrEmpty(requested))
        {
            var created = await _tasks.GetOrCreateContextAsync(
                new ContextRecord(call.Tenant, call.Principal, TaskIds.New(), DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
            if (!string.Equals(created.Owner, call.Principal, StringComparison.Ordinal)) throw A2AException.InvalidParams(NotFound);
            return created.ContextId;
        }

        var existing = await _tasks.GetContextAsync(call.Tenant, requested, ct).ConfigureAwait(false);
        if (existing is null)
        {
            if (_options.ClientContextIdPolicy != ClientContextIdPolicy.AdoptIfUnknown) throw A2AException.InvalidParams(NotFound);
            existing = await _tasks.GetOrCreateContextAsync(
                new ContextRecord(call.Tenant, call.Principal, requested, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        }
        if (!string.Equals(existing.Owner, call.Principal, StringComparison.Ordinal)) throw A2AException.InvalidParams(NotFound);
        return existing.ContextId;
    }

    // =====================================================================
    // Tasks
    // =====================================================================

    /// <summary>GetTask (spec §3.1.3). Throws TaskNotFound when the task does not exist or belongs to another caller.</summary>
    public async Task<AgentTask> GetTaskAsync(GetTaskRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.GetTask, request, request.Id, cancellationToken).ConfigureAwait(false);
        A2AValidator.RequireId(request.Id, "id");
        if (request.HistoryLength is < 0) throw A2AException.InvalidParams("historyLength must be >= 0");
        var record = await _tasks.GetAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        return Shape(record.Task, request.HistoryLength);
    }

    /// <summary>ListTasks (spec §3.1.4). Returns the caller's tasks, newest status first. The page size defaults to 50 and is capped by <see cref="A2ALimits.MaxListPageSize"/>.</summary>
    public async Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.ListTasks, request, null, cancellationToken).ConfigureAwait(false);
        if (request.PageSize is < 1) throw A2AException.InvalidParams("pageSize must be between 1 and 100");
        if (request.HistoryLength is < 0) throw A2AException.InvalidParams("historyLength must be >= 0");
        var pageSize = Math.Min(request.PageSize ?? 50, _options.Limits.MaxListPageSize);

        var result = await _tasks.ListAsync(new TaskQuery(
            call.Tenant,
            call.Principal,
            string.IsNullOrEmpty(request.ContextId) ? null : request.ContextId,
            request.Status is null or TaskState.Unspecified ? null : request.Status,
            request.StatusTimestampAfter,
            pageSize,
            request.PageToken), cancellationToken).ConfigureAwait(false);

        var include = request.IncludeArtifacts == true;
        return new ListTasksResponse
        {
            Tasks = result.Records.Select(r => Shape(r.Task, request.HistoryLength, include)).ToList(),
            NextPageToken = result.NextPageToken,
            PageSize = pageSize,
            TotalSize = result.TotalSize,
        };
    }

    /// <summary>CancelTask (spec §3.1.5). Asks the running agent to stop, waits up to <see cref="A2AServerOptions.CancelGracePeriod"/>, and returns the task. Throws TaskNotCancelable when the task is already terminal.</summary>
    public async Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.CancelTask, request, request.Id, cancellationToken).ConfigureAwait(false);
        A2AValidator.RequireId(request.Id, "id");
        var record = await _tasks.GetAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        if (record.Task.Status.State.IsTerminal()) throw NotCancelable(request.Id);

        var running = _engine.CancelRunning(Key(call, request.Id));
        var exec = _engine.StartCancel(Key(call, request.Id), record.Task, request.Metadata);
        exec.Detach();

        var waitAll = Task.WhenAll(running.Select(r => r.Completion).Append(exec.Completion));
        await Task.WhenAny(waitAll, Task.Delay(_options.CancelGracePeriod, cancellationToken)).ConfigureAwait(false);

        record = await _tasks.GetAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        if (record.Task.Status.State != TaskState.Canceled) throw NotCancelable(request.Id);
        return Shape(record.Task, null);
    }

    /// <summary>A fresh subscribe: a snapshot of the task, then live events until the task ends (spec §3.1.6).</summary>
    public IAsyncEnumerable<SequencedEvent> SubscribeToTaskAsync(SubscribeToTaskRequest request, ServerCallContext call, CancellationToken cancellationToken = default) =>
        SubscribeToTaskAsync(request, call, resumeAfter: null, cancellationToken);

    /// <summary>
    /// Subscribes to a task, or resumes a stream (spec design §7.4). Events never repeat and no sequence is skipped.
    /// </summary>
    /// <param name="request">The task to stream.</param>
    /// <param name="call">The caller.</param>
    /// <param name="resumeAfter">
    /// Null for a fresh subscribe: a task snapshot, then live events; a terminal task is UnsupportedOperation (spec-exact).
    /// Otherwise the last sequence the client saw (from <c>Last-Event-ID</c>):
    /// <list type="bullet">
    /// <item>still retained: the events after it, then live events;</item>
    /// <item>older than the retained events, nothing retained, beyond the last event, or negative (a cursor that could not be
    /// used): a task snapshot, stamped with the last sequence, then live events;</item>
    /// <item>task terminal and the cursor at or past its last event: a task snapshot, then the stream ends.</item>
    /// </list>
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public IAsyncEnumerable<SequencedEvent> SubscribeToTaskAsync(SubscribeToTaskRequest request, ServerCallContext call, long? resumeAfter,
        CancellationToken cancellationToken = default) =>
        SubscribeToTaskAsync(request, call, resumeAfter, onCaughtUp: null, cancellationToken);

    /// <summary>As the public overload; <paramref name="onCaughtUp"/> is described on <see cref="SubscribeCoreAsync"/>.</summary>
    internal IAsyncEnumerable<SequencedEvent> SubscribeToTaskAsync(SubscribeToTaskRequest request, ServerCallContext call, long? resumeAfter,
        Action? onCaughtUp, CancellationToken cancellationToken) =>
        LimitStreamAsync(SubscribeCoreAsync(request, call, resumeAfter, onCaughtUp, CancellationToken.None), call, A2AOperation.SubscribeToTask, request, cancellationToken);

    /// <summary>
    /// The subscribe and resume logic. <paramref name="onCaughtUp"/> runs once when a resume was accepted but has nothing to
    /// send yet (the client already has every event): the HTTP layer then starts the response at once instead of holding
    /// the headers until the next live event.
    /// </summary>
    private async IAsyncEnumerable<SequencedEvent> SubscribeCoreAsync(SubscribeToTaskRequest request, ServerCallContext call, long? resumeAfter,
        Action? onCaughtUp, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.SubscribeToTask, request, request.Id, cancellationToken).ConfigureAwait(false);
        RequireStreaming();
        A2AValidator.RequireId(request.Id, "id");
        var key = Key(call, request.Id);
        var record = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        if (resumeAfter is null && record.Task.Status.State.IsTerminal())
            throw new A2AException(A2AErrorCode.UnsupportedOperation, "Cannot subscribe to a task in a terminal state.") { Metadata = new() { ["taskId"] = request.Id } };

        using var slot = _admission.Enter(key, call.LimitPartition);
        using var sub = _bus.Subscribe(ScopeKey(call.Tenant, request.Id));
        // Read the starting point after subscribing, so every later event reaches the subscription (spec §3.1.6).
        var start = await ReadStartAsync(key, cancellationToken).ConfigureAwait(false);
        var terminal = start.Task.Status.State.IsTerminal();

        long lastYielded;
        var sentAny = false;
        if (resumeAfter is { } cursor && CanReplay(cursor, start, terminal))
        {
            lastYielded = cursor;
            await foreach (var stored in ReadContiguousAsync(key, cursor, long.MaxValue, cancellationToken).ConfigureAwait(false))
            {
                sentAny = true;
                yield return stored;
                lastYielded = stored.Sequence;
                if (IsFinal(stored.Event)) yield break;
            }
            if (lastYielded < start.Last)
            {
                // Events were trimmed while we read them. Never skip: send a snapshot instead.
                var snapshot = await ReadSnapshotAsync(key, cancellationToken).ConfigureAwait(false);
                sentAny = true;
                yield return snapshot;
                lastYielded = snapshot.Sequence;
                if (IsFinal(snapshot.Event)) yield break;
            }
            else if (terminal) yield break;
        }
        else
        {
            sentAny = true;
            yield return new SequencedEvent(start.Last, StreamResponse.Of(Shape(start.Task, null)));
            if (terminal) yield break;
            lastYielded = start.Last;
        }
        if (!sentAny) onCaughtUp?.Invoke();

        // Live events arrive in sequence order (they are published under the task lock). Drop what was already sent; fill
        // a gap from the store, or with a snapshot when the store no longer has the missing events.
        var position = new StreamPosition(lastYielded);
        await foreach (var live in sub.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await foreach (var e in InOrderAsync(key, live, position, cancellationToken).ConfigureAwait(false)) yield return e;
            if (position.Final) yield break;
        }

        // The bus ended the subscription (the task went terminal) without delivering a final event: a lossy bus can drop
        // the terminal event itself. Every event is in the store before it is published, so finish from there.
        await foreach (var stored in ReadContiguousAsync(key, position.Last, long.MaxValue, cancellationToken).ConfigureAwait(false))
        {
            yield return stored;
            position.Last = stored.Sequence;
            if (IsFinal(stored.Event)) yield break;
        }
        // The store no longer has the missing events (trimmed): end with a snapshot, as the live loop does, never without a final event.
        var last = await ReadSnapshotAsync(key, cancellationToken).ConfigureAwait(false);
        if (last.Sequence > position.Last) yield return last;
    }

    /// <summary>A task with its last and oldest retained event sequences, read together.</summary>
    private readonly record struct StreamStart(AgentTask Task, long Last, long? Oldest);

    /// <summary>Reads the task and its sequences under the task lock, so the snapshot and the sequences belong together.</summary>
    private async Task<StreamStart> ReadStartAsync(TaskKey key, CancellationToken ct)
    {
        using (await _engine.LockAsync(key, ct).ConfigureAwait(false))
        {
            var record = await _tasks.GetAsync(key, ct).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(key.TaskId);
            var last = await _events.LastSequenceAsync(key, ct).ConfigureAwait(false);
            var oldest = await _events.OldestRetainedAsync(key, ct).ConfigureAwait(false);
            return new StreamStart(record.Task, last, oldest);
        }
    }

    /// <summary>A snapshot of the task, stamped with its last sequence: the client has seen everything up to it.</summary>
    private async Task<SequencedEvent> ReadSnapshotAsync(TaskKey key, CancellationToken ct)
    {
        var start = await ReadStartAsync(key, ct).ConfigureAwait(false);
        return new SequencedEvent(start.Last, StreamResponse.Of(Shape(start.Task, null)));
    }

    /// <summary>
    /// True when the events after <paramref name="cursor"/> are still retained, so replay is gap-free. A terminal task whose
    /// last event the client already has gets a snapshot instead (there is nothing to replay and no live event will follow).
    /// </summary>
    private static bool CanReplay(long cursor, StreamStart start, bool terminal) =>
        start.Oldest is { } oldest && cursor >= oldest - 1 && (cursor < start.Last || (cursor == start.Last && !terminal));

    /// <summary>Stored events after <paramref name="after"/> and before <paramref name="before"/>, in order, stopping at the first gap.</summary>
    private async IAsyncEnumerable<SequencedEvent> ReadContiguousAsync(TaskKey key, long after, long before,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var expected = after + 1;
        await foreach (var stored in _events.ReadAfterAsync(key, after, ct).ConfigureAwait(false))
        {
            if (stored.Sequence >= before || stored.Sequence != expected) yield break;
            yield return stored;
            expected++;
        }
    }

    /// <summary>True for the last event of a task's stream: a terminal status update or a snapshot of a terminal task.</summary>
    private static bool IsFinal(StreamResponse evt) => evt switch
    {
        StatusUpdateEvent { StatusUpdate.Status.State: var state } => state.IsTerminal(),
        TaskEvent { Task.Status.State: var state } => state.IsTerminal(),
        _ => false,
    };

    /// <summary>
    /// Applies the per-stream limits (spec design §7.2, §7.7). Every <see cref="A2ALimits.StreamReauthInterval"/> the caller is
    /// authorized again; on denial nothing more is sent and the stream ends. At <see cref="A2ALimits.MaxStreamDuration"/> the
    /// stream ends normally, without an error; clients resume with <c>Last-Event-ID</c>. Stopping the stream never stops the task.
    /// </summary>
    private async IAsyncEnumerable<SequencedEvent> LimitStreamAsync(IAsyncEnumerable<SequencedEvent> source, ServerCallContext call,
        A2AOperation op, object request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var limits = _options.Limits;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (IsTimeLimit(limits.MaxStreamDuration)) stop.CancelAfter(limits.MaxStreamDuration);
        TimeSpan? reauthEvery = IsTimeLimit(limits.StreamReauthInterval) ? limits.StreamReauthInterval : null;
        static long Ticks(TimeSpan t) => (long)(t.TotalSeconds * Stopwatch.Frequency);

        var events = source.GetAsyncEnumerator(stop.Token);
        Task<bool>? move = null;
        var reauthAt = reauthEvery is { } first ? Stopwatch.GetTimestamp() + Ticks(first) : long.MaxValue;
        try
        {
            while (true)
            {
                move = events.MoveNextAsync().AsTask();
                // Idle stream: wake up at the re-check deadline while waiting for the next event.
                while (reauthEvery is not null && !move.IsCompleted)
                {
                    var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), reauthAt);
                    if (wait > TimeSpan.Zero)
                    {
                        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        var delay = Task.Delay(wait, delayCts.Token);
                        var winner = await Task.WhenAny(move, delay).ConfigureAwait(false);
                        delayCts.Cancel();
                        if (winner == move || stop.IsCancellationRequested) break;
                    }
                    if (!await StillAuthorizedAsync().ConfigureAwait(false)) yield break; // the finally stops the source
                }

                bool more;
                try
                {
                    more = await move.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    yield break; // MaxStreamDuration reached
                }
                if (!more) yield break;

                // Busy stream: the next event may always be ready, so check the deadline before every event too.
                if (Stopwatch.GetTimestamp() >= reauthAt && !await StillAuthorizedAsync().ConfigureAwait(false)) yield break;
                yield return events.Current;
            }
        }
        finally
        {
            stop.Cancel();
            if (move is { IsCompleted: false })
            {
                // An async iterator cannot be disposed while it runs; wait for the cancellation to reach it.
                try { await move.ConfigureAwait(false); }
                catch (Exception) { /* the stream is stopping anyway */ }
            }
            await events.DisposeAsync().ConfigureAwait(false);
        }

        // Asks the authorization handler again and moves the deadline.
        async ValueTask<bool> StillAuthorizedAsync()
        {
            var allowed = await _authorization.AuthorizeAsync(call, op, request, cancellationToken).ConfigureAwait(false);
            reauthAt = Stopwatch.GetTimestamp() + Ticks(reauthEvery!.Value);
            return allowed;
        }
    }

    /// <summary>True for a usable timer value. Zero, negative (including <see cref="Timeout.InfiniteTimeSpan"/>) and huge values turn the limit off.</summary>
    private static bool IsTimeLimit(TimeSpan value) => value > TimeSpan.Zero && value.TotalMilliseconds < int.MaxValue;

    /// <summary>Open streams counted against the stream limits. For tests.</summary>
    internal int OpenStreams => _admission.OpenStreams;

    // =====================================================================
    // Push notification configs
    // =====================================================================

    /// <summary>CreateTaskPushNotificationConfig (spec §3.1.7). Validates the webhook URL, stores the registration and returns it with secrets removed.</summary>
    public async Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(config.Tenant);
        await AuthorizeAsync(call, A2AOperation.CreateTaskPushNotificationConfig, config, config.TaskId, cancellationToken).ConfigureAwait(false);
        RequirePush();
        A2AValidator.ValidatePushConfig(config, "config", requireTaskId: true);
        var key = Key(call, config.TaskId!);
        _ = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(config.TaskId!);
        return await _pushConfigs.CreateAsync(key, config, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>GetTaskPushNotificationConfig (spec §3.1.8). Returns one registration with secrets removed.</summary>
    public async Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.GetTaskPushNotificationConfig, request, request.TaskId, cancellationToken).ConfigureAwait(false);
        RequirePush();
        A2AValidator.RequireId(request.TaskId, "taskId");
        A2AValidator.RequireId(request.Id, "id");
        var key = Key(call, request.TaskId);
        _ = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.TaskId);
        return await _pushConfigs.GetAsync(key, request.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>ListTaskPushNotificationConfigs (spec §3.1.9). Returns the registrations of a task, 100 per page at most, with secrets removed.</summary>
    public async Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.ListTaskPushNotificationConfigs, request, request.TaskId, cancellationToken).ConfigureAwait(false);
        RequirePush();
        A2AValidator.RequireId(request.TaskId, "taskId");
        var key = Key(call, request.TaskId);
        _ = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.TaskId);
        return await _pushConfigs.ListAsync(key, request.PageSize ?? 0, request.PageToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DeleteTaskPushNotificationConfig (spec §3.1.10). Throws when the registration does not exist.</summary>
    public async Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.DeleteTaskPushNotificationConfig, request, request.TaskId, cancellationToken).ConfigureAwait(false);
        RequirePush();
        A2AValidator.RequireId(request.TaskId, "taskId");
        A2AValidator.RequireId(request.Id, "id");
        var key = Key(call, request.TaskId);
        _ = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.TaskId);
        await _pushConfigs.DeleteAsync(key, request.Id, cancellationToken).ConfigureAwait(false);
    }

    // =====================================================================
    // Extended Agent Card
    // =====================================================================

    /// <summary>GetExtendedAgentCard (spec §3.1.11). Needs an authenticated caller and a card from <see cref="A2AServerOptions.ExtendedAgentCardProvider"/>.</summary>
    public async Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.GetExtendedAgentCard, request, null, cancellationToken).ConfigureAwait(false);
        if (_options.AgentCard.Capabilities.ExtendedAgentCard != true)
            throw new A2AException(A2AErrorCode.UnsupportedOperation, "This agent does not provide an extended agent card.");
        if (!call.IsAuthenticated) throw new A2AException(A2AErrorCode.Unauthenticated, "The extended agent card requires authentication.");
        var card = _options.ExtendedAgentCardProvider is null ? null : await _options.ExtendedAgentCardProvider(call, cancellationToken).ConfigureAwait(false);
        return card ?? throw new A2AException(A2AErrorCode.ExtendedAgentCardNotConfigured);
    }

    /// <summary>Status text of a task failed because its updates could not be stored (see <see cref="TaskExecutionEngine"/>).</summary>
    internal const string StoreFailureText = TaskExecutionEngine.StoreFailureText;

    // =====================================================================
    // Helpers
    // =====================================================================

    private static TaskKey Key(ServerCallContext call, string taskId) => new(call.Tenant, call.Principal, taskId);

    private AgentTask Shape(AgentTask task, int? historyLength, bool includeArtifacts = true) =>
        TaskShaper.Shape(task, historyLength, _options.Limits, includeArtifacts);

    private void RequireStreaming()
    {
        if (_options.AgentCard.Capabilities.Streaming != true)
            throw new A2AException(A2AErrorCode.UnsupportedOperation, "Streaming is not supported by this agent.");
    }

    private void RequirePush()
    {
        if (_options.AgentCard.Capabilities.PushNotifications != true)
            throw new A2AException(A2AErrorCode.PushNotificationNotSupported);
    }

    private static A2AException NotCancelable(string taskId) =>
        new(A2AErrorCode.TaskNotCancelable) { Metadata = new() { ["taskId"] = taskId } };

    private static string ScopeKey(string tenant, string taskId) => TaskExecutionEngine.ScopeKey(tenant, taskId);

    /// <summary>Waits for all running executions to finish. For tests and graceful shutdown.</summary>
    public Task WhenIdleAsync() => _engine.WhenIdleAsync();

    private int _disposed;

    /// <summary>
    /// Stops running executions. Safe to call more than once. The <see cref="TaskExecutionEngine"/> is not disposed here: its
    /// owner (the DI container) does that.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _engine.Shutdown();
    }
}
