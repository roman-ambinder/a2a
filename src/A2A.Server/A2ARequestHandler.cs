using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using A2A.Json;
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
    private readonly PushConfigStore _pushConfigs;
    private readonly A2AAuthorizationHandler _authorization;
    private readonly IPushSecretProtector _protector;
    private readonly IPushNotificationSender _push;
    private readonly TaskEventBus _bus;
    private readonly A2AServerOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Execution, byte>> _running = new(StringComparer.Ordinal);

    public A2ARequestHandler(
        IServiceScopeFactory scopeFactory,
        TaskStore tasks,
        EventStore events,
        PushConfigStore pushConfigs,
        A2AAuthorizationHandler authorization,
        IPushSecretProtector protector,
        IPushNotificationSender push,
        TaskEventBus bus,
        IOptions<A2AServerOptions> options,
        ILogger<A2ARequestHandler>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _tasks = tasks;
        _events = events;
        _pushConfigs = pushConfigs;
        _authorization = authorization;
        _protector = protector;
        _push = push;
        _bus = bus;
        _options = options.Value;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
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

    public async Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.SendMessage, request, request.Message?.TaskId, cancellationToken).ConfigureAwait(false);
        var prepared = await PrepareAsync(request, call, cancellationToken).ConfigureAwait(false);
        var exec = StartExecution(prepared.Context, prepared.PushConfig);
        var returnImmediately = request.Configuration?.ReturnImmediately == true;
        try
        {
            await foreach (var sequenced in exec.Outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var evt = sequenced.Event;
                if (evt is MessageEvent reply) return new MessageResponse(reply.Message);
                if (returnImmediately) break;
                if (evt is StatusUpdateEvent { StatusUpdate: var su } && (su.Status.State.IsTerminal() || su.Status.State.IsInterrupted())) break;
            }
        }
        finally
        {
            exec.Detach();
        }

        var record = await _tasks.GetAsync(Key(call, prepared.Context.TaskId), cancellationToken).ConfigureAwait(false)
            ?? throw new A2AException(A2AErrorCode.InvalidAgentResponse, "The agent finished without replying or creating a task.");
        return new TaskResponse(Shape(record.Task, request.Configuration?.HistoryLength));
    }

    public async IAsyncEnumerable<SequencedEvent> SendStreamingMessageAsync(SendMessageRequest request, ServerCallContext call,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.SendStreamingMessage, request, request.Message?.TaskId, cancellationToken).ConfigureAwait(false);
        RequireStreaming();
        var prepared = await PrepareAsync(request, call, cancellationToken).ConfigureAwait(false);
        var exec = StartExecution(prepared.Context, prepared.PushConfig);
        var any = false;
        try
        {
            await foreach (var sequenced in exec.Outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                any = true;
                yield return sequenced;
                if (sequenced.Event is MessageEvent) yield break;
                if (sequenced.Event is StatusUpdateEvent { StatusUpdate.Status.State: var st } && st.IsTerminal()) yield break;
            }
        }
        finally
        {
            exec.Detach();
        }
        if (!any) throw new A2AException(A2AErrorCode.InvalidAgentResponse, "The agent finished without replying or creating a task.");
    }

    private sealed record Prepared(AgentRequestContext Context, TaskPushNotificationConfig? PushConfig);

    private async Task<Prepared> PrepareAsync(SendMessageRequest request, ServerCallContext call, CancellationToken ct)
    {
        call.BindTenant(request.Tenant);
        A2AValidator.ValidateIncoming(request);
        RequestLimitValidator.Validate(request.Message, _options.Limits);
        var message = request.Message;
        AgentTask? existing = null;
        string taskId, contextId;

        if (!string.IsNullOrEmpty(message.TaskId))
        {
            taskId = message.TaskId;
            var gate = LockFor(call.Tenant, taskId);
            await gate.WaitAsync(ct).ConfigureAwait(false);
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
                    contextId = record.Task.ContextId;
                    message.ContextId = contextId;
                    (record.Task.History ??= []).Add(message);
                    try
                    {
                        await _tasks.UpdateAsync(record, record.Version, ct).ConfigureAwait(false);
                        existing = record.Task;
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
                gate.Release();
            }
        }
        else
        {
            taskId = TaskIds.New();
            contextId = await ResolveContextAsync(message.ContextId, call, ct).ConfigureAwait(false);
            message.ContextId = contextId;
        }

        TaskPushNotificationConfig? push = null;
        if (request.Configuration?.TaskPushNotificationConfig is { } p)
        {
            RequirePush();
            PushUrlValidator.Validate(p.Url, _options.PushNotifications);
            push = A2AJson.Clone(p);
            push.TaskId = taskId;
            push.Id = string.IsNullOrEmpty(push.Id) ? TaskIds.New() : push.Id;
            push.Tenant = call.Tenant;
            if (existing is not null)
            {
                var pushKey = Key(call, taskId);
                await EnsurePushCapacityAsync(pushKey, push.Id, ct).ConfigureAwait(false);
                await SavePushConfigAsync(pushKey, push, ct).ConfigureAwait(false);
            }
        }

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
        return new Prepared(context, existing is null ? push : null);
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

    public async Task<AgentTask> GetTaskAsync(GetTaskRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.GetTask, request, request.Id, cancellationToken).ConfigureAwait(false);
        A2AValidator.RequireId(request.Id, "id");
        if (request.HistoryLength is < 0) throw A2AException.InvalidParams("historyLength must be >= 0");
        var record = await _tasks.GetAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        return Shape(record.Task, request.HistoryLength);
    }

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

    public async Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.CancelTask, request, request.Id, cancellationToken).ConfigureAwait(false);
        A2AValidator.RequireId(request.Id, "id");
        var record = await _tasks.GetAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        if (record.Task.Status.State.IsTerminal()) throw NotCancelable(request.Id);

        var running = _running.TryGetValue(ScopeKey(call.Tenant, request.Id), out var set) ? set.Keys.ToList() : [];
        foreach (var r in running) r.Cancel();

        var snapshot = record.Task;
        var exec = StartRaw(snapshot.Id, snapshot.ContextId, isNewTask: false, userMessage: null, call, push: null, emitInitialSnapshot: false,
            (executor, updater, ct) => executor.CancelAsync(snapshot, updater, ct));
        exec.Detach();

        var waitAll = Task.WhenAll(running.Select(r => r.Completion).Append(exec.Completion));
        await Task.WhenAny(waitAll, Task.Delay(_options.CancelGracePeriod, cancellationToken)).ConfigureAwait(false);

        record = await _tasks.GetAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        if (record.Task.Status.State != TaskState.Canceled) throw NotCancelable(request.Id);
        return Shape(record.Task, null);
    }

    public async IAsyncEnumerable<SequencedEvent> SubscribeToTaskAsync(SubscribeToTaskRequest request, ServerCallContext call,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        await AuthorizeAsync(call, A2AOperation.SubscribeToTask, request, request.Id, cancellationToken).ConfigureAwait(false);
        RequireStreaming();
        A2AValidator.RequireId(request.Id, "id");
        var record = await _tasks.GetAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        if (record.Task.Status.State.IsTerminal())
            throw new A2AException(A2AErrorCode.UnsupportedOperation, "Cannot subscribe to a task in a terminal state.") { Metadata = new() { ["taskId"] = request.Id } };

        using var sub = _bus.Subscribe(ScopeKey(call.Tenant, request.Id));
        // Snapshot after subscribing, so no event can fall between the snapshot and the live feed (spec §3.1.6).
        // Read under the task lock so the snapshot and its sequence belong together; live events at or below it are duplicates.
        long snapshotSequence;
        var gate = LockFor(call.Tenant, request.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            record = await _tasks.GetAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
            snapshotSequence = await _events.LastSequenceAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
        yield return new SequencedEvent(snapshotSequence, StreamResponse.Of(Shape(record.Task, null)));
        if (record.Task.Status.State.IsTerminal()) yield break;

        await foreach (var sequenced in sub.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (sequenced.Sequence <= snapshotSequence) continue;
            yield return sequenced;
            if (sequenced.Event is StatusUpdateEvent { StatusUpdate.Status.State: var st } && st.IsTerminal()) yield break;
        }
    }

    // =====================================================================
    // Push notification configs
    // =====================================================================

    public async Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(config.Tenant);
        await AuthorizeAsync(call, A2AOperation.CreateTaskPushNotificationConfig, config, config.TaskId, cancellationToken).ConfigureAwait(false);
        RequirePush();
        A2AValidator.ValidatePushConfig(config, "config", requireTaskId: true);
        PushUrlValidator.Validate(config.Url, _options.PushNotifications);
        var key = Key(call, config.TaskId!);
        _ = await _tasks.GetAsync(key, cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(config.TaskId!);
        var stored = A2AJson.Clone(config);
        stored.Id = string.IsNullOrEmpty(stored.Id) ? TaskIds.New() : stored.Id;
        stored.Tenant = call.Tenant;
        await EnsurePushCapacityAsync(key, stored.Id, cancellationToken).ConfigureAwait(false);
        await SavePushConfigAsync(key, stored, cancellationToken).ConfigureAwait(false);
        return Redact(stored);
    }

    public async Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
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

    public async Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
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

    public async Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
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

    // =====================================================================
    // Execution engine
    // =====================================================================

    private Execution StartExecution(AgentRequestContext context, TaskPushNotificationConfig? push) =>
        StartRaw(context.TaskId, context.ContextId, context.IsNewTask, context.Message, context.Call, push, emitInitialSnapshot: !context.IsNewTask,
            (executor, updater, ct) => executor.ExecuteAsync(context, updater, ct));

    private Execution StartRaw(string taskId, string contextId, bool isNewTask, Message? userMessage, ServerCallContext call,
        TaskPushNotificationConfig? push, bool emitInitialSnapshot, Func<IAgentExecutor, TaskUpdater, CancellationToken, Task> run)
    {
        var exec = new Execution(taskId, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
        var scope = ScopeKey(call.Tenant, taskId);
        _running.GetOrAdd(scope, _ => new())[exec] = 0;
        var updater = new TaskUpdater(taskId, contextId, isNewTask, exec.Incoming.Writer);

        var producer = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var executor = scope.ServiceProvider.GetRequiredService<IAgentExecutor>();
                await run(executor, updater, exec.Cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (exec.Cts.IsCancellationRequested)
            {
                await TryFinishAsync(updater, TaskState.Canceled, null).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent executor failed for task {TaskId}", taskId);
                await TryFinishAsync(updater, TaskState.Failed, "The agent failed to process the request.").ConfigureAwait(false);
            }
            finally
            {
                exec.Incoming.Writer.TryComplete();
            }
        });

        // Background writes outlive the request, so fix the owner scope now.
        var key = Key(call, taskId);
        var consumer = Task.Run(() => ConsumeAsync(exec, key, contextId, isNewTask, userMessage, push, emitInitialSnapshot));
        _ = Task.WhenAll(producer, consumer).ContinueWith(_t =>
        {
            if (_running.TryGetValue(scope, out var set))
            {
                set.TryRemove(exec, out _);
                if (set.IsEmpty) _running.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Execution, byte>>(scope, set));
            }
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

    private async Task ConsumeAsync(Execution exec, TaskKey key, string contextId, bool isNewTask, Message? userMessage,
        TaskPushNotificationConfig? push, bool emitInitialSnapshot)
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

            await foreach (var evt in exec.Incoming.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (evt is MessageEvent)
                {
                    exec.Emit(new SequencedEvent(0, evt)); // direct reply, no task, not stored
                    continue;
                }

                var toPublish = new List<SequencedEvent>(2);
                var gate = LockFor(key.Tenant, taskId);
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
                                History = userMessage is null ? null : [WithTask(userMessage, taskId)],
                            },
                            Version: 0, CreatedAt: now, UpdatedAt: now)).ConfigureAwait(false);
                        if (push is not null) await SavePushConfigAsync(key, push, CancellationToken.None).ConfigureAwait(false);
                        created = true;
                        var snapshot = StreamResponse.Of(A2AJson.Clone(record.Task));
                        toPublish.Add(new SequencedEvent(await _events.AppendAsync(key, snapshot).ConfigureAwait(false), snapshot));
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

                    if (await ApplyAndStoreAsync(key, record, evt).ConfigureAwait(false) is { } stored) toPublish.Add(stored);
                }
                finally
                {
                    gate.Release();
                }

                // After the lock: bus, push and the initiating request, in sequence order.
                foreach (var sequenced in toPublish) Publish(exec, key, sequenced);
                if (evt is StatusUpdateEvent { StatusUpdate.Status.State: var st } && st.IsTerminal() && toPublish.Count > 0 && ReferenceEquals(toPublish[^1].Event, evt))
                {
                    await _events.MarkTerminalAsync(key).ConfigureAwait(false);
                    _bus.Complete(scope);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply events for task {TaskId}", taskId);
            exec.Cancel();
        }
        finally
        {
            exec.Outgoing.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Applies the event, appends it to the event store, then stores the task (the caller holds the task lock). After a
    /// version conflict, reloads and re-applies, up to <see cref="MaxConflictRetries"/> times; the event is appended only once.
    /// Returns null when the event was dropped (for example the task is already terminal).
    /// </summary>
    private async Task<SequencedEvent?> ApplyAndStoreAsync(TaskKey key, TaskRecord record, StreamResponse evt)
    {
        long sequence = 0;
        for (var attempt = 0; ; attempt++)
        {
            if (!Apply(record.Task, evt))
            {
                _logger.LogDebug("Dropped event for task {TaskId} in state {State}", key.TaskId, record.Task.Status.State);
                return null;
            }
            if (sequence == 0) sequence = await _events.AppendAsync(key, evt).ConfigureAwait(false);
            try
            {
                await _tasks.UpdateAsync(record, record.Version).ConfigureAwait(false);
                return new SequencedEvent(sequence, evt);
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

    private void Publish(Execution exec, TaskKey key, SequencedEvent evt)
    {
        exec.Emit(evt);
        _bus.Publish(ScopeKey(key.Tenant, key.TaskId), evt);
        _push.Enqueue(key, evt);
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

    private static Message WithTask(Message m, string taskId)
    {
        var copy = A2AJson.Clone(m);
        copy.TaskId = taskId;
        return copy;
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    /// <summary>Throws InvalidRequest when adding config <paramref name="configId"/> would exceed the per-task limit. Replacing an existing id is allowed.</summary>
    private async ValueTask EnsurePushCapacityAsync(TaskKey key, string configId, CancellationToken ct)
    {
        if (await _pushConfigs.GetAsync(key, configId, ct).ConfigureAwait(false) is not null) return;
        var limit = _options.Limits.MaxPushConfigsPerTask;
        if (await _pushConfigs.CountAsync(key, ct).ConfigureAwait(false) >= limit)
            throw new A2AException(A2AErrorCode.InvalidRequest, $"Too many push notification configs for this task (max {limit}).");
    }

    /// <summary>Protects the secrets of <paramref name="config"/> and stores a copy. The caller's instance is not changed.</summary>
    private ValueTask SavePushConfigAsync(TaskKey key, TaskPushNotificationConfig config, CancellationToken ct)
    {
        var copy = A2AJson.Clone(config);
        if (!string.IsNullOrEmpty(copy.Token)) copy.Token = _protector.Protect(copy.Token);
        if (copy.Authentication is { Credentials.Length: > 0 } auth) auth.Credentials = _protector.Protect(auth.Credentials);
        return _pushConfigs.SaveAsync(new PushConfigRecord(key, copy), ct);
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
            var all = _running.Values.SelectMany(s => s.Keys).Select(e => e.Completion).Where(t => t is not null).ToList();
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

        /// <summary>The initiating request stopped listening; drop further outgoing events.</summary>
        public void Detach()
        {
            _detached = true;
            while (Outgoing.Reader.TryRead(out _)) { }
        }
    }
}
