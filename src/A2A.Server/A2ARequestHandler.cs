using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using A2A.Json;
using A2A.Server.Push;
using A2A.Server.Storage;
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
    private readonly IPushNotificationConfigStore _pushConfigs;
    private readonly IPushNotificationSender _push;
    private readonly ITaskAccessPolicy _access; // unused until the store scopes by owner end to end (removed with the policy)
    private readonly TaskEventBus _bus;
    private readonly A2AServerOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Execution, byte>> _running = new(StringComparer.Ordinal);

    public A2ARequestHandler(
        IServiceScopeFactory scopeFactory,
        TaskStore tasks,
        IPushNotificationConfigStore pushConfigs,
        IPushNotificationSender push,
        ITaskAccessPolicy access,
        TaskEventBus bus,
        IOptions<A2AServerOptions> options,
        ILogger<A2ARequestHandler>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _tasks = tasks;
        _pushConfigs = pushConfigs;
        _push = push;
        _access = access;
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

        if (_options.RequireAuthenticatedCaller && !call.IsAuthenticated)
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
        var prepared = await PrepareAsync(request, call, cancellationToken).ConfigureAwait(false);
        var exec = StartExecution(prepared.Context, prepared.PushConfig);
        var returnImmediately = request.Configuration?.ReturnImmediately == true;
        try
        {
            await foreach (var evt in exec.Outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (evt.Message is not null) return new SendMessageResponse { Message = evt.Message };
                if (returnImmediately) break;
                if (evt.StatusUpdate is { } su && (su.Status.State.IsTerminal() || su.Status.State.IsInterrupted())) break;
            }
        }
        finally
        {
            exec.Detach();
        }

        var record = await _tasks.GetAsync(Key(call, prepared.Context.TaskId), cancellationToken).ConfigureAwait(false)
            ?? throw new A2AException(A2AErrorCode.InvalidAgentResponse, "The agent finished without replying or creating a task.");
        return new SendMessageResponse { Task = Shape(record.Task, request.Configuration?.HistoryLength) };
    }

    public async IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, ServerCallContext call,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RequireStreaming();
        var prepared = await PrepareAsync(request, call, cancellationToken).ConfigureAwait(false);
        var exec = StartExecution(prepared.Context, prepared.PushConfig);
        var any = false;
        try
        {
            await foreach (var evt in exec.Outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                any = true;
                yield return evt;
                if (evt.Message is not null) yield break;
                if (evt.StatusUpdate?.Status.State.IsTerminal() == true) yield break;
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
        var message = request.Message;
        AgentTask? existing = null;
        string taskId, contextId;

        if (!string.IsNullOrEmpty(message.TaskId))
        {
            taskId = message.TaskId;
            var gate = LockFor(taskId);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var record = await _tasks.GetAsync(Key(call, taskId), ct).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(taskId);
                if (!string.IsNullOrEmpty(message.ContextId) && message.ContextId != record.Task.ContextId)
                    throw A2AException.InvalidParams("message.contextId does not match the task's contextId");
                if (record.Task.Status.State.IsTerminal())
                    throw new A2AException(A2AErrorCode.UnsupportedOperation, "The task is in a terminal state and cannot accept further messages.")
                    {
                        Metadata = new() { ["taskId"] = taskId },
                    };
                contextId = record.Task.ContextId;
                message.ContextId = contextId;
                (record.Task.History ??= []).Add(message);
                await _tasks.UpdateAsync(record, record.Version, ct).ConfigureAwait(false);
                existing = record.Task;
            }
            finally
            {
                gate.Release();
            }
        }
        else
        {
            taskId = TaskIds.New();
            contextId = string.IsNullOrEmpty(message.ContextId) ? Guid.NewGuid().ToString() : message.ContextId;
            message.ContextId = contextId;
        }

        TaskPushNotificationConfig? push = null;
        if (request.Configuration?.TaskPushNotificationConfig is { } p)
        {
            RequirePush();
            PushUrlValidator.Validate(p.Url, _options.PushNotifications);
            push = A2AJson.Clone(p);
            push.TaskId = taskId;
            push.Id = string.IsNullOrEmpty(push.Id) ? Guid.NewGuid().ToString() : push.Id;
            push.Tenant = call.Tenant;
            if (existing is not null) await _pushConfigs.SaveAsync(push, ct).ConfigureAwait(false);
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

    // =====================================================================
    // Tasks
    // =====================================================================

    public async Task<AgentTask> GetTaskAsync(GetTaskRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        A2AValidator.RequireId(request.Id, "id");
        if (request.HistoryLength is < 0) throw A2AException.InvalidParams("historyLength must be >= 0");
        var record = await _tasks.GetAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        return Shape(record.Task, request.HistoryLength);
    }

    public async Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        if (request.PageSize is < 1) throw A2AException.InvalidParams("pageSize must be between 1 and 100");
        if (request.HistoryLength is < 0) throw A2AException.InvalidParams("historyLength must be >= 0");
        var pageSize = Math.Min(request.PageSize ?? 50, _options.MaxPageSize);

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
        A2AValidator.RequireId(request.Id, "id");
        var record = await _tasks.GetAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        if (record.Task.Status.State.IsTerminal()) throw NotCancelable(request.Id);

        var running = _running.TryGetValue(request.Id, out var set) ? set.Keys.ToList() : [];
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

    public async IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, ServerCallContext call,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        RequireStreaming();
        A2AValidator.RequireId(request.Id, "id");
        var record = await _tasks.GetAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        if (record.Task.Status.State.IsTerminal())
            throw new A2AException(A2AErrorCode.UnsupportedOperation, "Cannot subscribe to a task in a terminal state.") { Metadata = new() { ["taskId"] = request.Id } };

        using var sub = _bus.Subscribe(request.Id);
        // Snapshot after subscribing, so no event can fall between the snapshot and the live feed (spec §3.1.6).
        record = await _tasks.GetAsync(Key(call, request.Id), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.Id);
        yield return StreamResponse.Of(Shape(record.Task, null));
        if (record.Task.Status.State.IsTerminal()) yield break;

        await foreach (var evt in sub.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
            if (evt.StatusUpdate?.Status.State.IsTerminal() == true) yield break;
        }
    }

    // =====================================================================
    // Push notification configs
    // =====================================================================

    public async Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(config.Tenant);
        RequirePush();
        A2AValidator.ValidatePushConfig(config, "config", requireTaskId: true);
        PushUrlValidator.Validate(config.Url, _options.PushNotifications);
        _ = await _tasks.GetAsync(Key(call, config.TaskId!), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(config.TaskId!);
        var stored = A2AJson.Clone(config);
        stored.Id = string.IsNullOrEmpty(stored.Id) ? Guid.NewGuid().ToString() : stored.Id;
        stored.Tenant = call.Tenant;
        await _pushConfigs.SaveAsync(stored, cancellationToken).ConfigureAwait(false);
        return stored;
    }

    public async Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        RequirePush();
        A2AValidator.RequireId(request.TaskId, "taskId");
        A2AValidator.RequireId(request.Id, "id");
        _ = await _tasks.GetAsync(Key(call, request.TaskId), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.TaskId);
        return await _pushConfigs.GetAsync(request.TaskId, request.Id, cancellationToken).ConfigureAwait(false)
            ?? throw ConfigNotFound(request.TaskId, request.Id);
    }

    public async Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
        RequirePush();
        A2AValidator.RequireId(request.TaskId, "taskId");
        _ = await _tasks.GetAsync(Key(call, request.TaskId), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.TaskId);
        var all = await _pushConfigs.ListAsync(request.TaskId, cancellationToken).ConfigureAwait(false);

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
        RequirePush();
        A2AValidator.RequireId(request.TaskId, "taskId");
        A2AValidator.RequireId(request.Id, "id");
        _ = await _tasks.GetAsync(Key(call, request.TaskId), cancellationToken).ConfigureAwait(false) ?? throw A2AException.TaskNotFound(request.TaskId);
        if (!await _pushConfigs.DeleteAsync(request.TaskId, request.Id, cancellationToken).ConfigureAwait(false))
            throw ConfigNotFound(request.TaskId, request.Id);
    }

    // =====================================================================
    // Extended Agent Card
    // =====================================================================

    public async Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        call.BindTenant(request.Tenant);
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
        _running.GetOrAdd(taskId, _ => new())[exec] = 0;
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
            if (_running.TryGetValue(taskId, out var set))
            {
                set.TryRemove(exec, out _);
                if (set.IsEmpty) _running.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Execution, byte>>(taskId, set));
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
        var created = !isNewTask;
        try
        {
            if (emitInitialSnapshot)
            {
                var current = await _tasks.GetAsync(key).ConfigureAwait(false);
                if (current is not null) exec.Emit(StreamResponse.Of(current.Task));
            }

            await foreach (var evt in exec.Incoming.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (evt.Message is not null)
                {
                    exec.Emit(evt); // direct reply, no task
                    continue;
                }

                var gate = LockFor(taskId);
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
                                Status = new AgentTaskStatus { State = TaskState.Submitted, Timestamp = evt.StatusUpdate?.Status.Timestamp ?? now },
                                History = userMessage is null ? null : [WithTask(userMessage, taskId)],
                            },
                            Version: 0, CreatedAt: now, UpdatedAt: now)).ConfigureAwait(false);
                        if (push is not null) await _pushConfigs.SaveAsync(push).ConfigureAwait(false);
                        created = true;
                        Publish(exec, taskId, StreamResponse.Of(A2AJson.Clone(record.Task)));
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

                    if (!await ApplyAndStoreAsync(key, record, evt).ConfigureAwait(false)) continue;
                }
                finally
                {
                    gate.Release();
                }

                Publish(exec, taskId, evt);
                if (evt.StatusUpdate?.Status.State.IsTerminal() == true) _bus.Complete(taskId);
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

    /// <summary>Applies the event and stores the result. After a version conflict, reloads once and re-applies. Returns false when the event was dropped.</summary>
    private async Task<bool> ApplyAndStoreAsync(TaskKey key, TaskRecord record, StreamResponse evt)
    {
        if (!Apply(record.Task, evt))
        {
            _logger.LogDebug("Dropped event for task {TaskId} in state {State}", key.TaskId, record.Task.Status.State);
            return false;
        }
        try
        {
            await _tasks.UpdateAsync(record, record.Version).ConfigureAwait(false);
            return true;
        }
        catch (ConcurrencyException)
        {
            var fresh = await _tasks.GetAsync(key).ConfigureAwait(false);
            if (fresh is null)
            {
                _logger.LogWarning("Task {TaskId} disappeared from the store; dropping event", key.TaskId);
                return false;
            }
            if (!Apply(fresh.Task, evt))
            {
                _logger.LogDebug("Dropped event for task {TaskId} in state {State}", key.TaskId, fresh.Task.Status.State);
                return false;
            }
            await _tasks.UpdateAsync(fresh, fresh.Version).ConfigureAwait(false);
            return true;
        }
    }

    private void Publish(Execution exec, string taskId, StreamResponse evt)
    {
        exec.Emit(evt);
        _bus.Publish(taskId, evt);
        _push.Enqueue(taskId, evt);
    }

    /// <summary>Applies an event to the task. Returns false if the event must be dropped (task already terminal).</summary>
    private static bool Apply(AgentTask task, StreamResponse evt)
    {
        if (task.Status.State.IsTerminal()) return false;
        if (evt.StatusUpdate is { } su)
        {
            su.Status.Timestamp ??= DateTimeOffset.UtcNow;
            task.Status = A2AJson.Clone(su.Status);
            if (su.Status.Message is { } m) (task.History ??= []).Add(A2AJson.Clone(m));
            return true;
        }
        if (evt.ArtifactUpdate is { } au)
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

    private static TaskKey Key(ServerCallContext call, string taskId) => new(call.Tenant, call.Principal, taskId);

    /// <summary>Applies historyLength (0 = none, n = last n) and artifact inclusion to a copy of the task.</summary>
    internal static AgentTask Shape(AgentTask task, int? historyLength, bool includeArtifacts = true)
    {
        var copy = A2AJson.Clone(task);
        if (historyLength is 0) copy.History = null;
        else if (historyLength is int n && copy.History is { } h && h.Count > n) copy.History = h.GetRange(h.Count - n, n);
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

    private SemaphoreSlim LockFor(string taskId) =>
        _locks[(StringComparer.Ordinal.GetHashCode(taskId) & 0x7fffffff) % _locks.Length];

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
        public Channel<StreamResponse> Outgoing { get; } = Channel.CreateUnbounded<StreamResponse>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
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

        public void Emit(StreamResponse evt)
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
