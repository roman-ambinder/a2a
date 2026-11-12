using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using A2A.Client;
using A2A.Extensions.AI.Shared;
using Microsoft.Extensions.AI;

namespace A2A.Extensions.AI;

/// <summary>
///     An AI tool that sends a message to a remote A2A agent and returns what it answered. Create it with
///     <see cref="A2AClientAIExtensions.AsAIFunction" />.
/// </summary>
/// <remarks>
///     The result is a JSON object with <c>state</c>, <c>taskId</c>, <c>contextId</c>, <c>remoteContent</c>,
///     <c>truncated</c>,
///     <c>nonTextParts</c> and <c>notice</c>. Non-text parts are counted, never inlined. Failed, rejected, canceled,
///     input-required
///     and auth-required tasks are results with their state, not exceptions. Remote content is untrusted: see
///     <see cref="A2AAgentFunctionOptions.UntrustedContentNotice" />.
/// </remarks>
public sealed class A2AAgentFunction : AIFunction
{
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonElement Schema = BuildSchema();

    private readonly IA2AClient _client;
    private readonly A2AAgentFunctionOptions _options;

    internal A2AAgentFunction(IA2AClient client, string name, string description, A2AAgentFunctionOptions options)
    {
        _client = client;
        Name = name;
        Description = description;
        _options = options;
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string Description { get; }

    /// <inheritdoc />
    public override JsonElement JsonSchema => Schema;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var text = GetString(arguments, "message");
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("The 'message' argument is required.", nameof(arguments));
        var contextId = GetString(arguments, "contextId");
        var taskId = GetString(arguments, "taskId");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.Timeout != Timeout.InfiniteTimeSpan) cts.CancelAfter(_options.Timeout);
        string? remoteTaskId = null;
        try
        {
            var known = await RemoteAgentSupport.LookupKnownAsync(_client, string.IsNullOrEmpty(taskId) ? null : taskId,
                _options.RequestOptions, cts.Token, cancellationToken).ConfigureAwait(false);
            var message = Message.FromUser(text, string.IsNullOrEmpty(contextId) ? null : contextId,
                string.IsNullOrEmpty(taskId) ? null : taskId);
            var response = await _client.SendMessageAsync(message, null, _options.RequestOptions, cts.Token)
                .ConfigureAwait(false);
            switch (response)
            {
                case MessageResponse reply:
                    return BuildResult(TaskState.Completed, reply.Message.TaskId, reply.Message.ContextId,
                        DescribeMessage(reply.Message));
                case TaskResponse { Task: var task }:
                    remoteTaskId = task.Id;
                    if (RemoteAgentSupport.NeedsWait(task.Status.State))
                        task = await _client.WaitForTaskAsync(task.Id, null, _options.RequestOptions, cts.Token)
                            .ConfigureAwait(false);
                    var fresh = task.Artifacts?.Where(a => !known.ArtifactIds.Contains(a.ArtifactId)).ToList();
                    if (fresh is not null && _options.OnArtifactReceived is { } hook)
                        foreach (var artifact in fresh)
                            await hook(artifact, cts.Token).ConfigureAwait(false);
                    return BuildResult(task, fresh);
                default:
                    throw new InvalidOperationException("The remote agent sent an unknown response.");
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            await TryCancelRemoteAsync(remoteTaskId).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"The remote agent did not finish within {_options.Timeout}.");
        }
    }

    private async Task TryCancelRemoteAsync(string? taskId)
    {
        if (taskId is null) return;
        try
        {
            using var cts = new CancellationTokenSource(CancelTimeout);
            await _client.CancelTaskAsync(taskId, _options.RequestOptions, cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: the caller already has its own error.
        }
    }

    private static string? GetString(AIFunctionArguments arguments, string key)
    {
        return arguments.TryGetValue(key, out var value)
            ? value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
                _ => null
            }
            : null;
    }

    private JsonElement BuildResult(AgentTask task, List<Artifact>? artifacts)
    {
        var content = new RemoteContent();
        // An interrupted task asks something: the question is the content. Otherwise the artifacts, else the status message.
        if (task.Status.State.IsInterrupted() && task.Status.Message is { } question)
        {
            content.AddBlock(question.Parts);
        }
        else
        {
            if (artifacts is not null)
                foreach (var artifact in artifacts)
                    content.AddBlock(artifact.Parts);
            if (content.IsEmpty && task.Status.Message is { } status) content.AddBlock(status.Parts);
        }

        return BuildResult(task.Status.State, task.Id, task.ContextId, content);
    }

    private static RemoteContent DescribeMessage(Message message)
    {
        var content = new RemoteContent();
        content.AddBlock(message.Parts);
        return content;
    }

    private JsonElement BuildResult(TaskState state, string? taskId, string? contextId, RemoteContent content)
    {
        var text = content.ToString();
        var truncated = false;
        if (text.Length > _options.MaxResultCharacters)
        {
            var cut = _options.MaxResultCharacters;
            if (char.IsHighSurrogate(text[cut - 1])) cut--; // do not split a surrogate pair
            text = text[..cut];
            truncated = true;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("state", RemoteAgentSupport.StateName(state));
            if (!string.IsNullOrEmpty(taskId)) writer.WriteString("taskId", taskId);
            if (!string.IsNullOrEmpty(contextId)) writer.WriteString("contextId", contextId);
            writer.WriteString("remoteContent", text);
            writer.WriteBoolean("truncated", truncated);
            writer.WriteNumber("nonTextParts", content.NonTextParts);
            writer.WriteString("notice", _options.UntrustedContentNotice);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static JsonElement BuildSchema()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            WriteStringProperty(writer, "message", "The message to send to the remote agent.");
            WriteStringProperty(writer, "contextId",
                "Optional. The conversation id from an earlier result, to continue that conversation.");
            WriteStringProperty(writer, "taskId",
                "Optional. The task id from an earlier result, to answer a task that needs more input.");
            writer.WriteEndObject();
            writer.WriteStartArray("required");
            writer.WriteStringValue("message");
            writer.WriteEndArray();
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();

        static void WriteStringProperty(Utf8JsonWriter w, string name, string description)
        {
            w.WriteStartObject(name);
            w.WriteString("type", "string");
            w.WriteString("description", description);
            w.WriteEndObject();
        }
    }

    /// <summary>Text and JSON of parts; everything else is only counted.</summary>
    private sealed class RemoteContent
    {
        private readonly StringBuilder _text = new();
        private bool _blockHasContent;
        private bool _previousWasData;

        public int NonTextParts { get; private set; }
        public bool IsEmpty => _text.Length == 0;

        public void AddBlock(IEnumerable<Part> parts)
        {
            _blockHasContent = false;
            _previousWasData = false;
            foreach (var part in parts)
                switch (part)
                {
                    case TextPart text: Append(text.Text, false); break;
                    case DataPart data: Append(data.Data.GetRawText(), true); break;
                    default: NonTextParts++; break;
                }
        }

        private void Append(string value, bool isData)
        {
            if (_text.Length > 0)
            {
                if (!_blockHasContent) _text.Append("\n\n");
                else if (isData || _previousWasData) _text.Append('\n');
            }

            _text.Append(value);
            _blockHasContent = true;
            _previousWasData = isData;
        }

        public override string ToString()
        {
            return _text.ToString();
        }
    }
}