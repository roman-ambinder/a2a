namespace A2A.Server.Storage;

/// <summary>
/// Stores the bytes of large artifacts outside the task store. Keys are made by <c>ArtifactObjectKey</c> and are safe to use as a
/// path. Every provider must: read the stream once and never buffer the whole object, stop with
/// <see cref="ArtifactObjectTooLargeException"/> when the stream is longer than <c>maxBytes</c>, and leave no object behind when a
/// write fails.
/// </summary>
public abstract class ArtifactObjectStore
{
    /// <summary>Writes an object. An existing object with the same key is replaced.</summary>
    /// <param name="key">The object key.</param>
    /// <param name="content">The bytes. It may be non-seekable. It is not disposed.</param>
    /// <param name="info">Content type and file name, kept with the object.</param>
    /// <param name="maxBytes">Largest size accepted.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The size and the SHA-256 of the bytes, as <see cref="Integrity.ArtifactHash.Format"/> writes it.</returns>
    /// <exception cref="ArtifactObjectTooLargeException">The stream is longer than <paramref name="maxBytes"/>. No object is left.</exception>
    public abstract ValueTask<ArtifactObjectWriteResult> PutAsync(string key, Stream content, ArtifactObjectInfo info, long maxBytes, CancellationToken cancellationToken = default);

    /// <summary>Opens an object for reading, or returns null when it does not exist. The caller disposes the result.</summary>
    /// <param name="key">The object key.</param>
    /// <param name="range">The bytes to read, clamped to the object's size. Null reads all of it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public abstract ValueTask<ArtifactObjectContent?> OpenReadAsync(string key, ArtifactByteRange? range = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the object exists. The default opens the object and disposes it; providers with a cheaper check (a HEAD request)
    /// should override it.
    /// </summary>
    /// <param name="key">The object key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public virtual async ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        var content = await OpenReadAsync(key, null, cancellationToken).ConfigureAwait(false);
        if (content is null) return false;
        await content.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Deletes an object. Does nothing when it does not exist.</summary>
    public abstract ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Lists the objects whose key starts with <paramref name="prefix"/>, in no particular order.</summary>
    public abstract IAsyncEnumerable<ArtifactObjectEntry> ListAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// A link the caller can fetch the object from directly, which works for <paramref name="lifetime"/>. Returns null when the
    /// provider has none. Default null.
    /// </summary>
    public virtual ValueTask<Uri?> TryCreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default) => ValueTask.FromResult<Uri?>(null);
}

/// <summary>Content type and file name kept with an object.</summary>
/// <param name="ContentType">The media type, or null.</param>
/// <param name="FileName">The file name, or null.</param>
public sealed record ArtifactObjectInfo(string? ContentType, string? FileName);

/// <summary>What a write stored.</summary>
/// <param name="SizeBytes">Number of bytes written.</param>
/// <param name="Sha256">SHA-256 of the bytes, as <see cref="Integrity.ArtifactHash.Format"/> writes it.</param>
public sealed record ArtifactObjectWriteResult(long SizeBytes, string Sha256);

/// <summary>One object found by <see cref="ArtifactObjectStore.ListAsync"/>.</summary>
/// <param name="Key">The object key.</param>
/// <param name="CreatedAt">When the object was written.</param>
public sealed record ArtifactObjectEntry(string Key, DateTimeOffset CreatedAt);

/// <summary>A span of bytes to read.</summary>
/// <param name="Offset">First byte, from 0.</param>
/// <param name="Length">Number of bytes, or null for all bytes from <paramref name="Offset"/> on.</param>
public sealed record ArtifactByteRange(long Offset, long? Length);

/// <summary>An open object. Dispose it when done.</summary>
public sealed class ArtifactObjectContent : IAsyncDisposable
{
    /// <summary>Creates the content of a read.</summary>
    /// <param name="content">The bytes of the read range.</param>
    /// <param name="contentType">The media type, or null.</param>
    /// <param name="fileName">The file name, or null.</param>
    /// <param name="length">Number of bytes in <paramref name="content"/>.</param>
    /// <param name="totalLength">Size of the whole object.</param>
    public ArtifactObjectContent(Stream content, string? contentType, string? fileName, long length, long totalLength)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
        ContentType = contentType;
        FileName = fileName;
        Length = length;
        TotalLength = totalLength;
    }

    /// <summary>The bytes of the read range.</summary>
    public Stream Content { get; }
    /// <summary>The media type, or null.</summary>
    public string? ContentType { get; }
    /// <summary>The file name, or null.</summary>
    public string? FileName { get; }
    /// <summary>Number of bytes in <see cref="Content"/>.</summary>
    public long Length { get; }
    /// <summary>Size of the whole object.</summary>
    public long TotalLength { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>Thrown by <see cref="ArtifactObjectStore.PutAsync"/> when the content is longer than the limit.</summary>
public sealed class ArtifactObjectTooLargeException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="maxBytes">The limit that was passed.</param>
    public ArtifactObjectTooLargeException(long maxBytes) : base($"The object is larger than the limit of {maxBytes} bytes.") => MaxBytes = maxBytes;

    /// <summary>The limit that was passed.</summary>
    public long MaxBytes { get; }
}
