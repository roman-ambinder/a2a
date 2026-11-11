using System.Text.Json;

namespace A2A.Server;

/// <summary>
///     Streams one artifact chunk by chunk. Made by <see cref="TaskUpdater.OpenArtifact" />. The first write creates the
///     artifact with its name, description and metadata; later writes append to it. <see cref="CompleteAsync" /> sends the
///     last chunk (<c>lastChunk</c>, which seals it).
/// </summary>
/// <remarks>
///     A task that reaches a terminal state completes its open writers first, so
///     <c>await using var writer = updater.OpenArtifact("answer");</c> followed by <c>updater.CompleteAsync()</c> in the same
///     scope seals the artifact before the task completes. Disposing also completes the writer. A writer that never wrote
///     sends nothing. A writer is not thread-safe: write from one flow at a time.
/// </remarks>
public sealed class ArtifactWriter : IAsyncDisposable
{
    private readonly string? _description;
    private readonly Dictionary<string, JsonElement>? _metadata;
    private readonly string? _name;
    private readonly TaskUpdater _updater;
    private bool _completed;
    private bool _started;

    internal ArtifactWriter(TaskUpdater updater, string artifactId, string? name, string? description,
        Dictionary<string, JsonElement>? metadata)
    {
        _updater = updater;
        ArtifactId = artifactId;
        _name = name;
        _description = description;
        _metadata = metadata is null ? null : new Dictionary<string, JsonElement>(metadata);
    }

    /// <summary>Id of the artifact this writer writes.</summary>
    public string ArtifactId { get; }

    /// <summary>True once the last chunk was sent, or the task ended.</summary>
    public bool IsCompleted => _completed;

    /// <summary>Sends <paramref name="text" /> as the next chunk. Empty text sends nothing.</summary>
    /// <exception cref="InvalidOperationException">The writer is completed, or the task is already terminal.</exception>
    public ValueTask WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length == 0 ? default : WriteAsync([Part.FromText(text)], cancellationToken);
    }

    /// <summary>Sends <paramref name="parts" /> as the next chunk. No parts sends nothing.</summary>
    /// <exception cref="InvalidOperationException">The writer is completed, or the task is already terminal.</exception>
    public async ValueTask WriteAsync(IEnumerable<Part> parts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (_completed)
            throw new InvalidOperationException($"Artifact '{ArtifactId}' is complete: open a new writer for more output.");
        var list = parts.ToList();
        if (list.Count == 0) return;
        if (_started)
        {
            await _updater.AddArtifactAsync(list, ArtifactId, append: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await _updater.AddArtifactAsync(list, ArtifactId, _name, _description, metadata: _metadata,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _started = true;
    }

    /// <summary>
    ///     Sends the last chunk: an empty append with <c>lastChunk</c>, which seals the artifact. Does nothing when the writer
    ///     never wrote or is already completed.
    /// </summary>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_completed) return;
        _completed = true;
        _updater.Forget(this);
        if (!_started) return;
        await _updater.AddArtifactAsync(new Artifact { ArtifactId = ArtifactId, Parts = [] }, append: true,
            lastChunk: true, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Completes the writer, unless the task is already terminal (then there is nothing left to send).</summary>
    public ValueTask DisposeAsync()
    {
        if (_completed) return default;
        if (_updater.IsTerminal || _updater.HasReplied)
        {
            _completed = true;
            _updater.Forget(this);
            return default;
        }

        return CompleteAsync(CancellationToken.None);
    }
}
