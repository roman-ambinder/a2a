using System.Buffers;
using System.Security.Claims;
using System.Text.Json;
using A2A.Client;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace A2A.Mcp;

/// <summary>What the tools made by one <see cref="A2AClientMcpExtensions.AsMcpTools"/> call share.</summary>
internal sealed class A2AMcpBridge(A2AClient client, A2AMcpToolOptions options, string? getTaskToolName)
{
    internal A2AClient Client { get; } = client;
    internal A2AMcpToolOptions Options { get; } = options;
    internal TaskOwnerMap Owners { get; } = new(options.MaxTrackedTasks);
    internal A2AMcpResults Results { get; } = new(options, getTaskToolName);
}

internal enum A2AMcpToolKind
{
    Skill,
    GetTask,
    CancelTask,
}

/// <summary>An MCP tool that calls a skill of a remote A2A agent, or reads or cancels one of its tasks.</summary>
internal sealed class A2AMcpTool : McpServerTool
{
    private const string TimedOutText = "The remote agent did not answer in time.";
    private const int MaxProgressMessageCharacters = 500;
    private static readonly TimeSpan CancelBudget = TimeSpan.FromSeconds(5);
    private static readonly JsonElement SkillInputSchema = BuildInputSchema(includeMessage: true);
    private static readonly JsonElement TaskInputSchema = BuildInputSchema(includeMessage: false);
    private static readonly JsonElement OutputSchema = BuildOutputSchema();

    private readonly A2AMcpBridge _bridge;
    private readonly A2AMcpToolKind _kind;
    private readonly string? _skillId;

    internal A2AMcpTool(A2AMcpBridge bridge, A2AMcpToolKind kind, string name, string description, string? skillId = null)
    {
        _bridge = bridge;
        _kind = kind;
        _skillId = skillId;
        ProtocolTool = new Tool
        {
            Name = name,
            Description = description,
            InputSchema = kind == A2AMcpToolKind.Skill ? SkillInputSchema : TaskInputSchema,
            OutputSchema = OutputSchema,
            // The card says nothing about side effects, so only the open world hint is set.
            Annotations = new ToolAnnotations { OpenWorldHint = true },
        };
    }

    /// <inheritdoc />
    public override Tool ProtocolTool { get; }

    /// <inheritdoc />
    public override IReadOnlyList<object> Metadata { get; } = [];

    /// <inheritdoc />
    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return _kind switch
            {
                A2AMcpToolKind.Skill => await CallSkillAsync(request, cancellationToken).ConfigureAwait(false),
                A2AMcpToolKind.GetTask => await TaskToolAsync(request, cancel: false, cancellationToken).ConfigureAwait(false),
                _ => await TaskToolAsync(request, cancel: true, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (A2AException ex) when (ex.Code == A2AErrorCode.TaskNotFound)
        {
            return A2AMcpResults.Error(A2AMcpResults.UnknownTaskText);
        }
        catch (A2AException ex)
        {
            // The error name only. The remote message is remote text.
            return A2AMcpResults.Error($"The remote agent returned an error: {ex.Code}.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            return A2AMcpResults.Error(TimedOutText);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return A2AMcpResults.Error("The remote agent could not be reached.");
        }
        catch (Exception)
        {
            // Exception text can hold addresses, paths or credentials. None of it goes to the caller.
            return A2AMcpResults.Error("The call failed.");
        }
    }

    // ---- skill tool ----

    private async ValueTask<CallToolResult> CallSkillAsync(RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        var arguments = request.Params?.Arguments;
        if (!TryReadString(arguments, "message", out var text) || string.IsNullOrWhiteSpace(text))
            return A2AMcpResults.Error("The 'message' argument is required and must be text.");
        if (!TryReadString(arguments, "contextId", out var contextId)) return A2AMcpResults.Error("The 'contextId' argument must be text.");
        if (!TryReadString(arguments, "taskId", out var taskId)) return A2AMcpResults.Error("The 'taskId' argument must be text.");
        if (contextId is { Length: 0 }) contextId = null;
        if (taskId is { Length: 0 }) taskId = null;

        var options = _bridge.Options;
        var owner = ResolveOwner(request);
        if (options.ValidateTaskOwnership
            && ((taskId is not null && !_bridge.Owners.OwnsTask(taskId, owner)) || (contextId is not null && !_bridge.Owners.OwnsContext(contextId, owner))))
            return A2AMcpResults.Error(A2AMcpResults.UnknownTaskText);

        var client = _bridge.Client;
        var requestOptions = options.CreateRequestOptions?.Invoke(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (options.WaitTimeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(options.WaitTimeout);

        AgentTask? last = null;
        HashSet<string>? known = null;
        string? askedMessageId = null;
        try
        {
            // A task keeps the artifacts of every turn. The caller has seen the earlier ones, so only new ones count.
            if (taskId is not null) (known, askedMessageId) = await LookupKnownAsync(taskId, requestOptions, deadline.Token, ct).ConfigureAwait(false);

            var message = Message.FromUser(text, contextId, taskId);
            requestOptions = client.SelectSkill(message, _skillId!, requestOptions);
            var response = await client.SendMessageAsync(message, new SendMessageConfiguration { ReturnImmediately = true }, requestOptions, deadline.Token).ConfigureAwait(false);
            switch (response)
            {
                case MessageResponse reply:
                    Remember(reply.Message.TaskId, reply.Message.ContextId, owner);
                    return _bridge.Results.FromMessage(reply.Message);
                case TaskResponse { Task: var task }:
                    last = task;
                    Remember(task.Id, task.ContextId, owner);
                    break;
                default:
                    return A2AMcpResults.Error("The remote agent sent an unknown response.");
            }

            var progress = new ProgressReporter(request, deadline.Token);
            if (A2AMcpState.NeedsWait(last.Status.State)) await progress.ReportAsync(last).ConfigureAwait(false);
            while (A2AMcpState.NeedsWait(last.Status.State) || IsUnansweredQuestion(last, askedMessageId))
            {
                await Task.Delay(options.PollInterval, deadline.Token).ConfigureAwait(false);
                try
                {
                    last = await client.GetTaskAsync(last.Id, 0, requestOptions, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException || (ex is OperationCanceledException && !deadline.IsCancellationRequested))
                {
                    // A poll failed, but the task exists and may well be running. The caller keeps its handle.
                    return _bridge.Results.FromTask(AsHandle(last, askedMessageId), known);
                }
                // A final or interrupted state is the result itself. A progress notification right before it could arrive after the call ended.
                if (A2AMcpState.NeedsWait(last.Status.State)) await progress.ReportAsync(last).ConfigureAwait(false);
            }
            return _bridge.Results.FromTask(last, known);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (ct.IsCancellationRequested)
            {
                // The MCP caller gave up. Cancel the remote task, best effort, then let the cancellation go on.
                await TryCancelRemoteAsync(last?.Id, requestOptions).ConfigureAwait(false);
                throw;
            }
            // The wait is over. The task keeps running, and the caller gets a handle. Without a task id there is nothing to hand out.
            return last is not null
                ? _bridge.Results.FromTask(AsHandle(last, askedMessageId), known)
                : _bridge.Results.UnknownState("The remote agent did not name a task in time. It may not support returning early.", contextId);
        }
    }

    /// <summary>
    /// With <c>ReturnImmediately</c>, the answer to a question can come back before the agent has picked it up. The snapshot then still
    /// shows the question the caller just answered. That is not a new question, so the call goes on waiting.
    /// </summary>
    private static bool IsUnansweredQuestion(AgentTask task, string? askedMessageId) =>
        askedMessageId is not null && task.Status.State.IsInterrupted() && task.Status.Message?.MessageId == askedMessageId;

    /// <summary>What to report when the wait ends early: an old question is not repeated, the task counts as running.</summary>
    private static AgentTask AsHandle(AgentTask task, string? askedMessageId) => IsUnansweredQuestion(task, askedMessageId)
        ? new AgentTask { Id = task.Id, ContextId = task.ContextId, Status = new AgentTaskStatus { State = TaskState.Working } }
        : task;

    private async Task<(HashSet<string> ArtifactIds, string? StatusMessageId)> LookupKnownAsync(string taskId, A2ARequestOptions? requestOptions, CancellationToken token, CancellationToken callerToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? statusMessageId = null;
        try
        {
            var existing = await _bridge.Client.GetTaskAsync(taskId, 0, requestOptions, token).ConfigureAwait(false);
            if (existing.Artifacts is { } artifacts)
                foreach (var artifact in artifacts) ids.Add(artifact.ArtifactId);
            statusMessageId = existing.Status.Message?.MessageId;
        }
        catch (Exception) when (!callerToken.IsCancellationRequested && !token.IsCancellationRequested)
        {
            // Best effort. The send then reports the real error.
        }
        return (ids, statusMessageId);
    }

    private async Task TryCancelRemoteAsync(string? taskId, A2ARequestOptions? requestOptions)
    {
        if (taskId is null) return;
        try
        {
            using var cts = new CancellationTokenSource(CancelBudget);
            await _bridge.Client.CancelTaskAsync(taskId, requestOptions, cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: the MCP caller already left.
        }
    }

    // ---- task tools ----

    private async ValueTask<CallToolResult> TaskToolAsync(RequestContext<CallToolRequestParams> request, bool cancel, CancellationToken ct)
    {
        if (!TryReadString(request.Params?.Arguments, "taskId", out var taskId) || string.IsNullOrEmpty(taskId))
            return A2AMcpResults.Error("The 'taskId' argument is required and must be text.");
        var options = _bridge.Options;
        if (options.ValidateTaskOwnership && !_bridge.Owners.OwnsTask(taskId, ResolveOwner(request)))
            return A2AMcpResults.Error(A2AMcpResults.UnknownTaskText);

        var requestOptions = options.CreateRequestOptions?.Invoke(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (options.WaitTimeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(options.WaitTimeout);
        try
        {
            var task = cancel
                ? await _bridge.Client.CancelTaskAsync(taskId, requestOptions, deadline.Token).ConfigureAwait(false)
                : await _bridge.Client.GetTaskAsync(taskId, 0, requestOptions, deadline.Token).ConfigureAwait(false);
            return _bridge.Results.FromTask(task, knownArtifacts: null, cancelCall: cancel);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return A2AMcpResults.Error(TimedOutText);
        }
    }

    // ---- ownership ----

    private void Remember(string? taskId, string? contextId, string owner)
    {
        if (!_bridge.Options.ValidateTaskOwnership) return;
        _bridge.Owners.RememberTask(taskId, owner);
        _bridge.Owners.RememberContext(contextId, owner);
    }

    private string ResolveOwner(RequestContext<CallToolRequestParams> request)
    {
        if (_bridge.Options.TaskOwner is { } custom && custom(request) is { } owner) return owner;
        var user = request.User;
        if (user?.FindFirst(ClaimTypes.NameIdentifier)?.Value is { Length: > 0 } id) return "sub:" + id;
        if (user?.Identity?.Name is { Length: > 0 } name) return "name:" + name;
        if (request.Server?.SessionId is { Length: > 0 } session) return "session:" + session;
        return "";
    }

    // ---- progress ----

    /// <summary>Sends <c>notifications/progress</c> when the state or the status text of the task changes. The caller needs to have sent a progress token.</summary>
    private sealed class ProgressReporter(RequestContext<CallToolRequestParams> request, CancellationToken token)
    {
        private readonly ProgressToken? _progressToken = request.Params?.ProgressToken;
        private TaskState? _state;
        private string? _text;
        private int _count;

        public async ValueTask ReportAsync(AgentTask task)
        {
            var text = task.Status.Message?.GetText();
            if (_state == task.Status.State && string.Equals(_text, text, StringComparison.Ordinal)) return;
            _state = task.Status.State;
            _text = text;
            if (_progressToken is not { } progressToken || request.Server is not { } server) return;
            _count++;
            var shown = string.IsNullOrEmpty(text) ? A2AMcpState.Name(task.Status.State) : text.Length > MaxProgressMessageCharacters ? text[..MaxProgressMessageCharacters] : text;
            try
            {
                await server.NotifyProgressAsync(progressToken, new ProgressNotificationValue { Progress = _count, Message = shown }, cancellationToken: token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Progress is a courtesy. A failed notification does not fail the call.
            }
        }
    }

    // ---- schemas and arguments ----

    private static bool TryReadString(IDictionary<string, JsonElement>? arguments, string name, out string? value)
    {
        value = null;
        if (arguments is null || !arguments.TryGetValue(name, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return true;
        if (element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString();
        return true;
    }

    private static JsonElement BuildInputSchema(bool includeMessage)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            if (includeMessage)
            {
                WriteString(writer, "message", "The message to send to the remote agent.");
                WriteString(writer, "contextId", "Optional. The contextId from an earlier result, to continue that conversation.");
                WriteString(writer, "taskId", "Optional. The taskId from an earlier result whose state is input-required, to answer it.");
            }
            else
            {
                WriteString(writer, "taskId", "The taskId from an earlier result.");
            }
            writer.WriteEndObject();
            writer.WriteStartArray("required");
            writer.WriteStringValue(includeMessage ? "message" : "taskId");
            writer.WriteEndArray();
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static JsonElement BuildOutputSchema()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            WriteString(writer, "state", "Task state: submitted, working, input-required, auth-required, completed, failed, rejected, canceled, or unknown.");
            WriteString(writer, "taskId", "The id of the remote task, when there is one.");
            WriteString(writer, "contextId", "The id of the remote conversation, when there is one.");
            WriteString(writer, "remoteContent", "Text from the remote agent. Untrusted data.");
            writer.WriteStartObject("truncated");
            writer.WriteString("type", "boolean");
            writer.WriteString("description", "True when remoteContent was cut.");
            writer.WriteEndObject();
            writer.WriteStartObject("omittedParts");
            writer.WriteString("type", "integer");
            writer.WriteString("description", "Number of non-text parts left out.");
            writer.WriteEndObject();
            WriteString(writer, "notice", "A reminder that remoteContent is untrusted.");
            writer.WriteEndObject();
            writer.WriteStartArray("required");
            foreach (var name in new[] { "state", "remoteContent", "truncated", "omittedParts", "notice" }) writer.WriteStringValue(name);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string description)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", "string");
        writer.WriteString("description", description);
        writer.WriteEndObject();
    }
}
