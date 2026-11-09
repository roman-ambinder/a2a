using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using A2A.Client.Resilience;
using A2A.Client.Streaming;
using A2A.Skills;
using A2A.Validation;

namespace A2A.Client;

/// <summary>
/// High-level A2A client. Wraps a transport, fills in the tenant, checks declared capabilities,
/// and adds convenience overloads.
/// </summary>
/// <remarks>
/// Every operation is a span of the <c>A2A.Client</c> <see cref="System.Diagnostics.ActivitySource"/> (see
/// <see cref="A2A.Diagnostics.A2ATelemetry"/>), covering retries and stream reconnects; stream events are span events. The
/// transports send the current span as a W3C <c>traceparent</c> header (gRPC metadata), so server spans join the trace.
/// With <see cref="A2AClientOptions.CircuitBreaker"/> set, the client also stops calling a peer that keeps failing. Only the
/// transport call counts; checks before it and <see cref="A2AClientOptions.OnMessageReceived"/> do not.
/// </remarks>
public sealed class A2AClient : IA2AClient, IA2AExtensionOperationClient
{
    private readonly IA2AClient _transport;
    private readonly A2AClientOptions _options;
    private readonly string? _binding;
    private readonly AgentCard? _telemetryCard;
    private readonly CircuitBreaker? _breaker;
    private volatile bool _extendedCardLoaded;

    /// <summary>Creates a client over <paramref name="transport"/>. <paramref name="options"/> defaults to a new <see cref="A2AClientOptions"/>. <paramref name="agentCard"/> enables capability checks when given.</summary>
    public A2AClient(IA2AClient transport, A2AClientOptions? options = null, AgentCard? agentCard = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new A2AClientOptions();
        AgentCard = agentCard;
        _telemetryCard = agentCard;
        _breaker = _options.CircuitBreaker is { } breaker ? new CircuitBreaker(breaker, _options.TimeProvider, agentCard?.Name) : null;
        _binding = (transport as ITransportBinding)?.Binding;
    }

    /// <summary>The card this client was created from, if any.</summary>
    public AgentCard? AgentCard { get; private set; }

    /// <summary>The card the client was created with: spans tag it, never the extended card a refresh may bring (for authenticated callers only).</summary>
    internal AgentCard? TelemetryCard => _telemetryCard;

    /// <summary>The binding name for the client's spans (null when the transport names none).</summary>
    internal string? TelemetryBinding => _binding;

    /// <summary>The protocol version the client speaks, for the client's spans.</summary>
    internal string TelemetryProtocolVersion => _options.ProtocolVersion;

    // ---- Convenience ----

    /// <summary>Sends a text message. Pass <paramref name="taskId"/>/<paramref name="contextId"/> to continue a conversation.</summary>
    public Task<SendMessageResponse> SendMessageAsync(string text, string? contextId = null, string? taskId = null, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendMessageAsync(new SendMessageRequest { Message = Message.FromUser(text, contextId, taskId) }, options, cancellationToken);

    /// <summary>Sends <paramref name="message"/> and waits for the result (spec §3.1.1).</summary>
    public Task<SendMessageResponse> SendMessageAsync(Message message, SendMessageConfiguration? configuration = null, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendMessageAsync(new SendMessageRequest { Message = message, Configuration = configuration }, options, cancellationToken);

    /// <summary>Sends <paramref name="message"/> and streams the updates (spec §3.1.2).</summary>
    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(Message message, SendMessageConfiguration? configuration = null, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendStreamingMessageAsync(new SendMessageRequest { Message = message, Configuration = configuration }, options, cancellationToken);

    /// <summary>Fetches a task (spec §3.1.3). <paramref name="historyLength"/> limits the history; null means the server default.</summary>
    public Task<AgentTask> GetTaskAsync(string taskId, int? historyLength = null, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        GetTaskAsync(new GetTaskRequest { Id = taskId, HistoryLength = historyLength }, options, cancellationToken);

    /// <summary>Asks the agent to cancel a task (spec §3.1.5).</summary>
    public Task<AgentTask> CancelTaskAsync(string taskId, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        CancelTaskAsync(new CancelTaskRequest { Id = taskId }, options, cancellationToken);

    /// <summary>Streams updates of an existing task (spec §3.1.6).</summary>
    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(string taskId, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId }, options, cancellationToken);

    /// <summary>
    /// Polls a task until it reaches a terminal or interrupted state. Use when streaming and push are not available,
    /// or to recover after a dropped stream.
    /// </summary>
    public async Task<AgentTask> WaitForTaskAsync(string taskId, TimeSpan? pollInterval = null, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
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
    /// Registers a webhook for an existing task safely: it registers FIRST, then calls GetTask once. A task that finishes after the
    /// registration reaches the webhook; a task that finished before it is seen in the returned <see cref="A2A.Client.Push.PushRegistration.Task"/>
    /// (check <see cref="A2A.Client.Push.PushRegistration.IsFinal"/>), so the caller does not wait for a callback that never comes.
    /// Servers of other SDKs send no catch-up delivery, so this order matters. The webhook is a hint; the task is the truth.
    /// </summary>
    /// <remarks>
    /// If GetTask fails, the exception is thrown and the webhook stays registered. <paramref name="config"/>.TaskId is set to
    /// <paramref name="taskId"/> (a different non-empty value is refused).
    /// </remarks>
    public async Task<A2A.Client.Push.PushRegistration> RegisterPushAsync(string taskId, TaskPushNotificationConfig config, int? historyLength = 0, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(taskId)) throw new ArgumentException("The task id is required.", nameof(taskId));
        ArgumentNullException.ThrowIfNull(config);
        if (!string.IsNullOrEmpty(config.TaskId) && config.TaskId != taskId)
            throw new ArgumentException("config.TaskId differs from taskId.", nameof(config));
        config.TaskId = taskId;
        var stored = await CreateTaskPushNotificationConfigAsync(config, options, cancellationToken).ConfigureAwait(false);
        var task = await GetTaskAsync(taskId, historyLength, options, cancellationToken).ConfigureAwait(false);
        return new A2A.Client.Push.PushRegistration(stored, task);
    }

    /// <summary>Pages through all tasks matching <paramref name="request"/>.</summary>
    public async IAsyncEnumerable<AgentTask> ListAllTasksAsync(ListTasksRequest? request = null, A2ARequestOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        request ??= new ListTasksRequest();
        while (true)
        {
            var page = await ListTasksAsync(request, options, cancellationToken).ConfigureAwait(false);
            foreach (var t in page.Tasks) yield return t;
            if (string.IsNullOrEmpty(page.NextPageToken)) yield break;
            request = new ListTasksRequest
            {
                Tenant = request.Tenant, ContextId = request.ContextId, Status = request.Status, PageSize = request.PageSize,
                PageToken = page.NextPageToken, HistoryLength = request.HistoryLength,
                StatusTimestampAfter = request.StatusTimestampAfter, IncludeArtifacts = request.IncludeArtifacts,
            };
        }
    }

    /// <summary>Fetches the extended card and makes it the current card for capability checks (spec §3.1.11).</summary>
    public async Task<AgentCard> RefreshWithExtendedAgentCardAsync(A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var card = await GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest(), options, cancellationToken).ConfigureAwait(false);
        AgentCard = card;
        _extendedCardLoaded = true;
        return card;
    }

    /// <summary>
    /// Selects a skill for one send: writes <paramref name="skillId"/> to the message metadata and requests the skill-selection
    /// extension. Pass <paramref name="message"/> and the returned options to a send method that takes a message or a request. The returned
    /// options must be passed, because they request the extension; without them the server ignores the selection.
    /// </summary>
    /// <param name="message">The message to send. It is changed and must be the one you send.</param>
    /// <param name="skillId">The <see cref="AgentSkill.Id"/> of the skill. Not empty.</param>
    /// <param name="options">Options for the call, or null for new ones. These options are changed and returned.</param>
    /// <returns>The options to pass with <paramref name="message"/>.</returns>
    /// <remarks>
    /// When <see cref="A2AClientOptions.ValidateCapabilities"/> is on and the card is known, a skill that is not on the card fails here,
    /// and nothing is sent. The check is skipped when the card declares an extended card that is not loaded yet
    /// (<see cref="RefreshWithExtendedAgentCardAsync"/>), because the public list is then incomplete. A plain A2A peer ignores the
    /// selection.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="skillId"/> is null or empty.</exception>
    /// <exception cref="A2AException">InvalidParams: the card does not list the skill.</exception>
    [global::System.Diagnostics.CodeAnalysis.Experimental("A2AEXP001", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public A2ARequestOptions SelectSkill(Message message, string skillId, A2ARequestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(skillId);
        var extendedLoaded = _extendedCardLoaded; // read before the card: a refresh writes the card first, then the flag
        if (_options.ValidateCapabilities && AgentCard is { } card
            && (card.Capabilities.ExtendedAgentCard != true || extendedLoaded)
            && !(card.Skills?.Any(s => string.Equals(s.Id, skillId, StringComparison.Ordinal)) ?? false))
        {
            throw A2AException.InvalidParams($"The Agent Card has no skill '{skillId}'.");
        }

        message.SetRequestedSkillId(skillId);
        options ??= new A2ARequestOptions();
        if (options.Extensions is null or { IsReadOnly: true }) options.Extensions = new List<string>(options.Extensions ?? []);
        if (!options.Extensions.Contains(SkillSelection.ExtensionUri)) options.Extensions.Add(SkillSelection.ExtensionUri);
        return options;
    }

    /// <summary>
    /// Calls a typed skill: sends <paramref name="input"/> as one data part, waits for the task, and returns the data part of the
    /// last artifact that has one. A direct message reply with a data part is read the same way, for agents of other SDKs.
    /// </summary>
    /// <typeparam name="TInput">The input type.</typeparam>
    /// <typeparam name="TOutput">The output type.</typeparam>
    /// <param name="skillId">The <see cref="AgentSkill.Id"/> of the skill. Not empty.</param>
    /// <param name="input">The input. Not null.</param>
    /// <param name="inputType">Type information for <typeparamref name="TInput"/>, from a source-generated JSON context.</param>
    /// <param name="outputType">Type information for <typeparamref name="TOutput"/>, from a source-generated JSON context.</param>
    /// <param name="options">Options for the call, or null for new ones. These options are changed.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// It uses <see cref="SelectSkill"/>, so the same card check applies. The server refuses input that does not fit the skill with
    /// <see cref="A2AErrorCode.InvalidParams"/>; the details are in <see cref="A2AException.Details"/>.
    /// </remarks>
    /// <exception cref="A2AException">InvalidParams: the card does not list the skill, or the server refused the input. InvalidAgentResponse: the result is missing or does not fit <typeparamref name="TOutput"/>.</exception>
    /// <exception cref="TypedSkillCallException">The task ended in a state other than completed.</exception>
    [global::System.Diagnostics.CodeAnalysis.Experimental("A2AEXP001", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public async Task<TOutput> CallSkillAsync<TInput, TOutput>(string skillId, TInput input, JsonTypeInfo<TInput> inputType,
        JsonTypeInfo<TOutput> outputType, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(skillId);
        if (input is null) throw new ArgumentNullException(nameof(input));
        ArgumentNullException.ThrowIfNull(inputType);
        ArgumentNullException.ThrowIfNull(outputType);

        var message = new Message { MessageId = Message.NewId(), Role = Role.User, Parts = [Part.FromData(JsonSerializer.SerializeToElement(input, inputType))] };
        options = SelectSkill(message, skillId, options);
        var response = await SendMessageAsync(message, options: options, cancellationToken: cancellationToken).ConfigureAwait(false);
        switch (response)
        {
            case MessageResponse { Message: var reply }:
                return ReadResult(reply.Parts, outputType);
            case TaskResponse { Task: var task }:
                if (!task.Status.State.IsTerminal() && !task.Status.State.IsInterrupted())
                    task = await WaitForTaskAsync(task.Id, options: options, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (task.Status.State != TaskState.Completed) throw new TypedSkillCallException(task);
                for (var i = (task.Artifacts?.Count ?? 0) - 1; i >= 0; i--)
                    if (task.Artifacts![i].Parts.Any(p => p is DataPart)) return ReadResult(task.Artifacts[i].Parts, outputType);
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
            return data.Data.Deserialize(outputType) ?? throw new A2AException(A2AErrorCode.InvalidAgentResponse, "The result is null.");
        }
        catch (JsonException ex)
        {
            throw new A2AException(A2AErrorCode.InvalidAgentResponse, "The result does not fit the expected output type.", ex);
        }
    }

    // ---- IA2AClient ----

    /// <inheritdoc />
    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureMessageId(request);
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        if (request.Configuration?.TaskPushNotificationConfig is not null) RequireCapability(AgentCard?.Capabilities.PushNotifications, A2AErrorCode.PushNotificationNotSupported);
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.SendMessage, this, request.Tenant, request.Message?.TaskId, () =>
        {
            var call = Guard(() => _transport.SendMessageAsync(request, options, cancellationToken));
            return _options.OnMessageReceived is null ? call : InspectAsync(call, cancellationToken);
        }, cancellationToken, request.Message);
    }

    private async Task<SendMessageResponse> InspectAsync(Task<SendMessageResponse> call, CancellationToken ct)
    {
        var response = await call.ConfigureAwait(false);
        switch (response)
        {
            case MessageResponse { Message: var m }:
                await NotifyAsync(new ReceivedMessage(m, ReceivedMessageSource.DirectReply, m.TaskId, m.ContextId), ct).ConfigureAwait(false);
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

    private ValueTask InspectAsync(AgentTask task, CancellationToken ct) =>
        task.Status?.Message is { } m
            ? NotifyAsync(new ReceivedMessage(m, ReceivedMessageSource.TaskSnapshot, task.Id, task.ContextId), ct)
            : ValueTask.CompletedTask;

    /// <summary>Passes every agent message in <paramref name="source"/> to <see cref="A2AClientOptions.OnMessageReceived"/> before yielding its event.</summary>
    private IAsyncEnumerable<StreamResponse> Inspect(IAsyncEnumerable<StreamResponse> source) =>
        _options.OnMessageReceived is null ? source : InspectStreamAsync(source);

    private async IAsyncEnumerable<StreamResponse> InspectStreamAsync(IAsyncEnumerable<StreamResponse> source, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in source.WithCancellation(ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case MessageEvent { Message: var m }:
                    await NotifyAsync(new ReceivedMessage(m, ReceivedMessageSource.DirectReply, m.TaskId, m.ContextId), ct).ConfigureAwait(false);
                    break;
                case StatusUpdateEvent { StatusUpdate: { Status.Message: { } m } su }:
                    await NotifyAsync(new ReceivedMessage(m, ReceivedMessageSource.StatusUpdate, su.TaskId, su.ContextId), ct).ConfigureAwait(false);
                    break;
                case TaskEvent { Task: var t }:
                    await InspectAsync(t, ct).ConfigureAwait(false);
                    break;
            }
            yield return evt;
        }
    }

    private ValueTask NotifyAsync(ReceivedMessage received, CancellationToken ct) =>
        _options.OnMessageReceived is { } hook ? hook(received, ct) : ValueTask.CompletedTask;

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureMessageId(request);
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.Streaming, A2AErrorCode.UnsupportedOperation, "Agent does not declare streaming support.");
        return ClientTelemetry.TraceStream(A2AProtocol.Methods.SendStreamingMessage, this, request.Tenant, request.Message?.TaskId, Inspect(Guard(ResumableStream.RunAsync(
            knownTaskId: null,
            ct => _transport is IResumableTransport rt
                ? rt.SendStreamingFramesAsync(request, options, ct)
                : Frames(_transport.SendStreamingMessageAsync(request, options, ct), ct),
            (taskId, cursor, ct) => Resubscribe(request.Tenant, taskId, cursor, options, ct),
            (taskId, ct) => GetTaskForResume(request.Tenant, taskId, options, ct),
            _options.TimeProvider, cancellationToken))), request.Message);
    }

    /// <summary>
    /// Follows a task: subscribes, and when the connection drops reconnects with backoff (0.5 s, 1 s, 2 s, 4 s, 8 s; then
    /// the error is thrown) and <c>Last-Event-ID</c>, so no event is lost or repeated. A task that has already finished
    /// is answered with its final snapshot as a <see cref="TaskEvent"/>. The same <paramref name="options"/> (headers,
    /// credentials) are used on every reconnect.
    /// </summary>
    /// <remarks>
    /// "Not repeated" needs SSE event ids, which this SDK's servers send. A peer without event ids gives no cursor to resume
    /// from: after a reconnect it may send events again (starting with a task snapshot), and they are yielded again.
    /// The same holds for <see cref="SendStreamingMessageAsync(SendMessageRequest, A2ARequestOptions?, CancellationToken)"/>.
    /// </remarks>
    public IAsyncEnumerable<StreamResponse> ObserveTaskAsync(string taskId, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        A2AValidator.RequireId(taskId, "id");
        RequireCapability(AgentCard?.Capabilities.Streaming, A2AErrorCode.UnsupportedOperation, "Agent does not declare streaming support.");
        var tenant = _options.Tenant;
        return ClientTelemetry.TraceStream(A2AProtocol.Methods.SubscribeToTask, this, tenant, taskId, Inspect(Guard(ResumableStream.RunAsync(
            knownTaskId: taskId,
            ct => Resubscribe(tenant, taskId, null, options, ct),
            (id, cursor, ct) => Resubscribe(tenant, id, cursor, options, ct),
            (id, ct) => GetTaskForResume(tenant, id, options, ct),
            _options.TimeProvider, cancellationToken))));
    }

    private IAsyncEnumerable<StreamFrame> Resubscribe(string? tenant, string taskId, string? lastEventId, A2ARequestOptions? options, CancellationToken ct)
    {
        var request = new SubscribeToTaskRequest { Id = taskId, Tenant = tenant ?? _options.Tenant };
        return _transport is IResumableTransport rt
            ? rt.SubscribeToTaskAsync(request, options, lastEventId, ct)
            : Frames(_transport.SubscribeToTaskAsync(request, options, ct), ct);
    }

    private Task<AgentTask> GetTaskForResume(string? tenant, string taskId, A2ARequestOptions? options, CancellationToken ct) =>
        _transport.GetTaskAsync(new GetTaskRequest { Id = taskId, Tenant = tenant ?? _options.Tenant }, options, ct);

    private static async IAsyncEnumerable<StreamFrame> Frames(IAsyncEnumerable<StreamResponse> source, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false)) yield return new StreamFrame(item, null);
    }

    /// <inheritdoc />
    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.GetTask, this, request.Tenant, request.Id, () =>
        {
            var call = Guard(() => _transport.GetTaskAsync(request, options, cancellationToken));
            return _options.OnMessageReceived is null ? call : InspectAsync(call, cancellationToken);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Tenant ??= _options.Tenant;
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.ListTasks, this, request.Tenant, null,
            () => Guard(() => _transport.ListTasksAsync(request, options, cancellationToken)), cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.CancelTask, this, request.Tenant, request.Id, () =>
        {
            var call = Guard(() => CancelOrCanceledAsync(request, options, cancellationToken));
            return _options.OnMessageReceived is null ? call : InspectAsync(call, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// CancelTask, except that TaskNotCancelable for a task that is already canceled returns that task (a repeat cancel). In
    /// every other case, including a failing GetTask, the original exception is thrown.
    /// </summary>
    private async Task<AgentTask> CancelOrCanceledAsync(CancelTaskRequest request, A2ARequestOptions? options, CancellationToken ct)
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
                current = await _transport.GetTaskAsync(new GetTaskRequest { Id = request.Id, Tenant = request.Tenant }, options, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // The lookup failed: report the cancel's own error.
            }
            if (current?.Status?.State == TaskState.Canceled) return current;
            throw;
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.Streaming, A2AErrorCode.UnsupportedOperation, "Agent does not declare streaming support.");
        return ClientTelemetry.TraceStream(A2AProtocol.Methods.SubscribeToTask, this, request.Tenant, request.Id,
            Inspect(Guard(_transport.SubscribeToTaskAsync(request, options, cancellationToken))));
    }

    /// <inheritdoc />
    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        A2AValidator.ValidateOutgoing(config);
        config.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.PushNotifications, A2AErrorCode.PushNotificationNotSupported);
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.CreateTaskPushNotificationConfig, this, config.Tenant, config.TaskId,
            () => Guard(() => _transport.CreateTaskPushNotificationConfigAsync(config, options, cancellationToken)), cancellationToken);
    }

    /// <inheritdoc />
    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.GetTaskPushNotificationConfig, this, request.Tenant, request.TaskId,
            () => Guard(() => _transport.GetTaskPushNotificationConfigAsync(request, options, cancellationToken)), cancellationToken);
    }

    /// <inheritdoc />
    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.ListTaskPushNotificationConfigs, this, request.Tenant, request.TaskId,
            () => Guard(() => _transport.ListTaskPushNotificationConfigsAsync(request, options, cancellationToken)), cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        A2AValidator.ValidateOutgoing(request);
        request.Tenant ??= _options.Tenant;
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.DeleteTaskPushNotificationConfig, this, request.Tenant, request.TaskId,
            () => Guard(() => _transport.DeleteTaskPushNotificationConfigAsync(request, options, cancellationToken)), cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Tenant ??= _options.Tenant;
        RequireCapability(AgentCard?.Capabilities.ExtendedAgentCard, A2AErrorCode.UnsupportedOperation, "Agent does not declare an extended agent card.");
        return ClientTelemetry.TraceAsync(A2AProtocol.Methods.GetExtendedAgentCard, this, request.Tenant, null,
            () => Guard(() => _transport.GetExtendedAgentCardAsync(request, options, cancellationToken)), cancellationToken);
    }

    /// <summary>
    /// Calls a custom operation (see <see cref="IA2AExtensionOperationClient"/>). It is a span of the client, named by the method.
    /// Use <see cref="ExtensionOperationClientExtensions"/> to activate the extension of the operation too.
    /// </summary>
    /// <exception cref="A2AException">UnsupportedOperation when the transport cannot call custom operations.</exception>
    public Task<JsonElement> InvokeExtensionOperationAsync(string method, JsonElement parameters, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ExtensionOperationParams.RequireMethod(method);
        if (_transport is not IA2AExtensionOperationClient transport)
            throw new A2AException(A2AErrorCode.UnsupportedOperation, "The transport of this client does not support custom operations.");
        // As the other operations: this client's tenant applies, also when the transport has other options. Params that are not
        // an object are left to the transport, because HTTP+JSON can still carry the tenant in the path.
        parameters = ExtensionOperationParams.Normalize(parameters);
        if (parameters.ValueKind == JsonValueKind.Object) parameters = ExtensionOperationParams.WithTenant(parameters, _options.Tenant);
        return ClientTelemetry.TraceAsync(method, this, ExtensionOperationParams.TenantOf(parameters) ?? _options.Tenant, null,
            () => Guard(() => transport.InvokeExtensionOperationAsync(method, parameters, options, cancellationToken)), cancellationToken);
    }

    // ---- Circuit breaker: wraps only the transport call ----

    private Task<T> Guard<T>(Func<Task<T>> call) => _breaker is null ? call() : _breaker.RunAsync(call);

    private Task Guard(Func<Task> call) => _breaker is null ? call() : _breaker.RunAsync<object?>(async () => { await call().ConfigureAwait(false); return null; });

    private IAsyncEnumerable<StreamResponse> Guard(IAsyncEnumerable<StreamResponse> source) => _breaker is null ? source : _breaker.RunStreamAsync(source);

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
}
