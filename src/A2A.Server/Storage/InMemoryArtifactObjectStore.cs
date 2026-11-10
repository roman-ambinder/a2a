using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace A2A.Server.Storage;

/// <summary>
///     Process-local object store for tests and development. It keeps every object in memory and loses them when the
///     process ends,
///     so it is no use with a durable <see cref="TaskStore" />. It has no download links: downloads go through the server.
/// </summary>
public sealed class InMemoryArtifactObjectStore(TimeProvider? time = null) : ArtifactObjectStore
{
    private readonly ConcurrentDictionary<string, Entry> _objects = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public override async ValueTask<ArtifactObjectWriteResult> PutAsync(string key, Stream content,
        ArtifactObjectInfo info, long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(info);
        using var metered = new MeteredReadStream(content, maxBytes);
        using var buffer = new MemoryStream();
        // Nothing is stored until the whole stream has been read, so a failed or oversized write leaves no object.
        await metered.CopyToAsync(buffer, 81920, cancellationToken).ConfigureAwait(false);
        _objects[key] = new Entry(buffer.ToArray(), info, _time.GetUtcNow());
        return new ArtifactObjectWriteResult(metered.BytesRead, metered.Sha256);
    }

    /// <inheritdoc />
    public override ValueTask<ArtifactObjectContent?> OpenReadAsync(string key, ArtifactByteRange? range = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (range is { Offset: < 0 } or { Length: < 0 }) throw new ArgumentOutOfRangeException(nameof(range));
        if (!_objects.TryGetValue(key, out var entry)) return ValueTask.FromResult<ArtifactObjectContent?>(null);
        var total = entry.Bytes.LongLength;
        var offset = Math.Min(range?.Offset ?? 0, total);
        var length = Math.Min(range?.Length ?? total - offset, total - offset);
        var stream = new MemoryStream(entry.Bytes, (int)offset, (int)length, false);
        return ValueTask.FromResult<ArtifactObjectContent?>(new ArtifactObjectContent(stream, entry.Info.ContentType,
            entry.Info.FileName, length, total));
    }

    /// <inheritdoc />
    public override ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        _objects.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ArtifactObjectEntry> ListAsync(string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var (key, entry) in _objects.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                yield return new ArtifactObjectEntry(key, entry.CreatedAt) { SizeBytes = entry.Bytes.LongLength };
        }
    }

    private sealed record Entry(byte[] Bytes, ArtifactObjectInfo Info, DateTimeOffset CreatedAt);
}