using System.Security.Cryptography;
using A2A.Integrity;

namespace A2A.Server.Storage;

/// <summary>
///     Read-only wrapper for providers to upload from: counts the bytes, hashes them (SHA-256) and throws
///     <see cref="ArtifactObjectTooLargeException" /> as soon as more than <c>maxBytes</c> have been read. Does not
///     dispose the inner stream.
/// </summary>
internal sealed class MeteredReadStream : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private string? _sha256;

    public MeteredReadStream(Stream inner, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _maxBytes = maxBytes;
    }

    /// <summary>Bytes read so far.</summary>
    public long BytesRead { get; private set; }

    /// <summary>SHA-256 of the bytes read, as <see cref="ArtifactHash.Format" /> writes it. Call once, after the last read.</summary>
    public string Sha256 => _sha256 ??= ArtifactHash.Format(_hash.GetHashAndReset());

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        Count(buffer.AsSpan(offset, Math.Max(n, 0)));
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        Count(buffer[..Math.Max(n, 0)]);
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var n = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Count(buffer.AsSpan(offset, Math.Max(n, 0)));
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Count(buffer.Span[..Math.Max(n, 0)]);
        return n;
    }

    private void Count(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        BytesRead += data.Length;
        if (BytesRead > _maxBytes) throw new ArtifactObjectTooLargeException(_maxBytes);
        _hash.AppendData(data);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }
}