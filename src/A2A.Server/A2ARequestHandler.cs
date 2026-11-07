using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using A2A.Diagnostics;
using A2A.Json;
using A2A.Server.Idempotency;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using A2A.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace A2A.Server;

/// <summary>
/// Transport-agnostic implementation of every A2A operation. Protocol bindings (JSON-RPC, REST) parse the wire format,
/// build a <see cref="ServerCallContext"/>, and call these methods. Errors are thrown as <see cref="A2AException"/>.
/// </summary>
public sealed class A2ARequestHandler : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TaskStore _tasks;
    private readonly EventStore _events;
    private readonly IdempotencyStore _idempotency;
    private readonly PushConfigStore _pushConfigs;
    private readonly A2AAuthorizationHandler _authorization;
    private readonly IPushSecretProtector _protector;
    private readonly PushOutbox _outbox;
    private readonly TaskEventBus _bus;
    private readonly A2AServerOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly RunningRegistry<Execution> _running = new();
    private readonly StreamAdmission _admission;
    private readonly TimeProvider _time;

    /// <summary>
    /// Created by DI. The optional <c>timeProvider</c> is the clock for <see cref="A2AServerOptions.MaxTaskDuration"/>
    /// (default <see cref="TimeProvider.System"/>).
    /// </summary>
    public A2ARequestHandler(
        IServiceScopeFactory scopeFactory,
        TaskStore tasks,
        EventStore events,
        IdempotencyStore idempotency,
        PushConfigStore pushConfigs,
        A2AAuthorizationHandler authorization,
        IPushSecretProtector protector,
        PushOutbox outbox,
        TaskEventBus bus,
        IOptions<A2AServerOptions> options,
        ILogger<A2ARequestHandler>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _tasks = tasks;
        _events = events;
        _idempotency = idempotency;
        _pushConfigs = pushConfigs;
        _authorization = authorization;
        _protector = protector;
        _outbox = outbox;
        _bus = bus;
        _options = options.Value;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _admission = new StreamAdmission(_options.Limits);
        _time = timeProvider ?? TimeProvider.System;
    }

    public AgentCard AgentCard => _options.AgentCard;

    // =====================================================================
    // Call validation: version, extensions, authentication
    // =====================================================================

    /// <summary>Checks protocol version, required extensions, and authentication; fills <see cref="ServerCallContext.ActivatedExtensions"/>.</summary>
    public void ValidateCall(ServerCallContext call)
    {
        var version = string.IsNullOrWhiteSpace(call.ProtocolVersion)
            ? (_options.TreatMissingVersionAsCurrent ? A2AProtocol.Version : A2AProtocol.ImplicitVersion)
            : MajorMinor(call.ProtocolVersion!);
        if (!_options.SupportedVersions.Contains(version))
        {
            throw new A2AException(A2AErrorCode.VersionNotSupported,
                $"Protocol version '{version}' is not supported. Supported: {string.Join(", ", _options.SupportedVersions)}. Send the {A2AProtocol.Headers.Version} header.")
            {
                Metadata = new() { ["requestedVersion"] = version, ["supportedVersions"] = string.Join(",", _options.SupportedVersions) },
            };
        }

        // Backstop for hosts where the startup check could not run (or direct use of this class): anonymous is opt-in.
        if (!_options.AllowAnonymous && !call.IsAuthenticated)
            throw new A2AException(A2AErrorCode.Unauthenticated);

        var declared = _options.AgentCard.Capabilities.Extensions ?? [];
        foreach (var ext in declared.Where(e => e.Required))
        {
            if (!call.RequestedExtensions.Contains(ext.Uri, StringComparer.Ordinal))
                throw new A2AException(A2AErrorCode.ExtensionSupportRequired, $"Extension '{ext.Uri}' is required.")
                {
                    Metadata = new() { ["extension"] = ext.Uri },
                };
        }
        foreach (var uri in call.RequestedExtensions)
        {
            if (declared.Any(e => e.Uri == uri)) call.ActivatedExtensions.Add(uri);
        }
    }

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

    private static string MajorMinor(string v)
    {
        var parts = v.Trim().Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : parts[0];
    }

    // =====================================================================
    // SendMessage / SendStreamingMessage
    // =====================================================================

    /// <summary>
    /// Sends a message. Unless <see cref="SendMessageConfiguration.ReturnImmediately"/> is set, waits until the task is
    /// terminal or interrupted, by an event of this execution or of any other execution of the task (for example CancelTask),
    /// or until no execution of the task is running any more.
    /// </summary>
    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, ServerCallContext call, CancellationToken cancellationToken = default) =>
        ServerTelemetry.TraceAsync(A2AProtocol.Methods.SendMessage, call, request.Message?.TaskId, () => SendMessageCoreAsync(request, call, cancellationToken), cancellationToken);

    private async Task<SendMessageResponse> SendMessageCoreAsync(SendMessageRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.SendMessage, request, request.Message?.TaskId, cancellationToken).ConfigureAwait(false);
        var (claim, replay) = await BeginIdempotentAsync(request, call, cancellationToken).ConfigureAwait(false);
        if (replay is not null) return await ReplayAsync(replay, request, call, cancellationToken).ConfigureAwait(false);
        var taskId = string.IsNullOrEmpty(request.Message?.TaskId) ? TaskIds.New() : request.Message.TaskId;
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
                    var running = RunningCompletions(scope);
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

        var record = await _tasks.GetAsync(Key(call, exec.TaskId), cancellationToken).ConfigureAwait(false)
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
        ServerTelemetry.TraceStream(A2AProtocol.Methods.SendStreamingMessage, call, request.Message?.TaskId,
            LimitStreamAsync(SendStreamingCoreAsync(request, call), call, A2AOperation.SendStreamingMessage, request, cancellationToken));

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
        var (claim, replay) = await BeginIdempotentAsync(request, call, cancellationToken).ConfigureAwait(false);
        if (replay is IdempotencyOutcome.DirectMessage direct)
        {
            yield return new SequencedEvent(0, StreamResponse.Of(A2AJson.Clone(direct.Message)));
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
            await AbandonAsync(claim).ConfigureAwait(false);
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
                    var running = RunningCompletions(scope);
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

    /// <summary>Completions of the executions running for a task (in-memory scope key).</summary>
    private List<Task> RunningCompletions(string scope) =>
        _running.Snapshot(scope).Select(e => e.Completion).ToList();

    /// <param name="Context">The request context for the agent.</param>
    /// <param name="PushConfig">The push config to save when a new task is created.</param>
    /// <param name="LastSequence">For a follow-up, the task's last event sequence right after the message was appended; 0 for a new task.</param>
    private sealed record Prepared(AgentRequestContext Context, TaskPushNotificationConfig? PushConfig, long LastSequence);

    /// <summary>
    /// Begins idempotency for a SendMessage (spec design §7.3), after authorization and before the message is accepted. The
    /// key is the <c>Idempotency-Key</c> header, else the message id, scoped by tenant and principal. Returns the claim of a
    /// new request, or the outcome of the earlier request with the same key, waiting (up to the request's cancellation) while
    /// it runs. A key reused with a different request is <see cref="A2AException.IdempotencyKeyMismatch"/>; a key longer than
    /// <see cref="MaxIdempotencyKeyLength"/> characters is InvalidParams.
    /// </summary>
    private async Task<(IdempotencyClaim Claim, IdempotencyOutcome? Replay)> BeginIdempotentAsync(SendMessageRequest request,
        ServerCallContext call, CancellationToken ct)
    {
        var key = call.IdempotencyKey ?? request.Message?.MessageId;
        if (string.IsNullOrEmpty(key) || request.Message is null) return (IdempotencyClaim.None, null); // validation rejects it later
        if (key.Length > MaxIdempotencyKeyLength) throw A2AException.InvalidParams("Idempotency key too long.");
        var scope = new IdempotencyScope(call.Tenant, call.Principal, key);
        var hash = RequestHasher.Hash(request);
        while (true)
        {
            switch (await _idempotency.TryBeginAsync(scope, hash, ct).ConfigureAwait(false))
            {
                case IdempotencyResult.New:
                    return (new IdempotencyClaim(_idempotency, scope), null);
                case IdempotencyResult.Completed done:
                    return (IdempotencyClaim.None, done.Outcome);
                case IdempotencyResult.InProgress running:
                    try
                    {
                        return (IdempotencyClaim.None, await running.Completion.WaitAsync(ct).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        break; // the first request failed before creating anything: try again, possibly as the new owner
                    }
                case IdempotencyResult.Mismatch:
                    throw A2AException.IdempotencyKeyMismatch();
                default:
                    throw new UnreachableException();
            }
        }
    }

    /// <summary>
    /// The answer to a retried SendMessage: the same direct reply, or the task. Like the first request, a blocking retry
    /// returns the task once it is terminal or interrupted (or the request is cancelled); a <c>returnImmediately</c> retry
    /// returns it as it is now.
    /// </summary>
    private async Task<SendMessageResponse> ReplayAsync(IdempotencyOutcome outcome, SendMessageRequest request, ServerCallContext call, CancellationToken ct)
    {
        switch (outcome)
        {
            case IdempotencyOutcome.DirectMessage direct:
                return new MessageResponse(A2AJson.Clone(direct.Message));
            case IdempotencyOutcome.TaskCreated created:
                var key = Key(call, created.TaskId);
                var task = request.Configuration?.ReturnImmediately == true
                    ? (await _tasks.GetAsync(key, ct).ConfigureAwait(false))?.Task ?? throw A2AException.TaskNotFound(created.TaskId)
                    : await WaitUntilSettledAsync(key, created.AfterSequence, ct).ConfigureAwait(false);
                return new TaskResponse(Shape(task, request.Configuration?.HistoryLength));
            default:
                throw new UnreachableException();
        }
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
    /// </summary>
    private async Task<AgentTask> WaitUntilSettledAsync(TaskKey key, long afterSequence, CancellationToken ct)
    {
        var scope = ScopeKey(key.Tenant, key.TaskId);
        using var sub = _bus.Subscribe(scope);
        var statusAfter = afterSequence == 0 || await HasStatusEventAfterAsync(key, afterSequence, ct).ConfigureAwait(false);
        Task<bool>? live = null; // one pending wait at a time: the subscription has a single reader
        while (true)
        {
            var running = RunningCompletions(scope);
            var record = await _tasks.GetAsync(key, ct).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(key.TaskId);
            var state = record.Task.Status.State;
            if (state.IsTerminal() || (state.IsInterrupted() && statusAfter) || running.Count == 0) return record.Task;

            live ??= sub.Reader.WaitToReadAsync(ct).AsTask();
            await Task.WhenAny(running.Append(live)).ConfigureAwait(false);
            if (!live.IsCompleted) continue; // an execution ended: check again
            if (!await live.ConfigureAwait(false))
            {
                // The bus ended the topic (the task finished): one last read.
                return (await _tasks.GetAsync(key, ct).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(key.TaskId)).Task;
            }
            live = null;
            while (sub.Reader.TryRead(out var evt))
                if (evt.Sequence > afterSequence && evt.Event is StatusUpdateEvent) statusAfter = true;
        }
    }

    /// <summary>True when the event store holds a status event of the task with a sequence after <paramref name="afterSequence"/>.</summary>
    private async Task<bool> HasStatusEventAfterAsync(TaskKey key, long afterSequence, CancellationToken ct)
    {
        await foreach (var stored in _events.ReadAfterAsync(key, afterSequence, ct).ConfigureAwait(false))
            if (stored.Event is StatusUpdateEvent) return true;
        return false;
    }

    /// <summary>
    /// Accepts the message and starts the agent. The idempotency claim is abandoned when the message is refused or the agent
    /// cannot start; a follow-up records its task at once (the message is already in the task's history), and a new task
    /// records it when it is first stored.
    /// </summary>
    /// <returns>The execution, and the task's last sequence before it (0 for a new task; see <see cref="Prepared.LastSequence"/>).</returns>
    private async Task<(Execution Execution, long AfterSequence)> AcceptAsync(SendMessageRequest request, ServerCallContext call, IdempotencyClaim claim, string? newTaskId, CancellationToken ct)
    {
        try
        {
            var prepared = await PrepareAsync(request, call, ct, newTaskId).ConfigureAwait(false);
            if (prepared.Context.IsNewTask) return (StartExecution(prepared.Context, prepared.PushConfig, claim), 0);

            // Record after the execution is registered, so a blocking retry sees it running and waits for its outcome.
            var exec = StartExecution(prepared.Context, prepared.PushConfig, IdempotencyClaim.None);
            var taskId = prepared.Context.TaskId;
            await RecordAsync(claim, new IdempotencyOutcome.TaskCreated(taskId, prepared.LastSequence), taskId).ConfigureAwait(false);
            return (exec, prepared.LastSequence);
        }
        catch
        {
            await AbandonAsync(claim).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Abandons an idempotency claim on a failure path. A failing idempotency store is logged; the original error wins.</summary>
    private async Task AbandonAsync(IdempotencyClaim claim)
    {
        try { await claim.AbandonAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to abandon an idempotency entry"); }
    }

    private async Task<Prepared> PrepareAsync(SendMessageRequest request, ServerCallContext call, CancellationToken ct, string? newTaskId = null)
    {
        call.BindTenant(request.Tenant);
        A2AValidator.ValidateIncoming(request);
        RequestLimitValidator.Validate(request.Message, _options.Limits);
        // A copy: the caller's message is never changed (a retry with the same instance must hash the same), and the
        // stored history does not alias it.
        var message = A2AJson.Clone(request.Message);
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
            await PushUrlValidator.ValidateAsync(p.Url, _options.PushNotifications, ct).ConfigureAwait(false);
            push = A2AJson.Clone(p);
            push.TaskId = taskId;
            push.Id = string.IsNullOrEmpty(push.Id) ? TaskIds.New() : push.Id;
            push.Tenant = call.Tenant;
        }

        if (isFollowUp)
        {
            var gate = LockFor(call.Tenant, taskId);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            var pushIsNew = false;   // the config id did not exist before this request
            var pushSaved = false;
            var committed = false;
            try
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
                    EnsureTurnCapacity(record.Task);
                    if (push is not null)
                    {
                        // Saved atomically under the limit before the message is committed, so a refused request leaves no message.
                        if (!pushSaved) pushIsNew = await _pushConfigs.GetAsync(Key(call, taskId), push.Id!, ct).ConfigureAwait(false) is null;
                        if (!await SavePushConfigIfUnderLimitAsync(Key(call, taskId), push, ct).ConfigureAwait(false))
                            throw TooManyPushConfigs(_options.Limits.MaxPushConfigsPerTask);
                        pushSaved = true;
                    }
                    contextId = record.Task.ContextId;
                    message.ContextId = contextId;
                    (record.Task.History ??= []).Add(message);
                    try
                    {
                        await _tasks.UpdateAsync(record, record.Version, ct).ConfigureAwait(false);
                        existing = record.Task;
                        committed = true;
                        // Still under the task lock. The message is committed: a cancelled request must not stop here, or
                        // the idempotency entry would be abandoned and a retry would append the message again.
                        lastSequence = await _events.LastSequenceAsync(Key(call, taskId), CancellationToken.None).ConfigureAwait(false);
                        break;
                    }
                    catch (ConcurrencyException) when (attempt < MaxConflictRetries)
                    {
                        // The task changed under us: reload, re-check the state, and append again.
                    }
                }
            }
            finally
            {
                // The message was not committed (state refused, conflicts, cancel): do not keep the config it came with.
                if (pushSaved && pushIsNew && !committed) await TryDeletePushConfigAsync(Key(call, taskId), push!.Id!).ConfigureAwait(false);
                gate.Release();
            }
        }
        else
        {
            contextId = await ResolveContextAsync(message.ContextId, call, ct).ConfigureAwait(false);
            message.ContextId = contextId;
        }

        // A new task saves its push config when the task is created; a follow-up saved it above, before its message was committed.

        var context = new AgentRequestContext
        {
            Message = message,
            TaskId = taskId,
            ContextId = contextId,
            Task = existing,
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

    /// <summary>
    /// Throws UnsupportedOperation when one more message would exceed <see cref="A2AServerOptions.MaxTurnsPerTask"/>.
    /// Turns are the user messages in the stored history (the first message and every accepted follow-up).
    /// </summary>
    private void EnsureTurnCapacity(AgentTask task)
    {
        if (_options.MaxTurnsPerTask is not { } max) return;
        var turns = task.History?.Count(m => m.Role == Role.User) ?? 0;
        if (turns < max) return;
        throw new A2AException(A2AErrorCode.UnsupportedOperation,
            $"The task reached its limit of {max} messages. Start a new task and list this one in referenceTaskIds.")
        {
            Metadata = new() { ["taskId"] = task.Id, ["maxTurnsPerTask"] = max.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        };
    }

    // =====================================================================
    // Tasks
    // =====================================================================

    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, ServerCallContext call, CancellationToken cancellationToken = default) =>
        ServerTelemetry.TraceAsync(A2AProtocol.Methods.GetTask, call, request.Id, () => GetTaskCoreAsync(request, call, cancellationToken), cancellationToken);

    private async Task<AgentTask> GetTaskCoreAsync(GetTaskRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.GetTask, request, request.Id, cancellationToken).ConfigureAwait(false);
        A2AValidator.RequireId(request.Id, "id");
        if (request.HistoryLength is < 0) throw A2AException.InvalidParams("historyLength must be >= 0");
        var record = await _tasks.GetAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        return Shape(record.Task, request.HistoryLength);
    }

    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, ServerCallContext call, CancellationToken cancellationToken = default) =>
        ServerTelemetry.TraceAsync(A2AProtocol.Methods.ListTasks, call, null, () => ListTasksCoreAsync(request, call, cancellationToken), cancellationToken);

    private async Task<ListTasksResponse> ListTasksCoreAsync(ListTasksRequest request, ServerCallContext call, CancellationToken cancellationToken)
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

    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, ServerCallContext call, CancellationToken cancellationToken = default) =>
        ServerTelemetry.TraceAsync(A2AProtocol.Methods.CancelTask, call, request.Id, () => CancelTaskCoreAsync(request, call, cancellationToken), cancellationToken);

    private async Task<AgentTask> CancelTaskCoreAsync(CancelTaskRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.CancelTask, request, request.Id, cancellationToken).ConfigureAwait(false);
        A2AValidator.RequireId(request.Id, "id");
        var record = await _tasks.GetAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        if (record.Task.Status.State.IsTerminal()) throw NotCancelable(request.Id);

        var running = _running.Snapshot(ScopeKey(call.Tenant, request.Id));
        foreach (var r in running) r.Cancel();

        var snapshot = record.Task;
        var exec = StartRaw(snapshot.Id, snapshot.ContextId, isNewTask: false, userMessage: null, call, push: null, emitInitialSnapshot: false,
            (executor, updater, ct) => executor.CancelAsync(snapshot, updater, ct), requestMetadata: request.Metadata, kind: "cancel");
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
        ServerTelemetry.TraceStream(A2AProtocol.Methods.SubscribeToTask, call, request.Id,
            LimitStreamAsync(SubscribeCoreAsync(request, call, resumeAfter, onCaughtUp), call, A2AOperation.SubscribeToTask, request, cancellationToken));

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
        var gate = LockFor(key.Tenant, key.TaskId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var record = await _tasks.GetAsync(key, ct).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(key.TaskId);
            var last = await _events.LastSequenceAsync(key, ct).ConfigureAwait(false);
            var oldest = await _events.OldestRetainedAsync(key, ct).ConfigureAwait(false);
            return new StreamStart(record.Task, last, oldest);
        }
        finally
        {
            gate.Release();
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

    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, ServerCallContext call, CancellationToken cancellationToken = default) =>
        ServerTelemetry.TraceAsync(A2AProtocol.Methods.CreateTaskPushNotificationConfig, call, config.TaskId, () => CreateTaskPushNotificationConfigCoreAsync(config, call, cancellationToken), cancellationToken);

    private async Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigCoreAsync(TaskPushNotificationConfig config, ServerCallContext call, CancellationToken cancellationToken)
    {
        call.BindTenant(config.Tenant);
        await AuthorizeAsync(call, A2AOperation.CreateTaskPushNotificationConfig, config, config.TaskId, cancellationToken).ConfigureAwait(false);
        RequirePush();
        A2AValidator.ValidatePushConfig(config, "config", requireTaskId: true);
        await PushUrlValidator.ValidateAsync(config.Url, _options.PushNotifications, cancellationToken).ConfigureAwait(false);
        var key = Key(call, config.TaskId!);
        _ = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(config.TaskId!);
        var stored = A2AJson.Clone(config);
        stored.Id = string.IsNullOrEmpty(stored.Id) ? TaskIds.New() : stored.Id;
        stored.Tenant = call.Tenant;
        if (!await SavePushConfigIfUnderLimitAsync(key, stored, cancellationToken).ConfigureAwait(false))
            throw TooManyPushConfigs(_options.Limits.MaxPushConfigsPerTask);
        return Redact(stored);
    }

    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken = default) =>
        ServerTelemetry.TraceAsync(A2AProtocol.Methods.GetTaskPushNotificationConfig, call, request.TaskId, () => GetTaskPushNotificationConfigCoreAsync(request, call, cancellationToken), cancellationToken);

    private async Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigCoreAsync(GetTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.GetTaskPushNotificationConfig, request, request.TaskId, cancellationToken).ConfigureAwait(false);
        RequirePush();
        A2AValidator.RequireId(request.TaskId, "taskId");
        A2AValidator.RequireId(request.Id, "id");
        var key = Key(call, request.TaskId);
        _ = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.TaskId);
        var record = await _pushConfigs.GetAsync(key, request.Id, cancellationToken).ConfigureAwait(false)
            ?? throw ConfigNotFound(request.TaskId, request.Id);
        return Redact(record.Config);
    }

    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, ServerCallContext call, CancellationToken cancellationToken = default) =>
        ServerTelemetry.TraceAsync(A2AProtocol.Methods.ListTaskPushNotificationConfigs, call, request.TaskId, () => ListTaskPushNotificationConfigsCoreAsync(request, call, cancellationToken), cancellationToken);

    private async Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsCoreAsync(ListTaskPushNotificationConfigsRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.ListTaskPushNotificationConfigs, request, request.TaskId, cancellationToken).ConfigureAwait(false);
        RequirePush();
        A2AValidator.RequireId(request.TaskId, "taskId");
        var key = Key(call, request.TaskId);
        _ = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.TaskId);
        var all = (await _pushConfigs.ListAsync(key, cancellationToken).ConfigureAwait(false)).Select(r => Redact(r.Config)).ToList();

        var size = request.PageSize is > 0 ? Math.Min(request.PageSize.Value, 100) : 100;
        IEnumerable<TaskPushNotificationConfig> rest = all;
        if (!string.IsNullOrEmpty(request.PageToken))
            rest = all.SkipWhile(c => string.CompareOrdinal(c.Id, request.PageToken) <= 0);
        var page = rest.Take(size + 1).ToList();
        string? next = null;
        if (page.Count > size)
        {
            page.RemoveAt(page.Count - 1);
            next = page[^1].Id;
        }
        return new ListTaskPushNotificationConfigsResponse { Configs = page, NextPageToken = next };
    }

    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken = default) =>
        ServerTelemetry.TraceAsync(A2AProtocol.Methods.DeleteTaskPushNotificationConfig, call, request.TaskId, () => DeleteTaskPushNotificationConfigCoreAsync(request, call, cancellationToken), cancellationToken);

    private async Task DeleteTaskPushNotificationConfigCoreAsync(DeleteTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.DeleteTaskPushNotificationConfig, request, request.TaskId, cancellationToken).ConfigureAwait(false);
        RequirePush();
        A2AValidator.RequireId(request.TaskId, "taskId");
        A2AValidator.RequireId(request.Id, "id");
        var key = Key(call, request.TaskId);
        _ = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.TaskId);
        if (!await _pushConfigs.DeleteAsync(key, request.Id, cancellationToken).ConfigureAwait(false))
            throw ConfigNotFound(request.TaskId, request.Id);
    }

    // =====================================================================
    // Extended Agent Card
    // =====================================================================

    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, ServerCallContext call, CancellationToken cancellationToken = default) =>
        ServerTelemetry.TraceAsync(A2AProtocol.Methods.GetExtendedAgentCard, call, null, () => GetExtendedAgentCardCoreAsync(request, call, cancellationToken), cancellationToken);

    private async Task<AgentCard> GetExtendedAgentCardCoreAsync(GetExtendedAgentCardRequest request, ServerCallContext call, CancellationToken cancellationToken)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.GetExtendedAgentCard, request, null, cancellationToken).ConfigureAwait(false);
        if (_options.AgentCard.Capabilities.ExtendedAgentCard != true)
            throw new A2AException(A2AErrorCode.UnsupportedOperation, "This agent does not provide an extended agent card.");
        if (!call.IsAuthenticated) throw new A2AException(A2AErrorCode.Unauthenticated, "The extended agent card requires authentication.");
        var card = _options.ExtendedAgentCardProvider is null ? null : await _options.ExtendedAgentCardProvider(call, cancellationToken).ConfigureAwait(false);
        return card ?? throw new A2AException(A2AErrorCode.ExtendedAgentCardNotConfigured);
    }

    // =====================================================================
    // Execution engine
    // =====================================================================

    private Execution StartExecution(AgentRequestContext context, TaskPushNotificationConfig? push, IdempotencyClaim claim) =>
        StartRaw(context.TaskId, context.ContextId, context.IsNewTask, context.Message, context.Call, push, emitInitialSnapshot: !context.IsNewTask,
            (executor, updater, ct) => executor.ExecuteAsync(context, updater, ct), claim, context.Metadata, _options.MaxTaskDuration);

    /// <summary>
    /// Runs the agent for a task. An idempotency <c>claim</c> is completed when the task is first stored or the agent replies
    /// directly, and abandoned when neither happens. With a <paramref name="timeLimit"/>, an execution still running when it
    /// expires fails the task (<see cref="TimeLimitText"/>) and is cancelled.
    /// </summary>
    private Execution StartRaw(string taskId, string contextId, bool isNewTask, Message? userMessage, ServerCallContext call,
        TaskPushNotificationConfig? push, bool emitInitialSnapshot, Func<IAgentExecutor, TaskUpdater, CancellationToken, Task> run,
        IdempotencyClaim? claim = null, Dictionary<string, JsonElement>? requestMetadata = null, TimeSpan? timeLimit = null, string kind = "execute")
    {
        var exec = new Execution(taskId, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
        {
            // A child of the operation's span; ends when the agent has returned and every event is stored.
            Activity = ServerTelemetry.StartExecution(kind, call, taskId, contextId),
        };
        var scope = ScopeKey(call.Tenant, taskId);
        _running.Add(scope, exec);
        var updater = new TaskUpdater(taskId, contextId, isNewTask, exec.Incoming.Writer, requestMetadata);
        // Started before the agent, so the time spent waiting for a thread counts too. Disposed when the agent returns.
        var timer = timeLimit is { } limit && IsTimeLimit(limit)
            ? _time.CreateTimer(_ => _ = TimeOutAsync(exec, updater, taskId), null, limit, Timeout.InfiniteTimeSpan)
            : null;

        var producer = Task.Run(async () =>
        {
            if (exec.Activity is not null) Activity.Current = exec.Activity; // the agent's own spans nest under the run
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var executor = scope.ServiceProvider.GetRequiredService<IAgentExecutor>();
                await run(executor, updater, exec.Cts.Token).ConfigureAwait(false);
                exec.TryMarkReturned();
            }
            catch (Exception) when (!exec.TryMarkReturned())
            {
                // The task already failed with the time limit text; the agent's late error or cancellation is expected.
            }
            catch (OperationCanceledException) when (exec.Cts.IsCancellationRequested)
            {
                exec.Activity?.SetTag(A2ATelemetry.ErrorTypeCanceledTag, true);
                await TryFinishAsync(updater, TaskState.Canceled, null).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                A2ATelemetry.RecordError(exec.Activity, ex);
                _logger.LogError(ex, "Agent executor failed for task {TaskId}", taskId);
                await TryFinishAsync(updater, TaskState.Failed, "The agent failed to process the request.").ConfigureAwait(false);
            }
            finally
            {
                if (timer is not null) await timer.DisposeAsync().ConfigureAwait(false);
                exec.Incoming.Writer.TryComplete();
            }
        });

        // Background writes outlive the request, so fix the owner scope now.
        var key = Key(call, taskId);
        var consumer = Task.Run(() =>
        {
            if (exec.Activity is not null) Activity.Current = exec.Activity; // webhook deliveries link to the run
            return ConsumeAsync(exec, key, contextId, isNewTask, userMessage, push, emitInitialSnapshot, claim ?? IdempotencyClaim.None);
        });
        _ = Task.WhenAll(producer, consumer).ContinueWith(_t =>
        {
            _running.Remove(scope, exec);
            exec.EndActivity();
            exec.MarkDone();
        }, TaskScheduler.Default);
        return exec;
    }

    /// <summary>
    /// The execution ran past <see cref="A2AServerOptions.MaxTaskDuration"/>: fails the task (the FAILED event is queued
    /// before the token fires, so it wins over a CANCELED from the agent's cancellation), then cancels the agent.
    /// </summary>
    private async Task TimeOutAsync(Execution exec, TaskUpdater updater, string taskId)
    {
        if (!exec.TryMarkTimedOut()) return; // the agent returned first
        if (exec.Activity is { } activity)
        {
            activity.SetTag(A2ATelemetry.Tags.TimedOut, true);
            activity.SetTag(A2ATelemetry.Tags.ErrorType, "TimeLimit");
            activity.SetStatus(ActivityStatusCode.Error, "TimeLimit");
        }
        _logger.LogWarning("Task {TaskId}: the agent ran longer than {Limit}; failing the task", taskId, _options.MaxTaskDuration);
        await TryFinishAsync(updater, TaskState.Failed, TimeLimitText).ConfigureAwait(false);
        exec.Cancel();
    }

    private static async Task TryFinishAsync(TaskUpdater updater, TaskState state, string? text)
    {
        if (updater.IsTerminal || updater.HasReplied) return;
        try { await updater.UpdateStatusAsync(state, text is null ? null : updater.NewAgentMessage(text)).ConfigureAwait(false); }
        catch (Exception) { /* channel closed or terminal already */ }
    }

    private async Task ConsumeAsync(Execution exec, TaskKey key, string contextId, bool isNewTask, Message? userMessage,
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
                var snapshotGate = LockFor(key.Tenant, taskId);
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
                var evt = produced;
                if (A2AJson.SerializeToUtf8Bytes(evt).Length > _options.Limits.MaxEventBytes)
                {
                    // The event is never stored or sent: the task fails instead and the agent is stopped.
                    _logger.LogWarning("Task {TaskId}: the agent produced an event larger than {Limit} bytes; failing the task", taskId, _options.Limits.MaxEventBytes);
                    evt = FailureEvent(taskId, contextId);
                    exec.Cancel();
                }

                if (evt is MessageEvent reply)
                {
                    await RecordAsync(claim, new IdempotencyOutcome.DirectMessage(A2AJson.Clone(reply.Message)), taskId).ConfigureAwait(false);
                    exec.Emit(new SequencedEvent(0, evt)); // direct reply, no task, not stored
                    continue;
                }

                var gate = LockFor(key.Tenant, taskId);
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
                                History = userMessage is null ? null : [WithTask(userMessage, taskId)],
                            },
                            Version: 0, CreatedAt: now, UpdatedAt: now);
                        // The task, its first event (the snapshot) and the snapshot's push delivery are stored together. The
                        // push config is saved first, so the delivery service always finds the config of a queued delivery.
                        var snapshot = StreamResponse.Of(A2AJson.Clone(initial.Task));
                        IReadOnlyList<PushDelivery> deliveries = push is null ? [] : [NewDelivery(key, push.Id!)];
                        if (push is not null) await SavePushConfigAsync(key, push, CancellationToken.None).ConfigureAwait(false);
                        CommitResult committed;
                        try
                        {
                            committed = await _tasks.CommitAsync(new TaskCommit(initial, 0, snapshot, deliveries)).ConfigureAwait(false);
                        }
                        catch when (push is not null)
                        {
                            await TryDeletePushConfigAsync(key, push.Id!).ConfigureAwait(false); // no task, so no config
                            throw;
                        }
                        if (deliveries.Count > 0) _outbox.NotifyPending();
                        record = committed.Record;
                        created = true;
                        Publish(exec, key, new SequencedEvent(committed.Sequence, snapshot));
                        // Retries of this request now attach to the task (its first event is stored, so a replay starts complete).
                        await RecordAsync(claim, new IdempotencyOutcome.TaskCreated(taskId), taskId).ConfigureAwait(false);
                    }
                    else
                    {
                        record = await _tasks.GetAsync(key).ConfigureAwait(false);
                        if (record is null)
                        {
                            _logger.LogWarning("Task {TaskId} disappeared from the store; dropping event", taskId);
                            continue;
                        }
                    }

                    if (await ApplyAndStoreAsync(key, record, evt).ConfigureAwait(false) is not { } stored) continue;

                    // Still under the task lock, so sinks see events in sequence order even with concurrent executions.
                    // Every sink is non-blocking.
                    Publish(exec, key, stored);
                    if (stored.Event is StatusUpdateEvent { StatusUpdate.Status.State: var recorded }) exec.Activity?.SetTag(A2ATelemetry.Tags.TaskState, recorded.ToString());
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
            _logger.LogError(ex, "Failed to apply events for task {TaskId}", taskId);
            A2ATelemetry.RecordError(exec.Activity, ex);
            exec.Cancel();
            // Before the FAILED event is published: the waiting request must see the error, not that event.
            exec.Fail(new A2AException(A2AErrorCode.Internal));
            await TryFailStoredTaskAsync(key, contextId, scope).ConfigureAwait(false);
        }
        finally
        {
            // No task and no reply (for example the agent produced nothing): retries may run the request again.
            try { await claim.AbandonAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to abandon the idempotency entry for task {TaskId}", taskId); }
            // A storage failure (above) is an error for the request that is waiting, never a clean end of its stream.
            exec.Outgoing.Writer.TryComplete();
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
            var gate = LockFor(key.Tenant, key.TaskId);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var record = await _tasks.GetAsync(key).ConfigureAwait(false);
                if (record is null || record.Task.Status.State.IsTerminal()) return;
                var failed = FailureEvent(key.TaskId, contextId, StoreFailureText);
                if (await ApplyAndStoreAsync(key, record, failed).ConfigureAwait(false) is not { } stored) return;
                _bus.Publish(scope, stored);
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
            _logger.LogError(ex, "Could not mark task {TaskId} as failed after a storage error", key.TaskId);
        }
    }

    /// <summary>Records an idempotency outcome. A failing idempotency store is logged; it never stops the task.</summary>
    private async Task RecordAsync(IdempotencyClaim claim, IdempotencyOutcome outcome, string taskId)
    {
        try { await claim.CompleteAsync(outcome).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to record the idempotency outcome for task {TaskId}", taskId); }
    }

    /// <summary>
    /// Applies the event, then commits the task change and the event as one unit (<see cref="TaskStore.CommitAsync"/>), so
    /// the log holds only applied events, in the order their changes were stored, even across instances. After a version
    /// conflict, reloads and re-applies, up to <see cref="MaxConflictRetries"/> times. Returns null when the event was
    /// dropped (for example the task is already terminal).
    /// </summary>
    private async Task<SequencedEvent?> ApplyAndStoreAsync(TaskKey key, TaskRecord record, StreamResponse evt)
    {
        var deliveries = await DeliveriesAsync(key).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            if (!Apply(record.Task, evt))
            {
                _logger.LogDebug("Dropped event for task {TaskId} in state {State}", key.TaskId, record.Task.Status.State);
                return null;
            }
            try
            {
                var committed = await _tasks.CommitAsync(new TaskCommit(record, record.Version, evt, deliveries)).ConfigureAwait(false);
                if (deliveries.Count > 0) _outbox.NotifyPending();
                return new SequencedEvent(committed.Sequence, evt);
            }
            catch (ConcurrencyException) when (attempt < MaxConflictRetries)
            {
                var fresh = await _tasks.GetAsync(key).ConfigureAwait(false);
                if (fresh is null)
                {
                    _logger.LogWarning("Task {TaskId} disappeared from the store; dropping event", key.TaskId);
                    return null;
                }
                record = fresh;
            }
        }
    }

    /// <summary>Sends a stored event to the waiting request and to subscribers. Its webhooks were queued by its commit.</summary>
    private void Publish(Execution exec, TaskKey key, SequencedEvent evt)
    {
        exec.Emit(evt);
        _bus.Publish(ScopeKey(key.Tenant, key.TaskId), evt);
    }

    /// <summary>One push delivery per push config of the task, for the event about to be committed.</summary>
    private async ValueTask<IReadOnlyList<PushDelivery>> DeliveriesAsync(TaskKey key)
    {
        if (_options.AgentCard.Capabilities.PushNotifications != true) return []; // no config can exist
        var configs = await _pushConfigs.ListAsync(key).ConfigureAwait(false);
        return configs.Count == 0 ? [] : configs.Select(c => NewDelivery(key, c.Config.Id!)).ToList();
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

    /// <summary>Best effort: removes the push config saved for a task whose creation failed.</summary>
    private async Task TryDeletePushConfigAsync(TaskKey key, string configId)
    {
        try { await _pushConfigs.DeleteAsync(key, configId, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "Could not remove push config {ConfigId} of task {TaskId} that was not created", configId, key.TaskId); }
    }

    /// <summary>Applies an event to the task. Returns false if the event must be dropped (task already terminal).</summary>
    private static bool Apply(AgentTask task, StreamResponse evt)
    {
        if (task.Status.State.IsTerminal()) return false;
        if (evt is StatusUpdateEvent { StatusUpdate: var su })
        {
            if (!TaskStateMachine.CanTransition(task.Status.State, su.Status.State)) return false;
            su.Status.Timestamp ??= DateTimeOffset.UtcNow;
            task.Status = A2AJson.Clone(su.Status);
            if (su.Status.Message is { } m) (task.History ??= []).Add(A2AJson.Clone(m));
            return true;
        }
        if (evt is ArtifactUpdateEvent { ArtifactUpdate: var au })
        {
            A2AValidator.ValidateArtifact(au.Artifact, "artifact");
            var artifacts = task.Artifacts ??= [];
            var idx = artifacts.FindIndex(a => a.ArtifactId == au.Artifact.ArtifactId);
            var incoming = A2AJson.Clone(au.Artifact);
            if (idx < 0) artifacts.Add(incoming);
            else if (au.Append)
            {
                var existing = artifacts[idx];
                existing.Parts.AddRange(incoming.Parts);
                existing.Name ??= incoming.Name;
                existing.Description ??= incoming.Description;
                if (incoming.Metadata is not null)
                {
                    existing.Metadata ??= new();
                    foreach (var kv in incoming.Metadata) existing.Metadata[kv.Key] = kv.Value;
                }
            }
            else artifacts[idx] = incoming;
            return true;
        }
        return false;
    }

    /// <summary>Status text of a task failed because its updates could not be stored.</summary>
    internal const string StoreFailureText = "The task failed because its updates could not be stored.";

    /// <summary>Status text of a task failed because an execution ran longer than <see cref="A2AServerOptions.MaxTaskDuration"/>.</summary>
    internal const string TimeLimitText = "Task exceeded its time limit";

    /// <summary>Status text of a task failed because the agent produced an event larger than <see cref="A2ALimits.MaxEventBytes"/>.</summary>
    internal const string OversizedEventText = "The agent produced an event larger than the configured limit.";

    private static StreamResponse FailureEvent(string taskId, string contextId, string text = OversizedEventText) => StreamResponse.Of(new TaskStatusUpdateEvent
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
    });

    private static Message WithTask(Message m, string taskId)
    {
        var copy = A2AJson.Clone(m);
        copy.TaskId = taskId;
        return copy;
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static A2AException TooManyPushConfigs(int limit) =>
        new(A2AErrorCode.InvalidRequest, $"Too many push notification configs for this task (max {limit}).");

    /// <summary>Protects the secrets of <paramref name="config"/> and stores a copy. The caller's instance is not changed.</summary>
    private ValueTask SavePushConfigAsync(TaskKey key, TaskPushNotificationConfig config, CancellationToken ct) =>
        _pushConfigs.SaveAsync(ProtectedRecord(key, config), ct);

    /// <summary>
    /// Like <see cref="SavePushConfigAsync"/>, but atomically refuses (false) a new config id when the task already has
    /// <see cref="A2ALimits.MaxPushConfigsPerTask"/> configs. Replacing an existing id is allowed.
    /// </summary>
    private ValueTask<bool> SavePushConfigIfUnderLimitAsync(TaskKey key, TaskPushNotificationConfig config, CancellationToken ct) =>
        _pushConfigs.SaveIfUnderLimitAsync(ProtectedRecord(key, config), _options.Limits.MaxPushConfigsPerTask, ct);

    private PushConfigRecord ProtectedRecord(TaskKey key, TaskPushNotificationConfig config)
    {
        var copy = A2AJson.Clone(config);
        if (!string.IsNullOrEmpty(copy.Token)) copy.Token = _protector.Protect(copy.Token);
        if (copy.Authentication is { Credentials.Length: > 0 } auth) auth.Credentials = _protector.Protect(auth.Credentials);
        return new PushConfigRecord(key, copy);
    }

    /// <summary>Returns a copy with the token and credentials blanked. Secrets never leave the server.</summary>
    private static TaskPushNotificationConfig Redact(TaskPushNotificationConfig config)
    {
        var copy = A2AJson.Clone(config);
        if (!string.IsNullOrEmpty(copy.Token)) copy.Token = "";
        if (copy.Authentication is { Credentials.Length: > 0 } auth) auth.Credentials = "";
        return copy;
    }

    private static TaskKey Key(ServerCallContext call, string taskId) => new(call.Tenant, call.Principal, taskId);

    /// <summary>
    /// Applies historyLength (0 = none, n = last n) and artifact inclusion to a copy of the task. The history is capped at
    /// <see cref="A2ALimits.MaxHistoryLengthReturned"/> also when <paramref name="historyLength"/> is null or larger.
    /// </summary>
    private AgentTask Shape(AgentTask task, int? historyLength, bool includeArtifacts = true)
    {
        var copy = A2AJson.Clone(task);
        var n = Math.Min(historyLength ?? int.MaxValue, _options.Limits.MaxHistoryLengthReturned);
        if (n <= 0) copy.History = null;
        else if (copy.History is { } h && h.Count > n) copy.History = h.GetRange(h.Count - n, n);
        if (copy.History is { Count: 0 }) copy.History = null;
        if (!includeArtifacts) copy.Artifacts = null;
        return copy;
    }

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

    private static A2AException ConfigNotFound(string taskId, string id) =>
        new(A2AErrorCode.TaskNotFound, "Push notification config not found") { Metadata = new() { ["taskId"] = taskId, ["configId"] = id } };

    /// <summary>
    /// Longest idempotency key (the <c>Idempotency-Key</c> header, else the message id), in characters. Keys are held in
    /// memory per caller, so a longer one is InvalidParams.
    /// </summary>
    internal const int MaxIdempotencyKeyLength = 256;

    /// <summary>Retries after a version conflict when applying one change to a task.</summary>
    private const int MaxConflictRetries = 3;

    /// <summary>Task ids are unique per tenant only, so in-memory state (locks, running executions, bus topics) is keyed by both.</summary>
    private static string ScopeKey(string tenant, string taskId) => $"{tenant}\u001f{taskId}";

    private SemaphoreSlim LockFor(string tenant, string taskId) =>
        _locks[(StringComparer.Ordinal.GetHashCode(ScopeKey(tenant, taskId)) & 0x7fffffff) % _locks.Length];

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

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private sealed class Execution(string taskId, CancellationTokenSource cts)
    {
        private volatile bool _detached;
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string TaskId { get; } = taskId;
        public CancellationTokenSource Cts { get; } = cts;

        /// <summary>The run's span, or null when nobody listens.</summary>
        public Activity? Activity { get; init; }

        /// <summary>
        /// Ends the run's span. A run that left the task FAILED gets an error status (unless a more specific error was
        /// recorded, for example the time limit).
        /// </summary>
        public void EndActivity()
        {
            if (Activity is not { } activity) return;
            if (activity.Status == ActivityStatusCode.Unset && activity.GetTagItem(A2ATelemetry.Tags.TaskState) is string state
                && string.Equals(state, nameof(TaskState.Failed), StringComparison.Ordinal))
            {
                activity.SetTag(A2ATelemetry.Tags.ErrorType, "TaskFailed");
                activity.SetStatus(ActivityStatusCode.Error, "TaskFailed");
            }
            activity.Dispose();
        }
        public Channel<StreamResponse> Incoming { get; } = Channel.CreateUnbounded<StreamResponse>(new UnboundedChannelOptions { SingleReader = true });
        public Channel<SequencedEvent> Outgoing { get; } = Channel.CreateUnbounded<SequencedEvent>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
        public Task Completion => _done.Task;

        public void Cancel()
        {
            try { Cts.Cancel(); }
            catch (ObjectDisposedException) { /* already finished */ }
        }

        public void MarkDone()
        {
            Cts.Dispose();
            _done.TrySetResult();
        }

        public void Emit(SequencedEvent evt)
        {
            if (!_detached) Outgoing.Writer.TryWrite(evt);
        }

        /// <summary>True once the events of this execution could not be stored: the initiating request gets an error.</summary>
        public bool Failed => _failed;

        private volatile bool _failed;

        private int _outcome; // 0 running, 1 the agent returned (or threw), 2 timed out

        /// <summary>The agent returned or threw. False when the execution had already timed out.</summary>
        public bool TryMarkReturned() => Interlocked.CompareExchange(ref _outcome, 1, 0) != 2;

        /// <summary>The time limit expired. False when the agent had already returned.</summary>
        public bool TryMarkTimedOut() => Interlocked.CompareExchange(ref _outcome, 2, 0) == 0;

        /// <summary>Marks the storage failure and ends the outgoing events with <paramref name="error"/>.</summary>
        public void Fail(Exception error)
        {
            _failed = true;
            Outgoing.Writer.TryComplete(error);
        }

        /// <summary>The initiating request stopped listening; drop further outgoing events.</summary>
        public void Detach()
        {
            _detached = true;
            while (Outgoing.Reader.TryRead(out _)) { }
        }
    }
}
