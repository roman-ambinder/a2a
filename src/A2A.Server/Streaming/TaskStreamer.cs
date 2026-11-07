using System.Diagnostics;
using System.Runtime.CompilerServices;
using A2A.Server.Authorization;
using A2A.Server.Execution;
using A2A.Server.Idempotency;
using A2A.Server.Pipeline;
using A2A.Server.Storage;
using A2A.Validation;

namespace A2A.Server.Streaming;

/// <summary>
/// Accepts a streamed message and starts its execution: the request handler's accept step, which it shares with SendMessage.
/// It abandons <paramref name="claim"/> when the message is refused or the agent cannot start.
/// </summary>
/// <returns>The execution, and the task's last sequence before it (0 for a new task).</returns>
internal delegate Task<(Execution.Execution Execution, long AfterSequence)> AcceptMessage(SendMessageRequest request, ServerCallContext call,
    IdempotencyClaim claim, string taskId, CancellationToken ct);

/// <summary>
/// Follows a task's events: the SendStreamingMessage stream of a new execution, SubscribeToTask (fresh or resumed), and the
/// wait of a blocking SendMessage. Every stream sends the task's events in sequence order without repeats, fills gaps from
/// the event store (a snapshot when the store no longer has them), and is limited by <see cref="A2ALimits"/> (concurrent
/// streams, re-authorization, duration).
/// In an ASP.NET Core app the DI container creates it; protocol bindings call <see cref="A2ARequestHandler"/>, not this class.
/// </summary>
public sealed class TaskStreamer
{
    private readonly TaskStore _tasks;
    private readonly EventStore _events;
    private readonly TaskEventBus _bus;
    private readonly TaskExecutionEngine _engine;
    private readonly IdempotencyCoordinator _idempotency;
    private readonly A2AAuthorizationHandler _authorization;
    private readonly A2AServerOptions _options;
    private readonly StreamAdmission _admission;

    /// <summary>Creates the streamer. In an ASP.NET Core app the DI container does this; the parameters are the registered services.</summary>
    public TaskStreamer(
        TaskStore tasks,
        EventStore events,
        TaskExecutionEngine engine,
        IdempotencyCoordinator idempotency,
        A2AAuthorizationHandler authorization,
        A2AServerOptions options)
    {
        _tasks = tasks;
        _events = events;
        _bus = engine.Bus; // the bus the engine publishes to: streams subscribe to the same one
        _engine = engine;
        _idempotency = idempotency;
        _authorization = authorization;
        _options = options;
        _admission = new StreamAdmission(options.Limits);
    }

    /// <summary>Open streams counted against the stream limits. For tests.</summary>
    internal int OpenStreams => _admission.OpenStreams;

    /// <summary>
    /// SendStreamingMessage: authorizes, handles an idempotent retry, takes a stream slot, accepts the message with
    /// <paramref name="accept"/>, then streams the task's events (see <see cref="A2ARequestHandler.SendStreamingMessageAsync(SendMessageRequest, ServerCallContext, CancellationToken)"/>).
    /// Everything runs when the stream is enumerated, inside the stream limits.
    /// </summary>
    internal IAsyncEnumerable<SequencedEvent> StreamExecutionAsync(SendMessageRequest request, ServerCallContext call, AcceptMessage accept,
        CancellationToken cancellationToken) =>
        // CancellationToken.None: LimitStreamAsync owns the token ([EnumeratorCancellation]) and passes its linked token to the core.
        LimitStreamAsync(SendStreamingCoreAsync(request, call, accept, CancellationToken.None), call, A2AOperation.SendStreamingMessage, request, cancellationToken);

    /// <summary>
    /// SubscribeToTask, fresh or resumed (see <see cref="A2ARequestHandler.SubscribeToTaskAsync(SubscribeToTaskRequest, ServerCallContext, long?, CancellationToken)"/>),
    /// inside the stream limits. <paramref name="onCaughtUp"/> is described on <see cref="SubscribeCoreAsync"/>.
    /// </summary>
    internal IAsyncEnumerable<SequencedEvent> SubscribeAsync(SubscribeToTaskRequest request, ServerCallContext call, long? resumeAfter,
        Action? onCaughtUp, CancellationToken cancellationToken) =>
        // CancellationToken.None: LimitStreamAsync owns the token ([EnumeratorCancellation]) and passes its linked token to the core.
        LimitStreamAsync(SubscribeCoreAsync(request, call, resumeAfter, onCaughtUp, CancellationToken.None), call, A2AOperation.SubscribeToTask, request, cancellationToken);

    /// <summary>
    /// The blocking SendMessage, the counterpart of the send stream with the same end rule: follows <paramref name="exec"/> and,
    /// on <paramref name="sub"/>, the task's other executions (for example CancelTask) until the task is terminal or interrupted,
    /// or until this execution ended and no other execution of the task is running. With <paramref name="returnImmediately"/>
    /// it stops after the execution's first event. Detaches <paramref name="exec"/> when done.
    /// </summary>
    /// <returns>The agent's direct reply, or null when the response is the task (read it from the store).</returns>
    internal async Task<Message?> WaitForOutcomeAsync(Execution.Execution exec, TaskKey key, TaskEventBus.Subscription sub, long afterSequence,
        bool returnImmediately, CancellationToken cancellationToken)
    {
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
                    if (evt is MessageEvent reply) return reply.Message;
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
        return null;
    }

    private async IAsyncEnumerable<SequencedEvent> SendStreamingCoreAsync(SendMessageRequest request, ServerCallContext call, AcceptMessage accept,
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
        var (exec, _) = await accept(request, call, claim, taskId, cancellationToken).ConfigureAwait(false);
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
            yield return new SequencedEvent(start.Last, StreamResponse.Of(Shape(start.Task)));
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
        return new SequencedEvent(start.Last, StreamResponse.Of(Shape(start.Task)));
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

    private static TaskKey Key(ServerCallContext call, string taskId) => new(call.Tenant, call.Principal, taskId);

    private static string ScopeKey(string tenant, string taskId) => TaskExecutionEngine.ScopeKey(tenant, taskId);

    private AgentTask Shape(AgentTask task) => TaskShaper.Shape(task, null, _options.Limits);

    private Task AuthorizeAsync(ServerCallContext call, A2AOperation op, object request, string? taskId, CancellationToken ct) =>
        _authorization.RequireAsync(_options, call, op, request, taskId, ct);

    private void RequireStreaming()
    {
        if (_options.AgentCard.Capabilities.Streaming != true)
            throw new A2AException(A2AErrorCode.UnsupportedOperation, "Streaming is not supported by this agent.");
    }
}
