using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace A2A.Mcp;

/// <summary>Builds the results of the tools made by <see cref="A2AClientMcpExtensions.AsMcpTools"/>.</summary>
internal sealed class A2AMcpResults(A2AMcpToolOptions options, string? getTaskToolName)
{
    internal const string UnknownTaskText = "Unknown or expired task.";
    internal const string AuthRequiredText = "The remote agent needs authorization. Authorization happens outside MCP, so this tool cannot continue. Ask the user to authorize at the remote agent, then start again.";

    /// <summary>A tool error: fixed text, no structured content.</summary>
    internal static CallToolResult Error(string text) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = text }],
    };

    /// <summary>A direct reply of the remote agent: always completed.</summary>
    internal CallToolResult FromMessage(Message reply)
    {
        var content = new Collector(options);
        content.AddBlock(reply.Parts);
        return Build(TaskState.Completed, reply.TaskId, reply.ContextId, content, isError: false);
    }

    /// <summary>
    /// A task snapshot. <paramref name="knownArtifacts"/> are the artifact ids the caller has seen in an earlier turn; null means all
    /// artifacts count. <paramref name="cancelCall"/> is true for the cancel tool: a canceled task is what the caller asked for.
    /// </summary>
    internal CallToolResult FromTask(AgentTask task, HashSet<string>? knownArtifacts, bool cancelCall = false)
    {
        var state = task.Status.State;
        var content = new Collector(options);
        switch (state)
        {
            case TaskState.Submitted or TaskState.Working:
                content.AddText(getTaskToolName is null
                    ? "The task is still running."
                    : $"The task is still running. Call {getTaskToolName} with this taskId to check on it later.");
                break;
            case TaskState.AuthRequired:
                // The remote status text may carry a link meant for a person. The model must not follow it.
                content.AddText(AuthRequiredText);
                break;
            case TaskState.InputRequired:
                if (task.Status.Message is { } question) content.AddBlock(question.Parts);
                break;
            case TaskState.Completed:
                AddArtifacts(content, task, knownArtifacts);
                if (content.IsEmpty && task.Status.Message is { } done) content.AddBlock(done.Parts);
                break;
            default:
                if (task.Status.Message is { } status) content.AddBlock(status.Parts);
                if (content.IsEmpty) AddArtifacts(content, task, knownArtifacts);
                break;
        }
        var isError = A2AMcpState.IsError(state) && !(cancelCall && state == TaskState.Canceled);
        return Build(state, task.Id, task.ContextId, content, isError);
    }

    /// <summary>The send ended before the remote agent named a task. State <c>unknown</c>.</summary>
    internal CallToolResult UnknownState(string text, string? contextId = null)
    {
        var content = new Collector(options);
        content.AddText(text);
        return Build(TaskState.Unspecified, null, contextId, content, isError: true);
    }

    private static void AddArtifacts(Collector content, AgentTask task, HashSet<string>? known)
    {
        if (task.Artifacts is null) return;
        foreach (var artifact in task.Artifacts)
        {
            if (known is not null && known.Contains(artifact.ArtifactId)) continue;
            content.AddBlock(artifact.Parts);
        }
    }

    private CallToolResult Build(TaskState state, string? taskId, string? contextId, Collector content, bool isError)
    {
        var text = content.Text;
        var truncated = false;
        if (text.Length > options.MaxResultCharacters)
        {
            var cut = options.MaxResultCharacters;
            if (char.IsHighSurrogate(text[cut - 1])) cut--; // do not split a surrogate pair
            text = text[..cut];
            truncated = true;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("state", A2AMcpState.Name(state));
            if (!string.IsNullOrEmpty(taskId)) writer.WriteString("taskId", taskId);
            if (!string.IsNullOrEmpty(contextId)) writer.WriteString("contextId", contextId);
            writer.WriteString("remoteContent", text);
            writer.WriteBoolean("truncated", truncated);
            writer.WriteNumber("omittedParts", content.OmittedParts);
            writer.WriteString("notice", options.UntrustedContentNotice);
            writer.WriteEndObject();
        }
        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);

        var blocks = new List<ContentBlock>(1 + content.Blocks.Count) { new TextContentBlock { Text = json } };
        blocks.AddRange(content.Blocks);
        return new CallToolResult
        {
            IsError = isError,
            StructuredContent = document.RootElement.Clone(),
            Content = blocks,
        };
    }

    /// <summary>Text and JSON of parts become text. Bytes and links become content blocks, within the limits.</summary>
    private sealed class Collector(A2AMcpToolOptions options)
    {
        private readonly StringBuilder _text = new();
        private bool _blockHasContent;
        private bool _previousWasData;
        private long _binaryBytes;
        private int _partIndex;

        public List<ContentBlock> Blocks { get; } = [];
        public int OmittedParts { get; private set; }
        public bool IsEmpty => _text.Length == 0;
        public string Text => _text.ToString();

        /// <summary>Adds our own fixed text.</summary>
        public void AddText(string text)
        {
            _blockHasContent = false;
            _previousWasData = false;
            Append(text, isData: false);
        }

        public void AddBlock(IEnumerable<Part> parts)
        {
            _blockHasContent = false;
            _previousWasData = false;
            foreach (var part in parts)
            {
                var index = _partIndex++;
                switch (part)
                {
                    case TextPart text: Append(text.Text, isData: false); break;
                    case DataPart data: Append(data.Data.GetRawText(), isData: true); break;
                    case RawPart raw: AddRaw(raw, index); break;
                    case UrlPart url: AddUrl(url); break;
                    default: OmittedParts++; break;
                }
            }
        }

        private void AddRaw(RawPart raw, int index)
        {
            if (_binaryBytes + raw.Raw.Length > options.MaxBinaryBytes)
            {
                OmittedParts++;
                return;
            }
            _binaryBytes += raw.Raw.Length;
            Blocks.Add(A2AMcpContentExtensions.RawToBlock(raw, index));
        }

        private void AddUrl(UrlPart part)
        {
            if (A2AMcpContentExtensions.UrlToBlock(part) is { } block) Blocks.Add(block);
            else OmittedParts++;
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
    }
}
