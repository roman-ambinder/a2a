using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using A2A.Client.Push;
using A2A.Client.Resilience;
using A2A.Client.Streaming;
using A2A.Skills;
using A2A.Validation;

namespace A2A.Client;

/// <summary>
///     High-level A2A client. Wraps a transport, fills in the tenant, checks declared capabilities,
///     and adds convenience overloads.
/// </summary>
/// <remarks>
///     Every operation is a span of the <c>A2A.Client</c> <see cref="System.Diagnostics.ActivitySource" /> (see
///     <see cref="A2A.Diagnostics.A2ATelemetry" />), covering retries and stream reconnects; stream events are span
///     events. The
///     transports send the current span as a W3C <c>traceparent</c> header (gRPC metadata), so server spans join the
///     trace.
///     With <see cref="A2AClientOptions.CircuitBreaker" /> set, the client also stops calling a peer that keeps failing.
///     Only the
///     transport call counts; checks before it and <see cref="A2AClientOptions.OnMessageReceived" /> do not.
/// </remarks>
public sealed class A2AClient : IA2AClient, IA2AExtensionOperationClient
{
    private readonly CircuitBreaker? _breaker;
    private readonly A2AClientOptions _options;
    private readonly IA2AClient _transport;
    private volatile bool _extendedCardLoaded;

    /// <summary>
    ///     Creates a client over <paramref name="transport" />. <paramref name="options" /> defaults to a new
    ///     <see cref="A2AClientOptions" />. <paramref name="agentCard" /> enables capability checks when given.
    /// </summary>
    public A2AClient(IA2AClient transport, A2AClientOptions? options = null, AgentCard? agentCard = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new A2AClientOptions();
        AgentCard = agentCard;
        TelemetryCard = agentCard;
        _breaker = _options.CircuitBreaker is { } breaker
            ? new CircuitBreaker(breaker, _options.TimeProvider, agentCard?.Name)
            : null;
        TelemetryBinding = (transport as ITransportBinding)?.Binding;
    }

    /// <summary>The card this client was created from, if any.</summary>
    public AgentCard? AgentCard { get; private set; }

    /// <summary>
    ///     The card the client was created with: spans tag it, never the extended card a refresh may bring (for
    ///     authenticated callers only).
    /// </summary>
    internal AgentCard? TelemetryCard { get; }

    /// <summary>The binding name for the client's spans (null when the transport names none).</summary>
    internal string? TelemetryBinding { get; }

    /// <summary>The protocol version the client speaks, for the client's spans.</summary>
    internal string TelemetryProtocolVersion => _options.ProtocolVersion;

    // ---- IA2AClient ----

    /// <inheritdoc />
    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureMessageId(request);
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        if (request.Configuration?.TaskPushNotificationConfig is { } push)
        {
            RequireCapability(AgentCard?.Capabilities.PushNotifications, A2AErrorCode.PushNotificationNotSupported);
            RequireHttpsWebhook(push.Url, "configuration.taskPushNotificationConfig.url");
        }

        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.SendMessage, this, request.Tenant,
            request.Message?.TaskId, () =>
            {
                var call = Checked(Guard(() => _transport.SendMessageAsync(request, options, cancellationToken)), A2AValidator.ValidateResponse);
                return _options.OnMessageReceived is null ? call : InspectAsync(call, cancellationToken);
            }, cancellationToken, request.Message);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureMessageId(request);
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.Streaming, A2AErrorCode.UnsupportedOperation,
            "Agent does not declare streaming support.");
        return ClientTelemetry.TraceStream(A2AProtocol.Methods.SendStreamingMessage, this, request.Tenant,
            request.Message?.TaskId, Inspect(Checked(Guard(ResumableStream.RunAsync(
                null,
                ct => _transport is IResumableTransport rt
                    ? rt.SendStreamingFramesAsync(request, options, ct)
                    : Frames(_transport.SendStreamingMessageAsync(request, options, ct), ct),
                (taskId, cursor, ct) => Resubscribe(request.Tenant, taskId, cursor, options, ct),
                (taskId, ct) => GetTaskForResume(request.Tenant, taskId, options, ct),
                _options.TimeProvider, cancellationToken)))), request.Message);
    }

    /// <inheritdoc />
    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.GetTask, this, request.Tenant, request.Id, () =>
        {
            var call = Checked(Guard(() => _transport.GetTaskAsync(request, options, cancellationToken)), A2AValidator.ValidateResponse);
            return _options.OnMessageReceived is null ? call : InspectAsync(call, cancellationToken);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Tenant ??= _options.Tenant;
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.ListTasks, this, request.Tenant, null,
            () => Checked(Guard(() => _transport.ListTasksAsync(request, options, cancellationToken)), ValidateTasks), cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.CancelTask, this, request.Tenant, request.Id, () =>
        {
            var call = Checked(Guard(() => CancelOrCanceledAsync(request, options, cancellationToken)), A2AValidator.ValidateResponse);
            return _options.OnMessageReceived is null ? call : InspectAsync(call, cancellationToken);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.Streaming, A2AErrorCode.UnsupportedOperation,
            "Agent does not declare streaming support.");
        return ClientTelemetry.TraceStream(A2AProtocol.Methods.SubscribeToTask, this, request.Tenant, request.Id,
            Inspect(Checked(Guard(_transport.SubscribeToTaskAsync(request, options, cancellationToken)))));
    }

    /// <inheritdoc />
    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        A2AValidator.ValidateOutgoing(config);
        RequireHttpsWebhook(config.Url, "config.url");
        config.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.PushNotifications, A2AErrorCode.PushNotificationNotSupported);
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.CreateTaskPushNotificationConfig, this, config.Tenant,
            config.TaskId,
            () => Guard(() => _transport.CreateTaskPushNotificationConfigAsync(config, options, cancellationToken)),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(
        GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.GetTaskPushNotificationConfig, this, request.Tenant,
            request.TaskId,
            () => Guard(() => _transport.GetTaskPushNotificationConfigAsync(request, options, cancellationToken)),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(
        ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.ListTaskPushNotificationConfigs, this, request.Tenant,
            request.TaskId,
            () => Guard(() => _transport.ListTaskPushNotificationConfigsAsync(request, options, cancellationToken)),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.DeleteTaskPushNotificationConfig, this, request.Tenant,
            request.TaskId,
            () => Guard(() => _transport.DeleteTaskPushNotificationConfigAsync(request, options, cancellationToken)),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.ExtendedAgentCard, A2AErrorCode.UnsupportedOperation,
            "Agent does not declare an extended agent card.");
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.GetExtendedAgentCard, this, request.Tenant, null,
            () => Guard(() => _transport.GetExtendedAgentCardAsync(request, options, cancellationToken)),
            cancellationToken);
    }

    /// <summary>
    ///     Calls a custom operation (see <see cref="IA2AExtensionOperationClient" />). It is a span of the client, named by
    ///     the method.
    ///     Use <see cref="ExtensionOperationClientExtensions" /> to activate the extension of the operation too.
    /// </summary>
    /// <exception cref="A2AException">UnsupportedOperation when the transport cannot call custom operations.</exception>
    public Task<JsonElement> InvokeExtensionOperationAsync(string method, JsonElement parameters,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ExtensionOperationParams.RequireMethod(method);
        if (_transport is not IA2AExtensionOperationClient transport)
            throw new A2AException(A2AErrorCode.UnsupportedOperation,
                "The transport of this client does not support custom operations.");
        // As the other operations: this client's tenant applies, also when the transport has other options. Params that are not
        // an object are left to the transport, because HTTP+JSON can still carry the tenant in the path.
        parameters = ExtensionOperationParams.Normalize(parameters);
        if (parameters.ValueKind == JsonValueKind.Object)
            parameters = ExtensionOperationParams.WithTenant(parameters, _options.Tenant);
        return ClientTelemetry.TraceAsync(method, this,
            ExtensionOperationParams.TenantOf(parameters) ?? _options.Tenant, null,
            () => Guard(() => transport.InvokeExtensionOperationAsync(method, parameters, options, cancellationToken)),
            cancellationToken);
    }

    // ---- Convenience ----

    /// <summary>
    ///     Sends a text message. Pass <paramref name="taskId" />/<paramref name="contextId" /> to continue a
    ///     conversation.
    /// </summary>
    public Task<SendMessageResponse> SendMessageAsync(string text, string? contextId = null, string? taskId = null,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        return SendMessageAsync(new SendMessageRequest { Message = Message.FromUser(text, contextId, taskId) }, options,
            cancellationToken);
    }

    /// <summary>Sends <paramref name="message" /> and waits for the result (spec §3.1.1).</summary>
    public Task<SendMessageResponse> SendMessageAsync(Message message, SendMessageConfiguration? configuration = null,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        return SendMessageAsync(new SendMessageRequest { Message = message, Configuration = configuration }, options,
            cancellationToken);
    }

    /// <summary>Sends <paramref name="message" /> and streams the updates (spec §3.1.2).</summary>
    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(Message message,
        SendMessageConfiguration? configuration = null, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return SendStreamingMessageAsync(new SendMessageRequest { Message = message, Configuration = configuration },
            options, cancellationToken);
    }

    /// <summary>
    ///     Fetches a task (spec §3.1.3). <paramref name="historyLength" /> limits the history; null means the server
    ///     default.
    /// </summary>
    public Task<AgentTask> GetTaskAsync(string taskId, int? historyLength = null, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return GetTaskAsync(new GetTaskRequest { Id = taskId, HistoryLength = historyLength }, options,
            cancellationToken);
    }

    /// <summary>Asks the agent to cancel a task (spec §3.1.5).</summary>
    public Task<AgentTask> CancelTaskAsync(string taskId, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return CancelTaskAsync(new CancelTaskRequest { Id = taskId }, options, cancellationToken);
    }

    /// <summary>Streams updates of an existing task (spec §3.1.6).</summary>
    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(string taskId, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId }, options, cancellationToken);
    }

    /// <summary>
    ///     Polls a task until it reaches a terminal or interrupted state. Use when streaming and push are not available,
    ///     or to recover after a dropped stream.
    /// </summary>
    public async Task<AgentTask> WaitForTaskAsync(string taskId, TimeSpan? pollInterval = null,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var delay = pollInterval ?? TimeSpan.FromSeconds(1);
        while (true)
        {
            var task = await GetTaskAsync(taskId, 0, options, cancellationToken).ConfigureAwait(false);
            if (task.Status.State.IsTerminal() || task.Status.State.IsInterrupted()) return task;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Sends <paramref name="text" /> and returns the agent's answer as text: the text of a direct reply, or the text of
    ///     the task's artifacts once the task has completed. A task that is still running when the call returns is polled
    ///     with <see cref="WaitForTaskAsync" />.
    /// </summary>
    /// <param name="text">The message text.</param>
    /// <param name="contextId">The context (conversation) to continue, or null for a new one.</param>
    /// <param name="options">Options for the call.</param>
    /// <param name="cancellationToken">Cancels the call and the polling.</param>
    /// <exception cref="TaskNotCompletedException">
    ///     The task ended in a state other than completed (failed, rejected, canceled), or the agent needs input or
    ///     authorization.
    /// </exception>
    public async Task<string> AskAsync(string text, string? contextId = null, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var response = await SendMessageAsync(text, contextId, options: options, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        switch (response)
        {
            case MessageResponse { Message: var reply }:
                return reply.GetText();
            case TaskResponse { Task: var task }:
                if (!task.Status.State.IsTerminal() && !task.Status.State.IsInterrupted())
                    task = await WaitForTaskAsync(task.Id, options: options, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                if (task.Status.State != TaskState.Completed) throw new TaskNotCompletedException(task);
                return task.GetText();
            default:
                throw new A2AException(A2AErrorCode.InvalidAgentResponse, "The agent sent no result.");
        }
    }

    /// <summary>
    ///     Sends <paramref name="text" /> as a streaming message and yields the agent's text as it arrives: each chunk of an
    ///     artifact, and the text of a direct reply. Status updates without text are skipped.
    /// </summary>
    /// <param name="text">The message text.</param>
    /// <param name="contextId">The context (conversation) to continue, or null for a new one.</param>
    /// <param name="options">Options for the call.</param>
    /// <param name="cancellationToken">Cancels the stream.</param>
    /// <remarks>
    ///     A task snapshot in the stream yields the text of the artifacts not seen before, so text is not repeated. The agent
    ///     must declare streaming on its card (<see cref="AgentCapabilities.Streaming" />). The enumeration ends when the task
    ///     reaches a terminal state or needs input or authorization, even when the server keeps the stream open; the task
    ///     itself goes on.
    /// </remarks>
    /// <exception cref="TaskNotCompletedException">
    ///     After the last chunk: the task ended in a state other than completed, or the agent needs input or authorization.
    /// </exception>
    public async IAsyncEnumerable<string> StreamTextAsync(string text, string? contextId = null,
        A2ARequestOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var seenArtifacts = new HashSet<string>(StringComparer.Ordinal);
        AgentTask? ended = null;
        await foreach (var evt in SendStreamingMessageAsync(Message.FromUser(text, contextId), options: options,
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            switch (evt)
            {
                case TaskEvent { Task: var task }:
                    foreach (var artifact in task.Artifacts ?? [])
                        if (seenArtifacts.Add(artifact.ArtifactId) && artifact.GetText() is { Length: > 0 } t)
                            yield return t;
                    break;
                case ArtifactUpdateEvent { ArtifactUpdate.Artifact: var artifact }:
                    seenArtifacts.Add(artifact.ArtifactId);
                    break;
            }

            if (evt.GetText() is { Length: > 0 } chunk) yield return chunk;
            if (evt.State is not { } state || (!state.IsTerminal() && !state.IsInterrupted())) continue;
            if (state == TaskState.Completed) yield break;
            // A server can keep the stream open while the task waits for input; leaving it does not stop the task.
            ended = evt is TaskEvent { Task: var snapshot }
                ? snapshot
                : new AgentTask
                {
                    Id = evt.TaskId ?? string.Empty,
                    ContextId = ((StatusUpdateEvent)evt).StatusUpdate.ContextId,
                    Status = ((StatusUpdateEvent)evt).StatusUpdate.Status
                };
            break;
        }

        if (ended is not null) throw new TaskNotCompletedException(ended);
    }

    /// <summary>
    ///     Registers a webhook for an existing task safely: it registers FIRST, then calls GetTask once. A task that finishes
    ///     after the
    ///     registration reaches the webhook; a task that finished before it is seen in the returned
    ///     <see cref="A2A.Client.Push.PushRegistration.Task" />
    ///     (check <see cref="A2A.Client.Push.PushRegistration.IsFinal" />), so the caller does not wait for a callback that
    ///     never comes.
    ///     Servers of other SDKs send no catch-up delivery, so this order matters. The webhook is a hint; the task is the
    ///     truth.
    /// </summary>
    /// <remarks>
    ///     If GetTask fails, the exception is thrown and the webhook stays registered. <paramref name="config" />.TaskId is
    ///     set to
    ///     <paramref name="taskId" /> (a different non-empty value is refused). When the config has neither a token nor
    ///     authentication, <c>Token</c> is set to a new <see cref="PushTokens.Create" /> (spec §13.2: unique, single-purpose
    ///     tokens). The returned <see cref="A2A.Client.Push.PushRegistration.Config" /> carries the token that was sent,
    ///     for the receiver, even when the server redacts it in its answer.
    /// </remarks>
    public async Task<PushRegistration> RegisterPushAsync(string taskId, TaskPushNotificationConfig config,
        int? historyLength = 0, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(taskId)) throw new ArgumentException("The task id is required.", nameof(taskId));
        ArgumentNullException.ThrowIfNull(config);
        if (!string.IsNullOrEmpty(config.TaskId) && config.TaskId != taskId)
            throw new ArgumentException("config.TaskId differs from taskId.", nameof(config));
        config.TaskId = taskId;
        if (config.Token is null && config.Authentication is null) config.Token = PushTokens.Create();
        var stored = WithSentToken(await CreateTaskPushNotificationConfigAsync(config, options, cancellationToken)
            .ConfigureAwait(false), config.Token);
        var task = await GetTaskAsync(taskId, historyLength, options, cancellationToken).ConfigureAwait(false);
        return new PushRegistration(stored, task);
    }

    /// <summary>
    ///     Replaces the token of a webhook registration (spec §13.2): registers <paramref name="existing" />'s URL and
    ///     authentication again with <paramref name="newToken" /> (default: a new <see cref="PushTokens.Create" />), and
    ///     only then deletes the old registration, so no notification is lost in between. Accept both tokens at the
    ///     receiver until this returns. Returns the new registration, with the new token (even when the server redacts it).
    /// </summary>
    /// <remarks>
    ///     If the delete fails, the exception is thrown and both registrations exist; the old one still uses the old token.
    ///     Delete it with <c>DeleteTaskPushNotificationConfigAsync</c>.
    /// </remarks>
    public async Task<TaskPushNotificationConfig> RotatePushTokenAsync(TaskPushNotificationConfig existing,
        string? newToken = null, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (string.IsNullOrEmpty(existing.Id)) throw new ArgumentException("The registration has no id.", nameof(existing));
        if (string.IsNullOrEmpty(existing.TaskId))
            throw new ArgumentException("The registration has no task id.", nameof(existing));
        var token = newToken ?? PushTokens.Create();
        var replacement = WithSentToken(await CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig
        {
            Tenant = existing.Tenant, TaskId = existing.TaskId, Url = existing.Url,
            Token = token, Authentication = existing.Authentication,
            ProtocolVersion = existing.ProtocolVersion
        }, options, cancellationToken).ConfigureAwait(false), token);
        await DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest
        {
            Tenant = existing.Tenant, TaskId = existing.TaskId!, Id = existing.Id!
        }, options, cancellationToken).ConfigureAwait(false);
        return replacement;
    }

    /// <summary>
    ///     The stored config with the token this client sent, when the server left it out of the answer (servers redact
    ///     tokens; the SDK's server answers with an empty one). The token is this client's own, so nothing leaks.
    /// </summary>
    private static TaskPushNotificationConfig WithSentToken(TaskPushNotificationConfig stored, string? sent)
    {
        if (!string.IsNullOrEmpty(sent) && string.IsNullOrEmpty(stored.Token)) stored.Token = sent;
        return stored;
    }

    /// <summary>
    ///     Refuses a webhook URL that is not https when <see cref="A2AClientOptions.RequireHttpsWebhooks" /> is set. Loopback
    ///     is allowed for local development.
    /// </summary>
    private void RequireHttpsWebhook(string? url, string field)
    {
        if (!_options.RequireHttpsWebhooks || string.IsNullOrWhiteSpace(url)) return;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback)) return;
        throw A2AException.InvalidField(field,
            "must be an https URL (spec 13.2), because A2AClientOptions.RequireHttpsWebhooks is set");
    }

    /// <summary>Pages through all tasks matching <paramref name="request" />.</summary>
    public async IAsyncEnumerable<AgentTask> ListAllTasksAsync(ListTasksRequest? request = null,
        A2ARequestOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        request ??= new ListTasksRequest();
        while (true)
        {
            var page = await ListTasksAsync(request, options, cancellationToken).ConfigureAwait(false);
            foreach (var t in page.Tasks) yield return t;
            if (string.IsNullOrEmpty(page.NextPageToken)) yield break;
            request = new ListTasksRequest
            {
                Tenant = request.Tenant, ContextId = request.ContextId, Status = request.Status,
                PageSize = request.PageSize,
                PageToken = page.NextPageToken, HistoryLength = request.HistoryLength,
                StatusTimestampAfter = request.StatusTimestampAfter, IncludeArtifacts = request.IncludeArtifacts
            };
        }
    }

    /// <summary>Fetches the extended card and makes it the current card for capability checks (spec §3.1.11).</summary>
    public async Task<AgentCard> RefreshWithExtendedAgentCardAsync(A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var card = await GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest(), options, cancellationToken)
            .ConfigureAwait(false);
        AgentCard = card;
        _extendedCardLoaded = true;
        return card;
    }

    /// <summary>
    ///     Selects a skill for one send: writes <paramref name="skillId" /> to the message metadata and requests the
    ///     skill-selection
    ///     extension. Pass <paramref name="message" /> and the returned options to a send method that takes a message or a
    ///     request. The returned
    ///     options must be passed, because they request the extension; without them the server ignores the selection.
    /// </summary>
    /// <param name="message">The message to send. It is changed and must be the one you send.</param>
    /// <param name="skillId">The <see cref="AgentSkill.Id" /> of the skill. Not empty.</param>
    /// <param name="options">Options for the call, or null for new ones. These options are changed and returned.</param>
    /// <returns>The options to pass with <paramref name="message" />.</returns>
    /// <remarks>
    ///     When <see cref="A2AClientOptions.ValidateCapabilities" /> is on and the card is known, a skill that is not on the
    ///     card fails here,
    ///     and nothing is sent. The check is skipped when the card declares an extended card that is not loaded yet
    ///     (<see cref="RefreshWithExtendedAgentCardAsync" />), because the public list is then incomplete. A plain A2A peer
    ///     ignores the
    ///     selection.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="skillId" /> is null or empty.</exception>
    /// <exception cref="A2AException">InvalidParams: the card does not list the skill.</exception>
    [Experimental("A2AEXP001",
        UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public A2ARequestOptions SelectSkill(Message message, string skillId, A2ARequestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(skillId);
        var extendedLoaded =
            _extendedCardLoaded; // read before the card: a refresh writes the card first, then the flag
        if (_options.ValidateCapabilities && AgentCard is { } card
                                          && (card.Capabilities.ExtendedAgentCard != true || extendedLoaded)
                                          && !(card.Skills?.Any(s =>
                                              string.Equals(s.Id, skillId, StringComparison.Ordinal)) ?? false))
            throw A2AException.InvalidParams($"The Agent Card has no skill '{skillId}'.");

        message.SetRequestedSkillId(skillId);
        options ??= new A2ARequestOptions();
        if (options.Extensions is null or { IsReadOnly: true })
            options.Extensions = new List<string>(options.Extensions ?? []);
        if (!options.Extensions.Contains(SkillSelection.ExtensionUri))
            options.Extensions.Add(SkillSelection.ExtensionUri);
        return options;
    }

    /// <summary>
    ///     Calls a typed skill: sends <paramref name="input" /> as one data part, waits for the task, and returns the data
    ///     part of the
    ///     last artifact that has one. A direct message reply with a data part is read the same way, for agents of other SDKs.
    /// </summary>
    /// <typeparam name="TInput">The input type.</typeparam>
    /// <typeparam name="TOutput">The output type.</typeparam>
    /// <param name="skillId">The <see cref="AgentSkill.Id" /> of the skill. Not empty.</param>
    /// <param name="input">The input. Not null.</param>
    /// <param name="inputType">Type information for <typeparamref name="TInput" />, from a source-generated JSON context.</param>
    /// <param name="outputType">Type information for <typeparamref name="TOutput" />, from a source-generated JSON context.</param>
    /// <param name="options">Options for the call, or null for new ones. These options are changed.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    ///     It uses <see cref="SelectSkill" />, so the same card check applies. The server refuses input that does not fit the
    ///     skill with
    ///     <see cref="A2AErrorCode.InvalidParams" />; the details are in <see cref="A2AException.Details" />.
    /// </remarks>
    /// <exception cref="A2AException">
    ///     InvalidParams: the card does not list the skill, or the server refused the input.
    ///     InvalidAgentResponse: the result is missing or does not fit <typeparamref name="TOutput" />.
    /// </exception>
    /// <exception cref="TypedSkillCallException">The task ended in a state other than completed.</exception>
    [Experimental("A2AEXP001",
        UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public async Task<TOutput> CallSkillAsync<TInput, TOutput>(string skillId, TInput input,
        JsonTypeInfo<TInput> inputType,
        JsonTypeInfo<TOutput> outputType, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(skillId);
        if (input is null) throw new ArgumentNullException(nameof(input));
        ArgumentNullException.ThrowIfNull(inputType);
        ArgumentNullException.ThrowIfNull(outputType);

        var message = new Message
        {
            MessageId = Message.NewId(), Role = Role.User,
            Parts = [Part.FromData(JsonSerializer.SerializeToElement(input, inputType))]
        };
        options = SelectSkill(message, skillId, options);
        var response = await SendMessageAsync(message, options: options, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        switch (response)
        {
            case MessageResponse { Message: var reply }:
                return ReadResult(reply.Parts, outputType);
            case TaskResponse { Task: var task }:
                if (!task.Status.State.IsTerminal() && !task.Status.State.IsInterrupted())
                    task = await WaitForTaskAsync(task.Id, options: options, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                if (task.Status.State != TaskState.Completed) throw new TypedSkillCallException(task);
                for (var i = (task.Artifacts?.Count ?? 0) - 1; i >= 0; i--)
                    if (task.Artifacts![i].Parts.Any(p => p is DataPart))
                        return ReadResult(task.Artifacts[i].Parts, outputType);
                throw new A2AException(A2AErrorCode.InvalidAgentResponse, "The task completed without a data result.");
            default:
                throw new A2AException(A2AErrorCode.InvalidAgentResponse, "The agent sent no result.");
        }
    }

    private static TOutput ReadResult<TOutput>(List<Part> parts, JsonTypeInfo<TOutput> outputType)
    {
        if (parts.OfType<DataPart>().FirstOrDefault() is not { } data)
            throw new A2AException(A2AErrorCode.InvalidAgentResponse, "The result has no data part.");
        try
        {
            return data.Data.Deserialize(outputType) ??
                   throw new A2AException(A2AErrorCode.InvalidAgentResponse, "The result is null.");
        }
        catch (JsonException ex)
        {
            throw new A2AException(A2AErrorCode.InvalidAgentResponse,
                "The result does not fit the expected output type.", ex);
        }
    }

    private async Task<SendMessageResponse> InspectAsync(Task<SendMessageResponse> call, CancellationToken ct)
    {
        var response = await call.ConfigureAwait(false);
        switch (response)
        {
            case MessageResponse { Message: var m }:
                await NotifyAsync(new ReceivedMessage(m, ReceivedMessageSource.DirectReply, m.TaskId, m.ContextId), ct)
                    .ConfigureAwait(false);
                break;
            case TaskResponse { Task: var t }:
                await InspectAsync(t, ct).ConfigureAwait(false);
                break;
        }

        return response;
    }

    private async Task<AgentTask> InspectAsync(Task<AgentTask> call, CancellationToken ct)
    {
        var task = await call.ConfigureAwait(false);
        await InspectAsync(task, ct).ConfigureAwait(false);
        return task;
    }

    private ValueTask InspectAsync(AgentTask task, CancellationToken ct)
    {
        return task.Status?.Message is { } m
            ? NotifyAsync(new ReceivedMessage(m, ReceivedMessageSource.TaskSnapshot, task.Id, task.ContextId), ct)
            : ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Passes every agent message in <paramref name="source" /> to <see cref="A2AClientOptions.OnMessageReceived" />
    ///     before yielding its event.
    /// </summary>
    private IAsyncEnumerable<StreamResponse> Inspect(IAsyncEnumerable<StreamResponse> source)
    {
        return _options.OnMessageReceived is null ? source : InspectStreamAsync(source);
    }

    private async IAsyncEnumerable<StreamResponse> InspectStreamAsync(IAsyncEnumerable<StreamResponse> source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in source.WithCancellation(ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case MessageEvent { Message: var m }:
                    await NotifyAsync(new ReceivedMessage(m, ReceivedMessageSource.DirectReply, m.TaskId, m.ContextId),
                        ct).ConfigureAwait(false);
                    break;
                case StatusUpdateEvent { StatusUpdate: { Status.Message: { } m } su }:
                    await NotifyAsync(
                            new ReceivedMessage(m, ReceivedMessageSource.StatusUpdate, su.TaskId, su.ContextId), ct)
                        .ConfigureAwait(false);
                    break;
                case TaskEvent { Task: var t }:
                    await InspectAsync(t, ct).ConfigureAwait(false);
                    break;
            }

            yield return evt;
        }
    }

    private ValueTask NotifyAsync(ReceivedMessage received, CancellationToken ct)
    {
        return _options.OnMessageReceived is { } hook ? hook(received, ct) : ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Follows a task: subscribes, and when the connection drops reconnects with backoff (0.5 s, 1 s, 2 s, 4 s, 8 s; then
    ///     the error is thrown) and <c>Last-Event-ID</c>, so no event is lost or repeated. A task that has already finished
    ///     is answered with its final snapshot as a <see cref="TaskEvent" />. The same <paramref name="options" /> (headers,
    ///     credentials) are used on every reconnect.
    /// </summary>
    /// <remarks>
    ///     "Not repeated" needs SSE event ids, which this SDK's servers send. A peer without event ids gives no cursor to
    ///     resume
    ///     from: after a reconnect it may send events again (starting with a task snapshot), and they are yielded again.
    ///     The same holds for
    ///     <see cref="SendStreamingMessageAsync(SendMessageRequest, A2ARequestOptions?, CancellationToken)" />.
    /// </remarks>
    public IAsyncEnumerable<StreamResponse> ObserveTaskAsync(string taskId, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        A2AValidator.RequireId(taskId, "id");
        RequireCapability(AgentCard?.Capabilities.Streaming, A2AErrorCode.UnsupportedOperation,
            "Agent does not declare streaming support.");
        var tenant = _options.Tenant;
        return ClientTelemetry.TraceStream(A2AProtocol.Methods.SubscribeToTask, this, tenant, taskId, Inspect(Checked(Guard(
            ResumableStream.RunAsync(
                taskId,
                ct => Resubscribe(tenant, taskId, null, options, ct),
                (id, cursor, ct) => Resubscribe(tenant, id, cursor, options, ct),
                (id, ct) => GetTaskForResume(tenant, id, options, ct),
                _options.TimeProvider, cancellationToken)))));
    }

    private IAsyncEnumerable<StreamFrame> Resubscribe(string? tenant, string taskId, string? lastEventId,
        A2ARequestOptions? options, CancellationToken ct)
    {
        var request = new SubscribeToTaskRequest { Id = taskId, Tenant = tenant ?? _options.Tenant };
        return _transport is IResumableTransport rt
            ? rt.SubscribeToTaskAsync(request, options, lastEventId, ct)
            : Frames(_transport.SubscribeToTaskAsync(request, options, ct), ct);
    }

    private Task<AgentTask> GetTaskForResume(string? tenant, string taskId, A2ARequestOptions? options,
        CancellationToken ct)
    {
        return _transport.GetTaskAsync(new GetTaskRequest { Id = taskId, Tenant = tenant ?? _options.Tenant }, options,
            ct);
    }

    private static async IAsyncEnumerable<StreamFrame> Frames(IAsyncEnumerable<StreamResponse> source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
            yield return new StreamFrame(item, null);
    }

    /// <summary>
    ///     CancelTask, except that TaskNotCancelable for a task that is already canceled returns that task (a repeat cancel).
    ///     In
    ///     every other case, including a failing GetTask, the original exception is thrown.
    /// </summary>
    private async Task<AgentTask> CancelOrCanceledAsync(CancelTaskRequest request, A2ARequestOptions? options,
        CancellationToken ct)
    {
        try
        {
            return await _transport.CancelTaskAsync(request, options, ct).ConfigureAwait(false);
        }
        catch (A2AException ex) when (ex.Code == A2AErrorCode.TaskNotCancelable)
        {
            AgentTask? current = null;
            try
            {
                current = await _transport
                    .GetTaskAsync(new GetTaskRequest { Id = request.Id, Tenant = request.Tenant }, options, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // The lookup failed: report the cancel's own error.
            }

            if (current?.Status?.State == TaskState.Canceled) return current;
            throw;
        }
    }

    // ---- Circuit breaker: wraps only the transport call ----

    private Task<T> Guard<T>(Func<Task<T>> call)
    {
        return _breaker is null ? call() : _breaker.RunAsync(call);
    }

    private Task Guard(Func<Task> call)
    {
        return _breaker is null
            ? call()
            : _breaker.RunAsync<object?>(async () =>
            {
                await call().ConfigureAwait(false);
                return null;
            });
    }

    /// <summary>
    ///     With <see cref="A2AClientOptions.ValidateResponses" />, checks the result with <paramref name="check" />
    ///     (spec 5.7): an agent response that lacks a REQUIRED field fails with InvalidAgentResponse.
    /// </summary>
    private Task<T> Checked<T>(Task<T> call, Action<T> check)
    {
        return _options.ValidateResponses ? CheckedAsync(call, check) : call;
    }

    private static async Task<T> CheckedAsync<T>(Task<T> call, Action<T> check)
    {
        var result = await call.ConfigureAwait(false);
        check(result);
        return result;
    }

    /// <summary>Checks each stream event as it arrives, with <see cref="A2AClientOptions.ValidateResponses" />.</summary>
    private IAsyncEnumerable<StreamResponse> Checked(IAsyncEnumerable<StreamResponse> source)
    {
        return _options.ValidateResponses ? CheckedStreamAsync(source) : source;
    }

    private static async IAsyncEnumerable<StreamResponse> CheckedStreamAsync(IAsyncEnumerable<StreamResponse> source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in source.WithCancellation(ct).ConfigureAwait(false))
        {
            A2AValidator.ValidateResponse(evt);
            yield return evt;
        }
    }

    private static void ValidateTasks(ListTasksResponse response)
    {
        if (response.Tasks is not { } tasks) return;
        foreach (var task in tasks) A2AValidator.ValidateResponse(task);
    }

    private IAsyncEnumerable<StreamResponse> Guard(IAsyncEnumerable<StreamResponse> source)
    {
        return _breaker is null ? source : _breaker.RunStreamAsync(source);
    }

    private static void EnsureMessageId(SendMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Message is { } m && string.IsNullOrWhiteSpace(m.MessageId)) m.MessageId = Message.NewId();
    }

    private void RequireCapability(bool? declared, A2AErrorCode code, string? message = null)
    {
        if (!_options.ValidateCapabilities || AgentCard is null) return;
        if (declared != true) throw new A2AException(code, message);
    }

#pragma warning disable A2AEXP005
    /// <summary>
    ///     A2A 1.1 SendLiveMessage (spec 1.1 §3.1.12): opens a live exchange with <paramref name="first" /> as its first
    ///     request
    ///     (a message, or a subscribe to an existing task) and returns it. Read <see cref="A2ALiveConnection.Events" /> first:
    ///     the
    ///     exchange learns its task from it. Sends <c>A2A-Version: 1.1</c>; the agent must serve 1.1 and declare streaming.
    /// </summary>
    /// <exception cref="NotSupportedException">The transport has no live exchange.</exception>
    [Experimental("A2AEXP005",
        UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public Task<A2ALiveConnection> OpenLiveAsync(StreamRequest first, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(first);
        return _transport is ILiveTransport live
            ? live.OpenLiveAsync(first, options, cancellationToken)
            : throw new NotSupportedException(
                "The transport of this client has no live exchange (A2A 1.1 SendLiveMessage).");
    }

    /// <summary>
    ///     Opens a live exchange that starts by sending <paramref name="message" /> (
    ///     <see cref="OpenLiveAsync(StreamRequest, A2ARequestOptions?, CancellationToken)" />).
    /// </summary>
    [Experimental("A2AEXP005",
        UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public Task<A2ALiveConnection> OpenLiveAsync(Message message, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return OpenLiveAsync(new StreamRequest { SendMessage = new SendMessageRequest { Message = message } }, options,
            cancellationToken);
    }
#pragma warning restore A2AEXP005
}