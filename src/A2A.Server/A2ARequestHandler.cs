using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using A2A.Diagnostics;
using A2A.Json;
using A2A.Server.Agents;
using A2A.Server.Coordination;
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
    private readonly ExecutionCoordinator _coordinator;
    private readonly AgentCardProvider _cards;
    private readonly AgentExecutorResolver _executors;

    /// <summary>The refusal text for a tenant that no agent serves (its card is null).</summary>
    internal const string NoAgentText = "No agent is configured for this tenant.";

    /// <summary>
    /// Created by DI. The optional <c>timeProvider</c> is the clock for <see cref="A2AServerOptions.MaxTaskDuration"/> and
    /// the execution heartbeat (default <see cref="TimeProvider.System"/>). The optional <c>coordinator</c> tracks running
    /// executions across instances (default a process-local <see cref="InMemoryExecutionCoordinator"/>). The optional
    /// <c>cardProvider</c> and <c>executorResolver</c> pick each tenant's card and agent (default: the configured card and the
    /// registered <see cref="IAgentExecutor"/> for every tenant).
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="A2AServerOptions.Coordination"/> is invalid.</exception>
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
        TimeProvider? timeProvider = null,
        ExecutionCoordinator? coordinator = null,
        AgentCardProvider? cardProvider = null,
        AgentExecutorResolver? executorResolver = null)
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
        _options.Coordination.Validate();
        _coordinator = coordinator ?? new InMemoryExecutionCoordinator(_options.Coordination, _time);
        _cards = cardProvider ?? new DefaultAgentCardProvider(options);
        _executors = executorResolver ?? new DefaultAgentExecutorResolver();
        StartCancelListener(_shutdown.Token);
    }

    /// <summary>
    /// The configured card (<see cref="A2AServerOptions.AgentCard"/>). With an <see cref="AgentCardProvider"/> each tenant has
    /// its own card, and requests are checked against that one.
    /// </summary>
    public AgentCard AgentCard => _options.AgentCard;

    // =====================================================================
    // Call validation: version, authentication
    // =====================================================================

    /// <summary>
    /// Checks protocol version and authentication. Extensions depend on the tenant's card, so each operation checks them once
    /// its tenant is known (also from the request body): a missing required extension is
    /// <see cref="A2AErrorCode.ExtensionSupportRequired"/>, and <see cref="ServerCallContext.ActivatedExtensions"/> is filled then.
    /// </summary>
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
    }

    // =====================================================================
    // The tenant's agent: card, capabilities, extensions
    // =====================================================================

    /// <summary>
    /// The card of the call's tenant from the <see cref="AgentCardProvider"/>, or null when no agent serves the tenant. Asked
    /// once per request and tenant. The first time, checks the extensions against it: a required extension the caller did not
    /// request is <see cref="A2AErrorCode.ExtensionSupportRequired"/>; requested and declared ones are activated.
    /// </summary>
    private async ValueTask<AgentCard?> ResolveCardAsync(ServerCallContext call, CancellationToken ct)
    {
        if (call.ResolvedCard is { } resolved && string.Equals(resolved.Tenant, call.Tenant, StringComparison.Ordinal)) return resolved.Card;
        var card = await _cards.GetCardAsync(new AgentRequestScope(call.Tenant, call.RouteValues), ct).ConfigureAwait(false);
        if (card is not null) ApplyExtensions(card, call);
        call.ResolvedCard = (call.Tenant, card);
        return card;
    }

    /// <summary>The tenant's card; a tenant no agent serves is <see cref="A2AErrorCode.UnsupportedOperation"/> (<see cref="NoAgentText"/>).</summary>
    private async ValueTask<AgentCard> RequireAgentAsync(ServerCallContext call, CancellationToken ct) =>
        await ResolveCardAsync(call, ct).ConfigureAwait(false) ?? throw new A2AException(A2AErrorCode.UnsupportedOperation, NoAgentText);

    /// <summary>The tenant's card for an operation on task <paramref name="taskId"/>; a tenant no agent serves looks like a missing task.</summary>
    private async ValueTask<AgentCard> RequireAgentForTaskAsync(ServerCallContext call, string? taskId, CancellationToken ct) =>
        await ResolveCardAsync(call, ct).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(taskId ?? "");

    /// <summary>
    /// The card that names the authentication scheme of an Unauthenticated response: the tenant's card when it has one, else
    /// <see cref="A2AServerOptions.AgentCard"/>. A failing or cancelled provider call falls back too (logged). Never throws.
    /// </summary>
    internal async ValueTask<AgentCard> ChallengeCardAsync(ServerCallContext call, CancellationToken ct)
    {
        if (call.ResolvedCard is { } resolved && string.Equals(resolved.Tenant, call.Tenant, StringComparison.Ordinal))
            return resolved.Card ?? _options.AgentCard;
        try { return await _cards.GetCardAsync(new AgentRequestScope(call.Tenant, call.RouteValues), ct).ConfigureAwait(false) ?? _options.AgentCard; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve the Agent Card of the tenant for the authentication challenge");
            return _options.AgentCard;
        }
    }

    private static void ApplyExtensions(AgentCard card, ServerCallContext call)
    {
        var declared = card.Capabilities.Extensions ?? [];
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
        await RequireAgentAsync(call, cancellationToken).ConfigureAwait(false);
        // Before the idempotency claim and anything stored: a tenant without an agent leaves no trace.
        await using var agent = await RequireExecutorAsync(call.Tenant).ConfigureAwait(false);
        var (claim, replay) = await BeginIdempotentAsync(request, call, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            await agent.DisposeAsync().ConfigureAwait(false); // nothing runs
            return await ReplayAsync(replay, request, call, cancellationToken).ConfigureAwait(false);
        }
        var taskId = string.IsNullOrEmpty(request.Message?.TaskId) ? TaskIds.New() : request.Message.TaskId;
        var scope = ScopeKey(call.Tenant, taskId);
        // Subscribe before the agent starts, so no event of another execution is missed.
        using var sub = _bus.Subscribe(scope);
        var (accepted, afterSequence) = await AcceptAsync(request, call, claim, taskId, agent, cancellationToken).ConfigureAwait(false);
        if (accepted is not { } exec)
        {
            // A duplicate follow-up: nothing started, so there is nothing to wait for.
            var current = await _tasks.GetAsync(Key(call, taskId), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(taskId);
            return new TaskResponse(Shape(current.Task, request.Configuration?.HistoryLength));
        }
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
                    // This execution ended. Nothing more will change the task once no execution of it runs, on any instance.
                    if (!started || await RunningWaitsAsync(Key(call, exec.TaskId), stop.Token).ConfigureAwait(false) is not { } running) break;
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
        RequireStreaming(await RequireAgentAsync(call, cancellationToken).ConfigureAwait(false));
        // Before the idempotency claim and anything stored: a tenant without an agent leaves no trace.
        await using var agent = await RequireExecutorAsync(call.Tenant).ConfigureAwait(false);
        var (claim, replay) = await BeginIdempotentAsync(request, call, cancellationToken).ConfigureAwait(false);
        if (replay is not null) await agent.DisposeAsync().ConfigureAwait(false); // nothing runs
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
        var (accepted, _) = await AcceptAsync(request, call, claim, taskId, agent, cancellationToken).ConfigureAwait(false);
        if (accepted is not { } exec)
        {
            await agent.DisposeAsync().ConfigureAwait(false); // nothing runs
            // A duplicate follow-up: nothing started. The same path as a retried request: the retained events from the start,
            // then live events.
            var resume = new SubscribeToTaskRequest { Id = taskId, Tenant = request.Tenant };
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
                    // Nothing will change the task any more once no execution of it runs, on any instance.
                    if (await RunningWaitsAsync(key, stop.Token).ConfigureAwait(false) is not { } running) break;
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

    /// <summary>Completions of the executions running for a task on this instance (in-memory scope key).</summary>
    private List<Task> RunningCompletions(string scope) =>
        _running.Snapshot(scope).Select(e => e.Completion).ToList();

    /// <summary>
    /// Whether an execution of the task runs on any instance. Null when none does; otherwise what to wait on before asking
    /// again: the completions of the executions on this instance, or, when only other instances run one, a
    /// <see cref="CoordinationOptions.RunningPollInterval"/> delay. Executions here are checked first, without a coordinator round trip.
    /// </summary>
    private async ValueTask<List<Task>?> RunningWaitsAsync(TaskKey key, CancellationToken ct)
    {
        var local = RunningCompletions(ScopeKey(key.Tenant, key.TaskId));
        if (local.Count > 0) return local;
        if (!await _coordinator.IsRunningAsync(key, ct).ConfigureAwait(false)) return null;
        return [Task.Delay(_options.Coordination.RunningPollInterval, ct)];
    }

    /// <param name="Context">The request context for the agent.</param>
    /// <param name="PushConfig">The push config to save when a new task is created.</param>
    /// <param name="LastSequence">For a follow-up, the task's last event sequence right after the message was appended; 0 for a new task.</param>
    /// <param name="Duplicate">The follow-up's message id was already in the task's history: nothing was appended and no agent must start.</param>
    private sealed record Prepared(AgentRequestContext Context, TaskPushNotificationConfig? PushConfig, long LastSequence, bool Duplicate = false);

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
    /// <item>no execution running for the task any more, on any instance (the agent returned without settling it, or its
    /// instance stopped): nothing will change it, so its current state;</item>
    /// <item>or the request is cancelled.</item>
    /// </list>
    /// Subscribing before the first read, and checking for running executions before reading the task, means no change is missed.
    /// </summary>
    private async Task<AgentTask> WaitUntilSettledAsync(TaskKey key, long afterSequence, CancellationToken ct)
    {
        using var sub = _bus.Subscribe(ScopeKey(key.Tenant, key.TaskId));
        var statusAfter = afterSequence == 0 || await HasStatusEventAfterAsync(key, afterSequence, ct).ConfigureAwait(false);
        Task<bool>? live = null; // one pending wait at a time: the subscription has a single reader
        while (true)
        {
            var running = await RunningWaitsAsync(key, ct).ConfigureAwait(false);
            var record = await _tasks.GetAsync(key, ct).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(key.TaskId);
            var state = record.Task.Status.State;
            if (state.IsTerminal() || (state.IsInterrupted() && statusAfter) || running is null) return record.Task;

            live ??= sub.Reader.WaitToReadAsync(ct).AsTask();
            await Task.WhenAny(running.Append(live)).ConfigureAwait(false);
            if (!live.IsCompleted) continue; // an execution ended, or time to ask the coordinator again
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
    /// <returns>
    /// The execution, and the task's last sequence before it (0 for a new task; see <see cref="Prepared.LastSequence"/>). The
    /// execution is null for a duplicate follow-up (see <see cref="Prepared.Duplicate"/>): nothing was started.
    /// </returns>
    private async Task<(Execution? Execution, long AfterSequence)> AcceptAsync(SendMessageRequest request, ServerCallContext call, IdempotencyClaim claim,
        string? newTaskId, ResolvedAgent agent, CancellationToken ct)
    {
        try
        {
            var prepared = await PrepareAsync(request, call, ct, newTaskId).ConfigureAwait(false);
            if (prepared.Context.IsNewTask) return (await StartExecutionAsync(prepared.Context, prepared.PushConfig, claim, agent).ConfigureAwait(false), 0);
            if (prepared.Duplicate)
            {
                // The message is already in the history (an earlier request of it failed after the commit): its outcome is
                // the task as it is. A retry with this claim gets the task as it is too.
                await RecordAsync(claim, new IdempotencyOutcome.TaskCreated(prepared.Context.TaskId, 0), prepared.Context.TaskId).ConfigureAwait(false);
                return (null, 0);
            }

            // Record after the execution is registered (on every instance), so a blocking retry sees it running and waits
            // for its outcome.
            var exec = await StartExecutionAsync(prepared.Context, prepared.PushConfig, IdempotencyClaim.None, agent).ConfigureAwait(false);
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
        var duplicate = false;
        var approval = ApprovalState.None;

        // Check the push config before anything changes, so a refused request leaves no trace and a retry is refused the same way.
        TaskPushNotificationConfig? push = null;
        if (request.Configuration?.TaskPushNotificationConfig is { } p)
        {
            RequirePush(await RequireAgentAsync(call, ct).ConfigureAwait(false));
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
                    if (record.Task.History?.Any(m => string.Equals(m.MessageId, message.MessageId, StringComparison.Ordinal)) == true)
                    {
                        // Already accepted (an earlier request of it failed after the commit): no push config, no append, no
                        // new execution. Checked before the state, so a retry after the task settled gets its outcome.
                        duplicate = true;
                        existing = record.Task;
                        contextId = record.Task.ContextId;
                        break;
                    }
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
                    approval = ResolveApproval(record.Task, message, out var consume);
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
                    // Same update as the approving message: the approval cannot be used twice, also across instances (CAS).
                    if (consume is not null) (record.Task.Metadata ??= [])[A2AProtocol.ApprovalConsumedKey] = StringElement(consume);
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
                        // The task changed under us: wait a moment, reload, re-check the state, and append again.
                        await ConflictBackoffAsync(attempt, ct).ConfigureAwait(false);
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
            Approval = approval,
        };
        return new Prepared(context, existing is null ? push : null, lastSequence, duplicate);
    }

    /// <summary>
    /// Approval binding (spec design §6.6). The pending approval is the hash in the metadata of the status message of a task
    /// waiting in an interrupted state, so it is stored with the task and every instance sees it. It is used at most once:
    /// the commit of the approving message also records the status message's id in the task metadata
    /// (<see cref="A2AProtocol.ApprovalConsumedKey"/>), and a used approval is no longer pending. A follow-up without a hash is
    /// accepted (<see cref="ApprovalState.NotApproved"/> while an approval is pending); a hash that does not match the pending
    /// one, or when none is pending, is InvalidParams. <paramref name="consume"/> is the status message id to record when the
    /// result is <see cref="ApprovalState.Approved"/>.
    /// </summary>
    private static ApprovalState ResolveApproval(AgentTask task, Message message, out string? consume)
    {
        consume = null;
        string? pending = null;
        var status = task.Status.Message;
        if (task.Status.State.IsInterrupted() && status?.Metadata is { } statusMetadata
            && statusMetadata.TryGetValue(A2AProtocol.ApprovalHashKey, out var p) && p.ValueKind == JsonValueKind.String)
            pending = p.GetString();
        var used = pending is not null && task.Metadata is { } taskMetadata
            && taskMetadata.TryGetValue(A2AProtocol.ApprovalConsumedKey, out var c) && c.ValueKind == JsonValueKind.String
            && string.Equals(c.GetString(), status!.MessageId, StringComparison.Ordinal);

        if (message.Metadata is null || !message.Metadata.TryGetValue(A2AProtocol.ApprovalHashKey, out var value))
            return pending is null ? ApprovalState.None : ApprovalState.NotApproved;
        if (pending is null || used || value.ValueKind != JsonValueKind.String
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pending), Encoding.UTF8.GetBytes(value.GetString()!)))
            throw A2AException.InvalidParams("Approval does not match the pending request.");
        consume = status!.MessageId;
        return ApprovalState.Approved;
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
        await RequireAgentForTaskAsync(call, request.Id, cancellationToken).ConfigureAwait(false);
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
        await RequireAgentAsync(call, cancellationToken).ConfigureAwait(false);
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
        await RequireAgentForTaskAsync(call, request.Id, cancellationToken).ConfigureAwait(false);
        A2AValidator.RequireId(request.Id, "id");
        var key = Key(call, request.Id);
        var record = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        if (record.Task.Status.State.IsTerminal()) throw NotCancelable(request.Id);

        // Executions here stop at once; the other instances stop theirs when the request reaches them.
        CancelAll(_running.Snapshot(ScopeKey(call.Tenant, request.Id)));
        try
        {
            await _coordinator.RequestCancelAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not ask other instances to cancel task {TaskId}", request.Id);
        }

        var snapshot = record.Task;
        // Without an agent for the tenant, the default cancel runs (the task is marked canceled), so a task whose agent is
        // gone can still be cancelled.
        await using var agent = ResolveAgent(call.Tenant, CancelOnlyAgent.Instance);
        var exec = await StartRawAsync(snapshot.Id, snapshot.ContextId, isNewTask: false, userMessage: null, call, push: null, emitInitialSnapshot: false,
            (executor, updater, ct) => executor.CancelAsync(snapshot, updater, ct), agent, requestMetadata: request.Metadata, kind: CancelKind).ConfigureAwait(false);
        exec.Detach();

        await WaitUntilStoppedAsync(key, _options.CancelGracePeriod, cancellationToken).ConfigureAwait(false);

        record = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        var state = record.Task.Status.State;
        if (state == TaskState.Canceled) return Shape(record.Task, null);
        if (!state.IsTerminal() && await IsRunningAnywhereAsync(key, cancellationToken).ConfigureAwait(false))
        {
            // The execution has not stopped within the grace period: the cancel is requested, not done. Only the response says
            // so; its later CANCELED arrives through the normal stream.
            var pending = Shape(record.Task, null);
            (pending.Metadata ??= [])[A2AProtocol.CancelRequestedKey] = TrueElement;
            return pending;
        }
        throw NotCancelable(request.Id); // the agent refused and nothing runs, or the task ended otherwise
    }

    private static JsonElement StringElement(string value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) writer.WriteStringValue(value);
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    private static readonly JsonElement TrueElement = JsonDocument.Parse("true").RootElement.Clone();

    /// <summary>Whether an execution of the task runs on any instance. A failing coordinator counts as not running (logged).</summary>
    private async Task<bool> IsRunningAnywhereAsync(TaskKey key, CancellationToken ct)
    {
        if (RunningCompletions(ScopeKey(key.Tenant, key.TaskId)).Count > 0) return true;
        try { return await _coordinator.IsRunningAsync(key, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not check whether task {TaskId} still runs on other instances", key.TaskId);
            return false;
        }
    }

    /// <summary>
    /// Waits until no execution of the task runs on any instance (the cancelled ones and the CancelTask execution), or
    /// <paramref name="grace"/> has passed. When the coordinator fails, waits out the grace period (logged), so CancelTask
    /// still answers.
    /// </summary>
    private async Task WaitUntilStoppedAsync(TaskKey key, TimeSpan grace, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(grace);
        try
        {
            while (await RunningWaitsAsync(key, timeout.Token).ConfigureAwait(false) is { } running)
                await Task.WhenAny(running).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The grace period is over: answer with the task as it is.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The coordinator failed: other instances cannot be checked, so give them the whole grace period.
            _logger.LogWarning(ex, "Could not check whether task {TaskId} still runs on other instances; waiting out the cancel grace period", key.TaskId);
            try { await Task.Delay(Timeout.Infinite, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
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
        RequireStreaming(await RequireAgentForTaskAsync(call, request.Id, cancellationToken).ConfigureAwait(false));
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
        RequirePush(await RequireAgentForTaskAsync(call, config.TaskId, cancellationToken).ConfigureAwait(false));
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
        RequirePush(await RequireAgentForTaskAsync(call, request.TaskId, cancellationToken).ConfigureAwait(false));
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
        RequirePush(await RequireAgentForTaskAsync(call, request.TaskId, cancellationToken).ConfigureAwait(false));
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
        RequirePush(await RequireAgentForTaskAsync(call, request.TaskId, cancellationToken).ConfigureAwait(false));
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
        var publicCard = await RequireAgentAsync(call, cancellationToken).ConfigureAwait(false);
        if (publicCard.Capabilities.ExtendedAgentCard != true)
            throw new A2AException(A2AErrorCode.UnsupportedOperation, "This agent does not provide an extended agent card.");
        if (!call.IsAuthenticated) throw new A2AException(A2AErrorCode.Unauthenticated, "The extended agent card requires authentication.");
        var card = await _cards.GetExtendedCardAsync(call, cancellationToken).ConfigureAwait(false);
        return card ?? throw new A2AException(A2AErrorCode.ExtendedAgentCardNotConfigured);
    }

    // =====================================================================
    // Execution engine
    // =====================================================================

    private Task<Execution> StartExecutionAsync(AgentRequestContext context, TaskPushNotificationConfig? push, IdempotencyClaim claim, ResolvedAgent agent) =>
        StartRawAsync(context.TaskId, context.ContextId, context.IsNewTask, context.Message, context.Call, push, emitInitialSnapshot: !context.IsNewTask,
            (executor, updater, ct) => executor.ExecuteAsync(context, updater, ct), agent, claim, context.Metadata, _options.MaxTaskDuration);

    /// <summary>
    /// The agent for an execution of the tenant, from a new DI scope that becomes the execution's own scope (one scoped executor
    /// per execution, disposed when it ends). A resolver that throws does not throw here: the execution fails with that error,
    /// like an agent that throws. A null executor becomes <paramref name="fallback"/> when one is given.
    /// </summary>
    private ResolvedAgent ResolveAgent(string tenant, IAgentExecutor? fallback = null)
    {
        var scope = _scopeFactory.CreateAsyncScope();
        try
        {
            return new ResolvedAgent(scope, _executors.Resolve(tenant, scope.ServiceProvider) ?? fallback, null);
        }
        catch (Exception ex)
        {
            return new ResolvedAgent(scope, null, ExceptionDispatchInfo.Capture(ex));
        }
    }

    /// <summary>
    /// <see cref="ResolveAgent"/> for a message: a tenant whose resolver returns no executor is
    /// <see cref="A2AErrorCode.UnsupportedOperation"/> (<see cref="NoAgentText"/>), before anything is claimed or stored.
    /// </summary>
    private async ValueTask<ResolvedAgent> RequireExecutorAsync(string tenant)
    {
        var agent = ResolveAgent(tenant);
        if (agent.Executor is not null || agent.Error is not null) return agent;
        await agent.DisposeAsync().ConfigureAwait(false);
        throw new A2AException(A2AErrorCode.UnsupportedOperation, NoAgentText);
    }

    /// <summary>The execution kind of a CancelTask run. Cancel requests from other instances never cancel it.</summary>
    private const string CancelKind = "cancel";

    /// <summary>
    /// Runs the agent for a task. An idempotency <c>claim</c> is completed when the task is first stored or the agent replies
    /// directly, and abandoned when neither happens. With a <paramref name="timeLimit"/>, an execution still running when it
    /// expires fails the task (<see cref="TimeLimitText"/>) and is cancelled.
    /// </summary>
    /// <remarks>
    /// The execution is registered with the <see cref="ExecutionCoordinator"/> before the agent starts, so every instance sees
    /// it running. Every <see cref="CoordinationOptions.Heartbeat"/> it renews its lease and its idempotency claim (while the
    /// claim is unsettled, for example during a long direct reply). It unregisters once the agent returned and every event is
    /// stored, before the execution counts as done here. Coordinator failures are logged and never stop the execution.
    /// </remarks>
    private async Task<Execution> StartRawAsync(string taskId, string contextId, bool isNewTask, Message? userMessage, ServerCallContext call,
        TaskPushNotificationConfig? push, bool emitInitialSnapshot, Func<IAgentExecutor, TaskUpdater, CancellationToken, Task> run, ResolvedAgent agent,
        IdempotencyClaim? claim = null, Dictionary<string, JsonElement>? requestMetadata = null, TimeSpan? timeLimit = null, string kind = "execute")
    {
        // Background writes outlive the request, so fix the owner scope now.
        var key = Key(call, taskId);
        claim ??= IdempotencyClaim.None;
        // The request resolved the tenant's card already; its push capability holds for the whole execution, so no card
        // provider call happens per event (or under a task lock).
        var pushEnabled = (call.ResolvedCard is { } resolved && string.Equals(resolved.Tenant, call.Tenant, StringComparison.Ordinal)
            ? resolved.Card
            : await ResolveCardAsync(call, CancellationToken.None).ConfigureAwait(false))?.Capabilities.PushNotifications == true;
        var exec = new Execution(key, Guid.NewGuid().ToString(), kind == CancelKind, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
        {
            // A child of the operation's span; ends when the agent has returned and every event is stored.
            Activity = ServerTelemetry.StartExecution(kind, call, taskId, contextId),
            PushEnabled = pushEnabled,
        };
        var scope = ScopeKey(call.Tenant, taskId);
        // Here first, then cluster-wide: whenever the coordinator lists the execution, this instance has it too.
        _running.Add(scope, exec);
        try
        {
            await _coordinator.RegisterAsync(key, exec.Id, CancellationToken.None).ConfigureAwait(false);
            exec.Registered = true;
        }
        catch (Exception ex)
        {
            // The next heartbeat registers it instead.
            _logger.LogWarning(ex, "Could not register the execution of task {TaskId} with the execution coordinator", taskId);
        }
        var heartbeat = _time.CreateTimer(_ => _ = HeartbeatAsync(exec, claim), null, _options.Coordination.Heartbeat, _options.Coordination.Heartbeat);
        var updater = new TaskUpdater(taskId, contextId, isNewTask, exec.Incoming.Writer, requestMetadata);
        // Started before the agent, so the time spent waiting for a thread counts too. Disposed when the agent returns.
        var timer = timeLimit is { } limit && IsTimeLimit(limit)
            ? _time.CreateTimer(_ => _ = TimeOutAsync(exec, updater, taskId), null, limit, Timeout.InfiniteTimeSpan)
            : null;

        // From here the execution owns the agent's scope and disposes it when the agent returns.
        var agentScope = agent.TakeScope();
        var producer = Task.Run(async () =>
        {
            if (exec.Activity is not null) Activity.Current = exec.Activity; // the agent's own spans nest under the run
            try
            {
                await using var scope = agentScope;
                agent.Error?.Throw(); // the resolver failed: as if the agent had thrown
                var executor = agent.Executor ?? throw new InvalidOperationException($"No agent executor is configured for tenant '{key.Tenant}'.");
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

        var consumer = Task.Run(() =>
        {
            if (exec.Activity is not null) Activity.Current = exec.Activity; // webhook deliveries link to the run
            return ConsumeAsync(exec, key, contextId, isNewTask, userMessage, push, emitInitialSnapshot, claim);
        });
        _ = FinishAsync();
        return exec;

        async Task FinishAsync()
        {
            try { await Task.WhenAll(producer, consumer).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "The execution of task {TaskId} ended with an error", taskId); }
            await heartbeat.DisposeAsync().ConfigureAwait(false);
            // Waits for a heartbeat still under way and keeps later ones out, so a late register cannot outlive the unregister.
            await exec.HeartbeatGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Fenced by the execution id: never ends a newer execution's lease.
                await _coordinator.UnregisterAsync(key, exec.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not unregister the execution of task {TaskId} from the execution coordinator", taskId);
            }
            _running.Remove(scope, exec);
            exec.EndActivity();
            exec.MarkDone();
        }
    }

    /// <summary>
    /// Renews the execution's lease, or registers it when the register at start failed, and renews its idempotency claim.
    /// Runs on the heartbeat timer; skips a beat while the previous one is still running. Failures are logged and the
    /// execution goes on.
    /// </summary>
    private async Task HeartbeatAsync(Execution exec, IdempotencyClaim claim)
    {
        if (!exec.HeartbeatGate.Wait(0)) return;
        try
        {
            try
            {
                if (exec.Registered)
                {
                    await _coordinator.HeartbeatAsync(exec.Key, exec.Id, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    // A first registration, not a resurrection: this execution never had a lease.
                    await _coordinator.RegisterAsync(exec.Key, exec.Id, CancellationToken.None).ConfigureAwait(false);
                    exec.Registered = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not renew the execution lease of task {TaskId}", exec.TaskId);
            }
            try
            {
                await claim.RenewAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not renew the idempotency claim of task {TaskId}", exec.TaskId);
            }
        }
        finally
        {
            exec.HeartbeatGate.Release();
        }
    }

    /// <summary>
    /// Reads the cancel requests of every instance and cancels the matching executions here, until shutdown. The first reader
    /// is registered before this returns, so no request sent after the handler exists is missed; after a failure it reads
    /// again every <see cref="CoordinationOptions.RunningPollInterval"/>.
    /// </summary>
    private void StartCancelListener(CancellationToken stopping)
    {
        var (requests, next) = ReadCancelRequests(stopping);
        _ = ListenForCancelRequestsAsync(requests, next, stopping);
    }

    /// <summary>Starts a reader of cancel requests (registered once this returns), or returns null when the coordinator fails.</summary>
    private (IAsyncEnumerator<TaskKey>? Requests, ValueTask<bool> Next) ReadCancelRequests(CancellationToken stopping)
    {
        try
        {
            var requests = _coordinator.CancelRequestsAsync(stopping).GetAsyncEnumerator(stopping);
            return (requests, requests.MoveNextAsync());
        }
        catch (Exception ex)
        {
            LogCancelReadFailure(ex);
            return (null, default);
        }
    }

    private async Task ListenForCancelRequestsAsync(IAsyncEnumerator<TaskKey>? requests, ValueTask<bool> next, CancellationToken stopping)
    {
        while (true)
        {
            if (requests is not null)
            {
                try
                {
                    while (await next.ConfigureAwait(false))
                    {
                        _cancelReadFailures = 0; // reading works again
                        CancelHere(requests.Current);
                        next = requests.MoveNextAsync();
                    }
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    LogCancelReadFailure(ex);
                }
                try { await requests.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogDebug(ex, "Could not dispose the cancel request reader"); }
            }
            if (stopping.IsCancellationRequested) return;
            try { await Task.Delay(_options.Coordination.RunningPollInterval, stopping).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            (requests, next) = ReadCancelRequests(stopping);
        }
    }

    /// <summary>Consecutive failures to read cancel requests. Only the listener loop touches it.</summary>
    private int _cancelReadFailures;

    /// <summary>The first of a run of failures is a warning; the retries that fail again are debug, until reading works.</summary>
    private void LogCancelReadFailure(Exception ex)
    {
        if (_cancelReadFailures++ == 0)
            _logger.LogWarning(ex, "Could not read cancel requests from the execution coordinator; trying again every {Interval}", _options.Coordination.RunningPollInterval);
        else
            _logger.LogDebug(ex, "Still cannot read cancel requests from the execution coordinator ({Failures} failures in a row)", _cancelReadFailures);
    }

    /// <summary>
    /// Cancels this instance's executions of <paramref name="task"/> that another instance asked to cancel, except
    /// CancelTask runs (they record the cancellation).
    /// </summary>
    private void CancelHere(TaskKey task) =>
        CancelAll(_running.Snapshot(ScopeKey(task.Tenant, task.TaskId)).Where(exec => !exec.IsCancelRun && exec.Key == task));

    /// <summary>Cancels each execution; a cancellation callback that throws is logged and does not stop the others.</summary>
    private void CancelAll(IEnumerable<Execution> executions)
    {
        foreach (var exec in executions)
        {
            if (exec.Cancel() is { } error)
                _logger.LogWarning(error, "A cancellation callback of the agent for task {TaskId} failed", exec.TaskId);
        }
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

                    if (await ApplyAndStoreAsync(key, record, evt, exec.PushEnabled).ConfigureAwait(false) is not { } stored) continue;

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
            await TryFailStoredTaskAsync(key, exec.PushEnabled).ConfigureAwait(false);
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
            _logger.LogError(ex, "Could not mark task {TaskId} as failed after a storage error", key.TaskId);
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
        var gate = LockFor(key.Tenant, key.TaskId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!evenIfRunningHere && _running.Snapshot(scope).Any(exec => exec.Key == key)) return false;
            var record = await _tasks.GetAsync(key, ct).ConfigureAwait(false);
            if (record is null || record.Task.Status.State.IsTerminal()) return false;
            var evt = StatusEvent(key.TaskId, record.Task.ContextId, state, text, _time.GetUtcNow());
            if (await ApplyAndStoreAsync(key, record, evt, push, precondition).ConfigureAwait(false) is not { } stored) return false;
            _bus.Publish(scope, stored);
            if (state.IsTerminal())
            {
                await _events.MarkTerminalAsync(key).ConfigureAwait(false);
                _bus.Complete(scope);
            }
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Cancels every execution of the task: this instance's at once (CancelTask runs included), the other instances' through
    /// <see cref="ExecutionCoordinator.RequestCancelAsync"/>. Does not wait for them to stop.
    /// </summary>
    internal async Task CancelExecutionsAsync(TaskKey key, CancellationToken ct = default)
    {
        CancelAll(_running.Snapshot(ScopeKey(key.Tenant, key.TaskId)).Where(exec => exec.Key == key));
        await _coordinator.RequestCancelAsync(key, ct).ConfigureAwait(false);
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
    /// conflict, waits (<see cref="ConflictBackoffAsync"/>), reloads and re-applies, up to <see cref="MaxConflictRetries"/>
    /// times: another instance writing the same task back to back can win the race several times in a row. Returns null when the event was
    /// dropped (for example the task is already terminal, or the record, also a reloaded one, fails <paramref name="precondition"/>).
    /// </summary>
    private async Task<SequencedEvent?> ApplyAndStoreAsync(TaskKey key, TaskRecord record, StreamResponse evt, bool pushEnabled,
        Func<TaskRecord, bool>? precondition = null)
    {
        var deliveries = await DeliveriesAsync(key, pushEnabled).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            if (precondition is not null && !precondition(record))
            {
                _logger.LogDebug("Task {TaskId} changed; dropping the event", key.TaskId);
                return null;
            }
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
                await ConflictBackoffAsync(attempt, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>
    /// One push delivery per push config of the task, for the event about to be committed. None when the card of the task's
    /// tenant has no push notifications (<paramref name="pushEnabled"/>, resolved before the task lock).
    /// </summary>
    private async ValueTask<IReadOnlyList<PushDelivery>> DeliveriesAsync(TaskKey key, bool pushEnabled)
    {
        if (!pushEnabled) return [];
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

    private static StreamResponse StatusEvent(string taskId, string contextId, TaskState state, string text, DateTimeOffset at) => StreamResponse.Of(new TaskStatusUpdateEvent
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
    });

    private static StreamResponse FailureEvent(string taskId, string contextId, string text = OversizedEventText) =>
        StatusEvent(taskId, contextId, TaskState.Failed, text, DateTimeOffset.UtcNow);

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

    /// <summary>
    /// The push capability of the task's tenant for a commit no request made (the retention sweep), from the tenant alone. No
    /// agent for the tenant means no push. A failing provider fails closed: no push deliveries for this commit (logged).
    /// </summary>
    private async ValueTask<bool> PushEnabledForTenantAsync(TaskKey key, CancellationToken ct)
    {
        try
        {
            var card = await _cards.GetCardAsync(AgentRequestScope.ForTenant(key.Tenant), ct).ConfigureAwait(false);
            return card?.Capabilities.PushNotifications == true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not resolve the Agent Card of task {TaskId}; its status change sends no push notifications", key.TaskId);
            return false;
        }
    }

    private static void RequireStreaming(AgentCard card)
    {
        if (card.Capabilities.Streaming != true)
            throw new A2AException(A2AErrorCode.UnsupportedOperation, "Streaming is not supported by this agent.");
    }

    private static void RequirePush(AgentCard card)
    {
        if (card.Capabilities.PushNotifications != true)
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

    /// <summary>
    /// Retries after a version conflict when applying one change to a task. Conflicts come from other instances (writers here
    /// share the task lock), and one that writes back to back can win several races in a row.
    /// </summary>
    private const int MaxConflictRetries = 10;

    /// <summary>
    /// Waits before retry number <paramref name="attempt"/> + 1 after a version conflict: 5 ms doubling per attempt, at most
    /// 200 ms, ±20 % jitter, so writers on different instances stop colliding in lockstep. Real time on purpose (not the
    /// injected <see cref="TimeProvider"/>): a manual test clock must not stall a store retry.
    /// </summary>
    private static Task ConflictBackoffAsync(int attempt, CancellationToken ct)
    {
        var baseMs = Math.Min(5.0 * Math.Pow(2, Math.Min(attempt, 16)), 200.0);
        var jittered = baseMs * (0.8 + (0.4 * Random.Shared.NextDouble()));
        return Task.Delay(TimeSpan.FromMilliseconds(jittered), ct);
    }

    /// <summary>Task ids are unique per tenant only, so in-memory state (locks, running executions, bus topics) is keyed by both.</summary>
    private static string ScopeKey(string tenant, string taskId) => $"{tenant}\u001f{taskId}";

    private SemaphoreSlim LockFor(string tenant, string taskId) => _locks[LockStripe(tenant, taskId)];

    /// <summary>The index of the task's lock among the shared striped locks (tasks on the same stripe share a lock).</summary>
    internal int LockStripe(string tenant, string taskId) =>
        (StringComparer.Ordinal.GetHashCode(ScopeKey(tenant, taskId)) & 0x7fffffff) % _locks.Length;

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

    /// <summary>
    /// The agent of one execution, resolved before anything is claimed or stored, with the DI scope it came from. The request
    /// disposes the scope unless an execution took it (<see cref="TakeScope"/>), which then disposes it when the agent returns.
    /// </summary>
    private sealed class ResolvedAgent(AsyncServiceScope scope, IAgentExecutor? executor, ExceptionDispatchInfo? error) : IAsyncDisposable
    {
        private AsyncServiceScope? _scope = scope;

        /// <summary>The executor, or null when the resolver returned none or failed.</summary>
        public IAgentExecutor? Executor { get; } = executor;

        /// <summary>The resolver's failure, rethrown by the execution so the task fails like an agent that throws.</summary>
        public ExceptionDispatchInfo? Error { get; } = error;

        /// <summary>Hands the scope to the execution.</summary>
        public AsyncServiceScope TakeScope()
        {
            var taken = _scope ?? throw new InvalidOperationException("The agent's scope was already taken or disposed.");
            _scope = null;
            return taken;
        }

        public ValueTask DisposeAsync()
        {
            var owned = _scope;
            _scope = null;
            return owned?.DisposeAsync() ?? default;
        }
    }

    /// <summary>Runs CancelTask for a tenant without an agent: the default cancel (the task is marked canceled).</summary>
    private sealed class CancelOnlyAgent : IAgentExecutor
    {
        public static readonly CancelOnlyAgent Instance = new();

        public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No agent is configured for this tenant.");
    }

    private sealed class Execution(TaskKey key, string id, bool isCancelRun, CancellationTokenSource cts)
    {
        private volatile bool _detached;
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _registered;

        /// <summary>The task, owner-scoped.</summary>
        public TaskKey Key { get; } = key;

        public string TaskId => Key.TaskId;

        /// <summary>This execution's id with the <see cref="ExecutionCoordinator"/>: new for every execution.</summary>
        public string Id { get; } = id;

        /// <summary>True for the run of a CancelTask (<see cref="IAgentExecutor.CancelAsync"/>).</summary>
        public bool IsCancelRun { get; } = isCancelRun;

        public CancellationTokenSource Cts { get; } = cts;

        /// <summary>True once the execution's lease is registered with the coordinator.</summary>
        public bool Registered { get => _registered; set => _registered = value; }

        /// <summary>Held by a running heartbeat, and for good once the execution ends.</summary>
        public SemaphoreSlim HeartbeatGate { get; } = new(1, 1);

        /// <summary>The run's span, or null when nobody listens.</summary>
        public Activity? Activity { get; init; }

        /// <summary>Whether the tenant's card had push notifications when the execution started; decides its push deliveries.</summary>
        public bool PushEnabled { get; init; }

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

        /// <summary>
        /// Cancels the execution. Returns the error of a cancellation callback that threw (the token is cancelled anyway), or null.
        /// </summary>
        public Exception? Cancel()
        {
            try
            {
                Cts.Cancel();
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null; // already finished
            }
            catch (AggregateException ex)
            {
                return ex;
            }
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
