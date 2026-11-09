using System.Diagnostics;
using System.Runtime.CompilerServices;
using A2A.Diagnostics;
using A2A.Server.Agents;
using A2A.Server.Audit;
using A2A.Server.Authorization;
using A2A.Server.Execution;
using A2A.Server.Idempotency;
using A2A.Server.Pipeline;
using A2A.Server.Storage;
using A2A.Server.Telemetry;
using A2A.Validation;

namespace A2A.Server.Streaming;

/// <summary>
/// Accepts a streamed message and starts its execution: the request handler's accept step, which it shares with SendMessage.
/// It abandons <paramref name="claim"/> when the message is refused or the agent cannot start.
/// </summary>
/// <returns>
/// The execution, the task's last sequence before it (0 for a new task), and for a follow-up the timestamp of the task's
/// status when the message was accepted (see <see cref="TaskExecutionEngine.StatusChangedSince"/>). The execution is null for
/// a duplicate follow-up: nothing was started. The execution takes the lease of <paramref name="agent"/>.
/// </returns>
internal delegate Task<(Execution.Execution? Execution, long AfterSequence, DateTimeOffset? AcceptedStatusTimestamp)> AcceptMessage(
    SendMessageRequest request, ServerCallContext call, IdempotencyClaim claim, string taskId, RentedAgent agent, CancellationToken ct);

/// <summary>
/// Follows a task's events: the SendStreamingMessage stream of a new execution, SubscribeToTask (fresh or resumed), and the
/// wait of a blocking SendMessage. Every stream sends the task's events in sequence order without repeats, fills gaps from
/// the event store (a snapshot when the store no longer has them), and is limited by <see cref="A2ALimits"/> (concurrent
/// streams, re-authorization, duration).
/// Internal: <see cref="A2ARequestHandler"/> creates its own from the services it is given; protocol bindings call the handler.
/// </summary>
internal sealed class TaskStreamer
{
    private readonly TaskStore _tasks;
    private readonly EventStore _events;
    private readonly TaskEventBus _bus;
    private readonly TaskExecutionEngine _engine;
    private readonly IdempotencyCoordinator _idempotency;
    private readonly A2AAuthorizationHandler _authorization;
    private readonly A2AServerOptions _options;
    private readonly RequestCards _cards;
    private readonly SkillAuthorizer _skills;
    private readonly StreamAdmission _admission;
    private readonly A2AAuditor _audit;

    /// <summary>Creates the streamer from the request handler's services.</summary>
    internal TaskStreamer(
        TaskStore tasks,
        EventStore events,
        TaskExecutionEngine engine,
        IdempotencyCoordinator idempotency,
        A2AAuthorizationHandler authorization,
        A2AServerOptions options,
        IAgentCardProvider cards,
        SkillAuthorizer skills,
        A2AAuditor? audit = null)
    {
        _audit = audit ?? A2AAuditor.None;
        _cards = new RequestCards(cards);
        _skills = skills;
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

    /// <summary>Most a long-poll waits, whatever <see cref="A2AServerOptions.LongPollTimeout"/> says.</summary>
    internal static readonly TimeSpan MaxLongPoll = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A2A 1.1 long-poll (§3.2.7): the task once its generation is above <paramref name="after"/>, or as it is when it is
    /// terminal (nothing changes it any more), when <see cref="A2AServerOptions.LongPollTimeout"/> passes, or when the wait is
    /// cancelled by the server. The generation equals the last event sequence, so any event published for the task can end
    /// the wait; the task is read again after subscribing, so no change between the first read and the subscription is missed.
    /// The wait holds a stream slot. Throws TaskNotFound like GetTask.
    /// </summary>
    internal async Task<TaskRecord> WaitPastGenerationAsync(TaskKey key, ServerCallContext call, long after, CancellationToken ct)
    {
        var record = await _tasks.GetAsync(key, ct).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(key.TaskId);
        var timeout = _options.LongPollTimeout > MaxLongPoll ? MaxLongPoll : _options.LongPollTimeout;
        if (await IsPastAsync(key, record, after, ct).ConfigureAwait(false) || timeout <= TimeSpan.Zero) return record;

        using var slot = _admission.Enter(key, call.LimitPartition);
        using var sub = _bus.Subscribe(ScopeKey(call.Tenant, key.TaskId));
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(timeout);
        try
        {
            while (true)
            {
                record = await _tasks.GetAsync(key, ct).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(key.TaskId);
                if (await IsPastAsync(key, record, after, ct).ConfigureAwait(false)) return record;
                if (!await sub.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false)) break; // the topic ended: terminal
                while (sub.Reader.TryRead(out _)) { } // read the task again, whatever the events were
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The time limit: answer with the task as it is (the spec allows an earlier answer).
        }
        return await _tasks.GetAsync(key, ct).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(key.TaskId);
    }

    private async ValueTask<bool> IsPastAsync(TaskKey key, TaskRecord record, long after, CancellationToken ct) =>
        record.Task.Status.State.IsTerminal()
        || (record.Task.Generation ?? await _events.LastSequenceAsync(key, ct).ConfigureAwait(false)) > after;

    /// <summary>
    /// SendStreamingMessage: handles an idempotent retry, takes a stream slot, accepts the message with
    /// <paramref name="accept"/>, then streams the task's events (see <see cref="A2ARequestHandler.SendStreamingMessageAsync"/>).
    /// Everything runs when the stream is enumerated, inside the stream limits. The caller has bound the tenant and authorized
    /// the call (the operation pipeline); the stream limits authorize it again as <paramref name="operation"/>.
    /// </summary>
    internal IAsyncEnumerable<SequencedEvent> StreamExecutionAsync(SendMessageRequest request, ServerCallContext call, AcceptMessage accept,
        A2AOperationDescriptor operation, CancellationToken cancellationToken) =>
        // CancellationToken.None: LimitStreamAsync owns the token ([EnumeratorCancellation]) and passes its linked token to the core.
        LimitStreamAsync(ShapeHistoryAsync(SendStreamingCoreAsync(request, call, accept, CancellationToken.None), request.Configuration?.HistoryLength,
            request.Configuration?.TimelineLength, CancellationToken.None),
            call, operation, request, cancellationToken);

    /// <summary>
    /// Applies <c>historyLength</c> and the history cap to every task event of SendStreamingMessage (the first event, gap
    /// snapshots and replays), like every other path that returns a task. Stored events are not changed.
    /// </summary>
    private async IAsyncEnumerable<SequencedEvent> ShapeHistoryAsync(IAsyncEnumerable<SequencedEvent> source, int? historyLength,
        int? timelineLength, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return TaskShaper.ShapeEvent(evt, historyLength, _options.Limits, timelineLength);
    }

    /// <summary>
    /// SubscribeToTask, fresh or resumed (see <see cref="A2ARequestHandler.SubscribeToTaskAsync"/>),
    /// inside the stream limits. <paramref name="onCaughtUp"/> is described on <see cref="SubscribeCoreAsync"/>. The caller has
    /// bound the tenant and authorized the call (the operation pipeline); the stream limits authorize it again as <paramref name="operation"/>.
    /// </summary>
    internal IAsyncEnumerable<SequencedEvent> SubscribeAsync(SubscribeToTaskRequest request, ServerCallContext call, long? resumeAfter,
        Action? onCaughtUp, A2AOperationDescriptor operation, CancellationToken cancellationToken) =>
        // CancellationToken.None: LimitStreamAsync owns the token ([EnumeratorCancellation]) and passes its linked token to the core.
        LimitStreamAsync(SubscribeCoreAsync(request, call, resumeAfter, onCaughtUp, CancellationToken.None), call, operation, request, cancellationToken);

    /// <summary>
    /// The blocking SendMessage, the counterpart of the send stream with the same end rule: follows <paramref name="exec"/> and,
    /// on <paramref name="sub"/>, the task's other executions (for example CancelTask) until the task is terminal or interrupted,
    /// or until this execution ended and no other execution of the task is running. With <paramref name="returnImmediately"/>
    /// it stops after the execution's first event. Detaches <paramref name="exec"/> when done.
    /// <paramref name="acceptedStatusTimestamp"/> is, for a follow-up, the timestamp of the task's status when the message was
    /// accepted (see <see cref="SettledInStoreAsync"/>).
    /// </summary>
    /// <returns>The agent's direct reply, or null when the response is the task (read it from the store).</returns>
    internal async Task<Message?> WaitForOutcomeAsync(Execution.Execution exec, TaskKey key, TaskEventBus.Subscription sub, long afterSequence,
        DateTimeOffset? acceptedStatusTimestamp, bool returnImmediately, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var own = exec.Outgoing.Reader;
            Task<bool>? ownWait = null, busWait = null; // one pending wait per reader: both have a single reader
            var ownDone = false;
            var started = false;
            // The last sequence seen on the bus. Every event after afterSequence is published after the subscription was made,
            // so a jump means the bounded subscription dropped events.
            var busLast = afterSequence;
            while (true)
            {
                if (exec.Failed) throw new A2AException(A2AErrorCode.Internal); // storage failed: an error, not the FAILED task

                // Other executions (CancelTask, a later follow-up) settle the task too; so does this one, seen on the bus when
                // its own channel dropped the event.
                var settledElsewhere = false;
                long? missedAfter = null;
                while (sub.Reader.TryRead(out var other))
                {
                    if (other.Sequence > busLast + 1) missedAfter ??= busLast;
                    busLast = Math.Max(busLast, other.Sequence);
                    if (other.Sequence > afterSequence && Settles(other.Event)) settledElsewhere = true;
                }
                if (!settledElsewhere && missedAfter is { } after)
                    settledElsewhere = await SettledInStoreAsync(key, after, busLast, acceptedStatusTimestamp, stop.Token).ConfigureAwait(false);
                if (settledElsewhere) break;

                if (!ownDone && own.TryRead(out var sequenced))
                {
                    var evt = sequenced.Event;
                    if (evt is MessageEvent reply) return reply.Message;
                    started = true;
                    if (returnImmediately) break;
                    if (Settles(evt)) break;
                    continue;
                }

                if (ownDone)
                {
                    // This execution ended. Nothing more will change the task once no execution of it runs, on any instance.
                    if (!started || await _engine.RunningWaitsAsync(key, stop.Token).ConfigureAwait(false) is not { } running) break;
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

    /// <summary>True for a status update to a terminal or interrupted state: the outcome a blocking SendMessage waits for.</summary>
    private static bool Settles(StreamResponse evt) =>
        evt is StatusUpdateEvent { StatusUpdate.Status.State: var state } && (state.IsTerminal() || state.IsInterrupted());

    /// <summary>
    /// True when the event store holds an event after <paramref name="after"/> and up to <paramref name="upTo"/> that
    /// <see cref="Settles"/> the task: the bus dropped events in that range. Every event is stored before it is published.
    /// When the store no longer has all of them (trimmed), the task itself decides: settled when it is terminal, or interrupted
    /// with a status set after the follow-up was accepted (its timestamp differs from <paramref name="acceptedStatusTimestamp"/>;
    /// a follow-up does not change the status itself, so an interrupted state from before it is not its outcome).
    /// </summary>
    private async Task<bool> SettledInStoreAsync(TaskKey key, long after, long upTo, DateTimeOffset? acceptedStatusTimestamp, CancellationToken ct)
    {
        var expected = after + 1;
        await foreach (var stored in _events.ReadAfterAsync(key, after, ct).ConfigureAwait(false))
        {
            if (stored.Sequence > upTo || stored.Sequence != expected) break;
            if (Settles(stored.Event)) return true;
            expected++;
        }
        if (expected > upTo) return false; // every event of the range was there, none settles the task

        var record = await _tasks.GetAsync(key, ct).ConfigureAwait(false);
        return record is null || record.Task.Status.State.IsTerminal()
            || (record.Task.Status.State.IsInterrupted() && TaskExecutionEngine.StatusChangedSince(record.Task.Status, acceptedStatusTimestamp));
    }

    private async IAsyncEnumerable<SequencedEvent> SendStreamingCoreAsync(SendMessageRequest request, ServerCallContext call, AcceptMessage accept,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RequestCards.RequireStreaming(await _cards.RequireAgentAsync(call, cancellationToken).ConfigureAwait(false));
        // Before the idempotency claim and anything stored: a tenant without an agent leaves no trace.
        await using var agent = await _engine.RequireAgentAsync(call.Tenant, cancellationToken).ConfigureAwait(false);
        var (claim, replay) = await _idempotency.BeginAsync(request, call, cancellationToken).ConfigureAwait(false);
        if (replay is not null) await agent.DisposeAsync().ConfigureAwait(false); // nothing runs
        if (replay is IdempotencyOutcome.DirectMessage direct)
        {
            // The stored outcome belongs to the idempotency store; the caller gets its own copy.
            yield return new SequencedEvent(0, StreamResponse.Of(direct.Message.DeepCopy()));
            yield break;
        }
        if (replay is IdempotencyOutcome.TaskCreated created)
        {
            // The same path as a resume from cursor 0: the retained events from the start, then live events.
            // The resume is a SubscribeToTask of the created task: authorized as one, as a reconnect would be.
            var resume = new SubscribeToTaskRequest { Id = created.TaskId, Tenant = request.Tenant };
            await AuthorizeAsync(call, A2AOperationDescriptor.For(A2AOperation.SubscribeToTask), resume, created.TaskId, cancellationToken).ConfigureAwait(false);
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
            ServerTelemetry.StreamsOpened.Add(1);
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
        var (accepted, _, _) = await accept(request, call, claim, taskId, agent, cancellationToken).ConfigureAwait(false);
        if (accepted is not { } exec)
        {
            await agent.DisposeAsync().ConfigureAwait(false); // nothing runs
            // The resume takes a stream slot and a subscription of its own: release these first, so a duplicate is not
            // refused for a slot nothing streams on (MaxConcurrentStreamsPerTask = 1).
            sub.Dispose();
            admitted.Dispose();
            // A duplicate follow-up: nothing started. The same path as a retried request: the retained events from the start,
            // then live events. The resume is a SubscribeToTask of the task: authorized as one, like every resume.
            var resume = new SubscribeToTaskRequest { Id = taskId, Tenant = request.Tenant };
            await AuthorizeAsync(call, A2AOperationDescriptor.For(A2AOperation.SubscribeToTask), resume, taskId, cancellationToken).ConfigureAwait(false);
            await foreach (var replayed in SubscribeCoreAsync(resume, call, resumeAfter: 0, onCaughtUp: null, cancellationToken).ConfigureAwait(false))
                yield return replayed;
            yield break;
        }
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
                    // Storage failed: send what this execution still has queued, then the events its bounded channel dropped
                    // (from the store, up to the last event it emitted, so never the FAILED event stored after them), then the
                    // error.
                    while (own.TryRead(out var queued))
                    {
                        if (TryInOrder(queued, position, out var isNextQueued)) { if (isNextQueued) yield return queued; }
                        else await foreach (var e in InOrderAsync(key, queued, position, cancellationToken).ConfigureAwait(false)) yield return e;
                        if (position.Final) yield break;
                    }
                    await foreach (var stored in ReadContiguousAsync(key, position.Last, exec.LastSequence + 1, cancellationToken).ConfigureAwait(false))
                    {
                        yield return stored;
                        position.Last = stored.Sequence;
                        if (IsFinal(stored.Event)) yield break;
                    }
                    throw new A2AException(A2AErrorCode.Internal);
                }

                // The bus first: another execution's event may come before this execution's next one.
                while (sub.Reader.TryRead(out var other))
                {
                    if (TryInOrder(other, position, out var isNextOther)) { if (isNextOther) yield return other; }
                    else await foreach (var e in InOrderAsync(key, other, position, cancellationToken).ConfigureAwait(false)) yield return e;
                    if (position.Final) yield break;
                }
                if (!ownDone && own.TryRead(out var mine))
                {
                    if (TryInOrder(mine, position, out var isNextMine)) { if (isNextMine) yield return mine; }
                    else await foreach (var e in InOrderAsync(key, mine, position, cancellationToken).ConfigureAwait(false)) yield return e;
                    if (position.Final) yield break;
                    continue;
                }

                if (ownDone)
                {
                    // Nothing will change the task any more once no execution of it runs, on any instance.
                    if (await _engine.RunningWaitsAsync(key, stop.Token).ConfigureAwait(false) is not { } running) break;
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
            else if (A2AProtocol.IsVersion11OrLater(call.ProtocolVersion))
            {
                // A2A 1.1 (§3.1.2): a non-terminal state, an interrupted one included, does not end the stream by itself. No
                // execution runs now, so the stream follows the task as a subscription until it is terminal (or the stream
                // limits end it). It releases this stream's slot and subscription first, and is authorized as a subscribe.
                sub.Dispose();
                admitted.Dispose();
                var follow = new SubscribeToTaskRequest { Id = taskId, Tenant = request.Tenant };
                await AuthorizeAsync(call, A2AOperationDescriptor.For(A2AOperation.SubscribeToTask), follow, taskId, cancellationToken).ConfigureAwait(false);
                await foreach (var e in SubscribeCoreAsync(follow, call, resumeAfter: position.Last, onCaughtUp: null, cancellationToken).ConfigureAwait(false))
                    yield return e;
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
    /// The common cases of <see cref="InOrderAsync"/> without an async iterator: true when <paramref name="live"/> was handled,
    /// as a repeat (dropped, <paramref name="next"/> false) or as the next event (<paramref name="position"/> updated; the caller
    /// sends it). False when there is a gap before it: the caller uses <see cref="InOrderAsync"/>.
    /// </summary>
    private static bool TryInOrder(SequencedEvent live, StreamPosition position, out bool next)
    {
        next = false;
        if (live.Sequence <= position.Last) return true;
        if (live.Sequence != position.Last + 1) return false;
        position.Last = live.Sequence;
        position.Final = IsFinal(live.Event);
        next = true;
        return true;
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
            await foreach (var filled in FillGapAsync(key, live.Sequence, position, ct).ConfigureAwait(false)) yield return filled;
            if (position.Final || live.Sequence <= position.Last) yield break;
        }
        position.Last = live.Sequence;
        position.Final = IsFinal(live.Event);
        yield return live;
    }

    /// <summary>
    /// Yields the stored events after <paramref name="position"/> and before <paramref name="before"/>, in order, and then a
    /// snapshot when the store no longer has the ones still missing. Updates <paramref name="position"/>; stops after a final event.
    /// </summary>
    private async IAsyncEnumerable<SequencedEvent> FillGapAsync(TaskKey key, long before, StreamPosition position,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var stored in ReadContiguousAsync(key, position.Last, before, ct).ConfigureAwait(false))
        {
            position.Last = stored.Sequence;
            position.Final = IsFinal(stored.Event);
            yield return stored;
            if (position.Final) yield break;
        }
        if (before > position.Last + 1)
        {
            var snapshot = await ReadSnapshotAsync(key, ct).ConfigureAwait(false);
            position.Last = snapshot.Sequence;
            position.Final = IsFinal(snapshot.Event);
            yield return snapshot;
        }
    }

    /// <summary>
    /// The subscribe and resume logic. <paramref name="onCaughtUp"/> runs once when a resume was accepted but has nothing to
    /// send yet (the client already has every event): the HTTP layer then starts the response at once instead of holding
    /// the headers until the next live event.
    /// </summary>
    private async IAsyncEnumerable<SequencedEvent> SubscribeCoreAsync(SubscribeToTaskRequest request, ServerCallContext call, long? resumeAfter,
        Action? onCaughtUp, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RequestCards.RequireStreaming(await _cards.RequireAgentForTaskAsync(call, request.Id, cancellationToken).ConfigureAwait(false));
        A2AValidator.RequireId(request.Id, "id");
        var key = Key(call, request.Id);
        var record = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        if (resumeAfter is null && record.Task.Status.State.IsTerminal())
            throw new A2AException(A2AErrorCode.UnsupportedOperation, "Cannot subscribe to a task in a terminal state.") { Metadata = new() { ["taskId"] = request.Id } };

        using var slot = _admission.Enter(key, call.LimitPartition);
        ServerTelemetry.StreamsOpened.Add(1);
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
        // A lost bus message, or an instance that died right after its commit, would leave an idle stream silent until
        // MaxStreamDuration. So every StreamCheckInterval an idle stream looks at the store (see CheckLiveAsync).
        var position = new StreamPosition(lastYielded);
        var interval = _options.Coordination.StreamCheckInterval;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sawLive = false; // an event came over the bus since the last check
        var strandedChecks = 0; // checks in a row that found a task nothing runs
        Task<bool>? busWait = null; // one pending wait per timer and per reader: the subscription has a single reader
        Task? tick = null;
        try
        {
            while (true)
            {
                while (sub.Reader.TryRead(out var live))
                {
                    sawLive = true;
                    if (TryInOrder(live, position, out var isNextLive)) { if (isNextLive) yield return live; }
                    else await foreach (var e in InOrderAsync(key, live, position, cancellationToken).ConfigureAwait(false)) yield return e;
                    if (position.Final) yield break;
                }

                busWait ??= sub.Reader.WaitToReadAsync(stop.Token).AsTask();
                tick ??= Task.Delay(interval, _engine.Clock, stop.Token);
                await Task.WhenAny(busWait, tick).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (busWait.IsCompleted)
                {
                    if (!await busWait.ConfigureAwait(false)) break; // the bus ended the subscription
                    busWait = null;
                    continue;
                }

                tick = null;
                if (sawLive)
                {
                    // A busy stream does no store reads.
                    sawLive = false;
                    strandedChecks = 0;
                    continue;
                }

                var check = await CheckLiveAsync(key, cancellationToken).ConfigureAwait(false);
                var filled = false;
                if (check.LastSequence > position.Last)
                {
                    await foreach (var e in FillGapAsync(key, check.LastSequence + 1, position, cancellationToken).ConfigureAwait(false)) yield return e;
                    if (position.Final) yield break;
                    filled = true;
                }
                if (check.Gone) yield break;
                if (check.Terminal) break; // finish from the store, below
                if (check.Stranded && !filled)
                {
                    // Two checks in a row, so a follow-up that is committed but not registered yet is not taken for a crash. A
                    // clean end, as at MaxStreamDuration: the client resumes with Last-Event-ID.
                    if (++strandedChecks >= 2) yield break;
                }
                else
                {
                    strandedChecks = 0;
                }
            }
        }
        finally
        {
            stop.Cancel();
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

    /// <summary>
    /// What an idle stream finds in the store: the last event sequence; <c>Gone</c> when the task no longer exists;
    /// <c>Terminal</c> when it is in a terminal state; <c>Stranded</c> when it is neither terminal nor interrupted and no
    /// execution of it runs on any instance. An interrupted task (input or auth required) has no execution and is never stranded.
    /// </summary>
    private readonly record struct LiveCheck(long LastSequence, bool Gone, bool Terminal, bool Stranded);

    /// <summary>
    /// Looks at the store for an idle stream: the last sequence, then the task, then, for a task that is neither terminal nor
    /// interrupted, whether an execution runs. A failing store or coordinator must not end a healthy stream: any failure is
    /// logged and answered with "nothing to do". A coordinator that fails counts as running (it is not asked through
    /// <see cref="TaskExecutionEngine.IsRunningAnywhereAsync"/>, which counts a failure as not running).
    /// </summary>
    private async Task<LiveCheck> CheckLiveAsync(TaskKey key, CancellationToken ct)
    {
        try
        {
            var last = await _events.LastSequenceAsync(key, ct).ConfigureAwait(false);
            var record = await _tasks.GetAsync(key, ct).ConfigureAwait(false);
            if (record is null) return new LiveCheck(last, Gone: true, Terminal: false, Stranded: false);
            var state = record.Task.Status.State;
            if (state.IsTerminal()) return new LiveCheck(last, Gone: false, Terminal: true, Stranded: false);
            if (state.IsInterrupted()) return new LiveCheck(last, Gone: false, Terminal: false, Stranded: false);
            // The wait it returns for a running task is not used: end it, so its timer does not linger.
            using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var running = await _engine.RunningWaitsAsync(key, done.Token).ConfigureAwait(false);
            done.Cancel();
            return new LiveCheck(last, Gone: false, Terminal: false, Stranded: running is null);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            ServerLog.StreamCheckFailed(_engine.Logger, ex, key.TaskId);
            return new LiveCheck(0, Gone: false, Terminal: false, Stranded: false);
        }
    }

    /// <summary>A task with its last and oldest retained event sequences, read together.</summary>
    private readonly record struct StreamStart(AgentTask Task, long Last, long? Oldest);

    /// <summary>
    /// Reads the task and its sequences under the task lock, so the snapshot and the sequences belong together on this
    /// instance. Another instance can still commit meanwhile: the sequence is read as <see cref="TaskExecutionEngine.ReadTaskAtSequenceAsync"/> does.
    /// </summary>
    private async Task<StreamStart> ReadStartAsync(TaskKey key, CancellationToken ct)
    {
        using (await _engine.LockAsync(key, ct).ConfigureAwait(false))
        {
            var (record, last) = await _engine.ReadTaskAtSequenceAsync(key, ct).ConfigureAwait(false);
            if (record is null) throw A2AException.TaskNotFound(key.TaskId);
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
        await foreach (var stored in _events.ReadRangeAsync(key, after, before, ct).ConfigureAwait(false))
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
    /// authorized again, as <paramref name="operation"/> with <paramref name="request"/> (what the stream was opened with); on
    /// denial nothing more is sent and the stream ends. At <see cref="A2ALimits.MaxStreamDuration"/> the
    /// stream ends normally, without an error; clients resume with <c>Last-Event-ID</c>. Stopping the stream never stops the task.
    /// </summary>
    private async IAsyncEnumerable<SequencedEvent> LimitStreamAsync(IAsyncEnumerable<SequencedEvent> source, ServerCallContext call,
        A2AOperationDescriptor operation, object request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var limits = _options.Limits;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (TimeLimits.IsTimeLimit(limits.MaxStreamDuration)) stop.CancelAfter(limits.MaxStreamDuration);
        TimeSpan? reauthEvery = TimeLimits.IsTimeLimit(limits.StreamReauthInterval) ? limits.StreamReauthInterval : null;
        static long Ticks(TimeSpan t) => (long)(t.TotalSeconds * Stopwatch.Frequency);

        var events = source.GetAsyncEnumerator(stop.Token);
        Task<bool>? move = null;
        Task? deadline = null; // one timer for the re-check, kept across events until it fires
        var reauthAt = reauthEvery is { } first ? Stopwatch.GetTimestamp() + Ticks(first) : long.MaxValue;
        try
        {
            while (true)
            {
                bool more;
                try
                {
                    var next = events.MoveNextAsync();
                    if (reauthEvery is null || next.IsCompleted) more = await next.ConfigureAwait(false); // no timer, no Task
                    else
                    {
                        // Idle stream: wake up at the re-check deadline while waiting for the next event.
                        move = next.AsTask();
                        while (!move.IsCompleted)
                        {
                            var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), reauthAt);
                            if (wait > TimeSpan.Zero)
                            {
                                deadline ??= Task.Delay(wait, stop.Token);
                                if (await Task.WhenAny(move, deadline).ConfigureAwait(false) == move || stop.IsCancellationRequested) break;
                                deadline = null;
                                if (Stopwatch.GetTimestamp() < reauthAt) continue; // moved by a check between events: wait again
                            }
                            if (!await StillAuthorizedAsync().ConfigureAwait(false)) yield break; // the finally stops the source
                        }
                        more = await move.ConfigureAwait(false);
                        move = null;
                    }
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
            var allowed = await _authorization.AuthorizeAsync(call, operation, request, cancellationToken).ConfigureAwait(false)
                && await _skills.StillAllowedAsync(operation, call, cancellationToken).ConfigureAwait(false);
            // The stream just ends (the caller sees no error), so the audit trail is where the revoked access shows.
            if (!allowed)
            {
                SecurityEventLog.Record(_options, A2ATelemetry.SecurityEventNames.PermissionDenied, operation.Name, call, AuthorizationFilter.TaskIdOf(request));
                await OperationAuthorization.DeniedAsync(_audit, call, operation, AuthorizationFilter.TaskIdOf(request), "PermissionDenied").ConfigureAwait(false);
            }
            reauthAt = Stopwatch.GetTimestamp() + Ticks(reauthEvery!.Value);
            return allowed;
        }
    }

    private static TaskKey Key(ServerCallContext call, string taskId) => new(call.Tenant, call.Principal, taskId);

    private static string ScopeKey(string tenant, string taskId) => TaskExecutionEngine.ScopeKey(tenant, taskId);

    private AgentTask Shape(AgentTask task) => TaskShaper.Shape(task, null, _options.Limits);

    private Task AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, string? taskId, CancellationToken ct) =>
        _authorization.RequireAsync(_options, call, operation, request, taskId, _audit, ct);
}
