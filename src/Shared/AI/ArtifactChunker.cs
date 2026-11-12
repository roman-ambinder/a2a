// Shared source, compiled into A2A.Extensions.AI and A2A.Extensions.AgentFramework (linked, not referenced: shipped
// packages do not use InternalsVisibleTo). The namespace differs per assembly so the two internal copies never share a
// full name. All types are internal.

using System.Text;
using A2A.Server;
using Microsoft.Extensions.AI;

#if A2A_AGENT_FRAMEWORK
namespace A2A.Extensions.AgentFramework.Shared;
#else
namespace A2A.Extensions.AI.Shared;
#endif

/// <summary>
///     Collects model output and emits it as chunks of one artifact. One chunk is always held back so the last one can say
///     so.
/// </summary>
/// <param name="artifactName">Name of the artifact, sent with the first chunk.</param>
/// <param name="streamChunkSize">Text characters to collect before a chunk is sent. At least 1.</param>
/// <param name="updater">Where the chunks go.</param>
internal sealed class ArtifactChunker(string artifactName, int streamChunkSize, TaskUpdater updater)
{
    private readonly string _artifactId = Guid.NewGuid().ToString();
    private readonly List<Part> _pending = [];
    private readonly StringBuilder _text = new();
    private bool _first = true;
    private bool _pendingNonText;

    /// <summary>Every part emitted so far, in order.</summary>
    public List<Part> Reply { get; } = [];

    /// <summary>True once any text or file content was added.</summary>
    public bool HasOutput { get; private set; }

    /// <summary>True when the model asked for a tool call that nothing ran.</summary>
    public bool HasUnresolvedToolCall { get; private set; }

    /// <summary>
    ///     Adds one content item. <see cref="ErrorContent" /> throws. Content with no A2A form (reasoning, usage, tool
    ///     results) is ignored.
    /// </summary>
    public async ValueTask AddAsync(AIContent content, CancellationToken ct)
    {
        switch (content)
        {
            case ErrorContent:
                throw new InvalidOperationException("The model reported an error.");
            case FunctionCallContent { InformationalOnly: false }:
                HasUnresolvedToolCall = true;
                return;
            case TextContent text:
                if (string.IsNullOrEmpty(text.Text)) return;
                await FlushIfDueAsync(ct).ConfigureAwait(false);
                _text.Append(text.Text);
                HasOutput = true;
                return;
            case DataContent or UriContent:
                if (ModelContent.ConvertToPart(content, false) is not { } part) return;
                await FlushIfDueAsync(ct).ConfigureAwait(false);
                MoveTextToPending();
                _pending.Add(part);
                _pendingNonText = true;
                HasOutput = true;
                return;
        }
    }

    /// <summary>Emits what is held back as the last chunk. Emits nothing when there was no output.</summary>
    public ValueTask FinishAsync(CancellationToken ct)
    {
        return EmitAsync(true, ct);
    }

    /// <summary>
    ///     Emits what is held back without marking it the last chunk, for a run that stops to wait for the caller. Emits
    ///     nothing when there is nothing held back.
    /// </summary>
    public ValueTask FlushAsync(CancellationToken ct)
    {
        return EmitAsync(false, ct);
    }

    private ValueTask FlushIfDueAsync(CancellationToken ct)
    {
        return _pendingNonText || _text.Length >= streamChunkSize
            ? EmitAsync(false, ct)
            : ValueTask.CompletedTask;
    }

    private void MoveTextToPending()
    {
        if (_text.Length == 0) return;
        _pending.Add(new TextPart(_text.ToString()));
        _text.Clear();
    }

    private async ValueTask EmitAsync(bool lastChunk, CancellationToken ct)
    {
        MoveTextToPending();
        if (_pending.Count == 0) return;
        // A new list for every event: the server owns what we pass in.
        var parts = new List<Part>(_pending);
        _pending.Clear();
        _pendingNonText = false;
        Reply.AddRange(parts);
        var append = !_first;
        _first = false;
        await updater.AddArtifactAsync(parts, _artifactId, append ? null : artifactName, append: append,
            lastChunk: lastChunk, cancellationToken: ct).ConfigureAwait(false);
    }
}
