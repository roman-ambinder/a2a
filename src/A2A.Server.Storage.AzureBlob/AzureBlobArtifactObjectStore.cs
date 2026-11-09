using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using A2A.Integrity;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;

namespace A2A.Server.Storage.AzureBlob;

/// <summary>
/// Stores artifact objects in an Azure Blob Storage container as block blobs. An upload stages one 8 MiB block at a time, so a
/// non-seekable stream never needs a full buffer, and commits the block list at the end: nothing is visible before the commit,
/// so a failed or oversized upload leaves no blob (staged blocks that were never committed are discarded by the service).
/// Content type and file name are kept with the blob. Downloads can be redirected to a SAS URL
/// (<see cref="TryCreateDownloadUrlAsync"/>) that forces <c>Content-Disposition: attachment</c>.
/// </summary>
/// <remarks>
/// The credentials need to read, write, list and delete blobs in the container. SAS links need a shared key (the account key
/// in the connection string, or a <see cref="BlobContainerClient"/> built with a <see cref="StorageSharedKeyCredential"/>).
/// </remarks>
public sealed class AzureBlobArtifactObjectStore : ArtifactObjectStore
{
    private const int BlockSize = 8 * 1024 * 1024;
    private const string FileNameMetadata = "filename";

    private readonly BlobContainerClient _container;
    private readonly string _keyPrefix;

    /// <summary>Creates the store from a container client, or from <see cref="AzureBlobArtifactStoreOptions.ConnectionString"/> and <see cref="AzureBlobArtifactStoreOptions.ContainerName"/>.</summary>
    /// <param name="options">The settings.</param>
    public AzureBlobArtifactObjectStore(AzureBlobArtifactStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ContainerClient is { } client)
        {
            _container = client;
        }
        else
        {
            if (string.IsNullOrEmpty(options.ContainerName) || string.IsNullOrEmpty(options.ConnectionString))
                throw new ArgumentException("Set ContainerClient, or both ContainerName and ConnectionString.", nameof(options));
            _container = new BlobContainerClient(options.ConnectionString, options.ContainerName);
        }
        // A prefix always ends with "/", so "prod" does not also cover "prod2".
        _keyPrefix = options.KeyPrefix is { Length: > 0 } prefix ? (prefix.EndsWith('/') ? prefix : prefix + "/") : "";
    }

    /// <inheritdoc />
    public override async ValueTask<ArtifactObjectWriteResult> PutAsync(string key, Stream content, ArtifactObjectInfo info, long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(info);
        var blob = _container.GetBlockBlobClient(ToBlobName(key));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
        var blockIds = new List<string>();
        var committing = false;
        try
        {
            while (true)
            {
                // One block from the stream, counted and hashed. It never reads past maxBytes + 1, so an oversized stream stops early.
                var room = maxBytes - total; // may be long.MaxValue
                var want = room >= BlockSize ? BlockSize : (int)Math.Max(room + 1, 0);
                var n = want == 0 ? 0 : await FillAsync(content, buffer, want, cancellationToken).ConfigureAwait(false);
                total += n;
                if (total > maxBytes) throw new ArtifactObjectTooLargeException(maxBytes);
                if (n == 0) break;
                hash.AppendData(buffer, 0, n);

                // Block ids must all have the same length: a 4-byte counter, base64.
                var idBytes = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(idBytes, blockIds.Count);
                var id = Convert.ToBase64String(idBytes);
                using var body = new MemoryStream(buffer, 0, n, writable: false);
                await blob.StageBlockAsync(id, body, cancellationToken: cancellationToken).ConfigureAwait(false);
                blockIds.Add(id);
                if (n < BlockSize) break;
            }

            var options = new CommitBlockListOptions { HttpHeaders = new BlobHttpHeaders { ContentType = info.ContentType } };
            if (info.FileName is not null)
            {
                // Metadata travels in HTTP headers, so keep it ASCII.
                options.Metadata = new Dictionary<string, string> { [FileNameMetadata] = Uri.EscapeDataString(info.FileName) };
            }
            committing = true;
            await blob.CommitBlockListAsync(blockIds, options, cancellationToken).ConfigureAwait(false);
            return new ArtifactObjectWriteResult(total, ArtifactHash.Format(hash.GetHashAndReset()));
        }
        catch
        {
            // Before the commit nothing is visible. A commit that fails (or is cancelled) may still have taken effect on the
            // service, so the blob is removed; the original failure is the one to report.
            if (committing) await DeleteQuietlyAsync(blob).ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task DeleteQuietlyAsync(BlockBlobClient blob)
    {
        try
        {
            // The caller may have cancelled, so the cleanup must not use their token.
            await blob.DeleteIfExistsAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing more to do; the original failure is the one to report.
        }
    }

    /// <summary>The blob name for a store key.</summary>
    private string ToBlobName(string key) => _keyPrefix + key;

    private string FromBlobName(string name) => name.Substring(_keyPrefix.Length);

    /// <summary>Reads until the buffer holds <paramref name="count"/> bytes or the stream ends. Returns the byte count.</summary>
    private static async Task<int> FillAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <inheritdoc />
    public override async ValueTask<ArtifactObjectContent?> OpenReadAsync(string key, ArtifactByteRange? range = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (range is { Offset: < 0 } or { Length: < 0 }) throw new ArgumentOutOfRangeException(nameof(range));
        var blob = _container.GetBlobClient(ToBlobName(key));
        if (range is { Length: 0 }) return await OpenEmptyAsync(blob, cancellationToken).ConfigureAwait(false);

        var options = new BlobDownloadOptions();
        if (range is not null) options.Range = new HttpRange(range.Offset, range.Length);
        try
        {
            var response = (await blob.DownloadStreamingAsync(options, cancellationToken).ConfigureAwait(false)).Value;
            var details = response.Details;
            return new ArtifactObjectContent(response.Content, details.ContentType, FileNameOf(details.Metadata), details.ContentLength, TotalOf(details));
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The range starts at or past the end, or the blob is empty: the clamped result is no bytes.
            return await OpenEmptyAsync(blob, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<ArtifactObjectContent?> OpenEmptyAsync(BlobClient blob, CancellationToken cancellationToken)
    {
        try
        {
            var properties = (await blob.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
            return new ArtifactObjectContent(Stream.Null, properties.ContentType, FileNameOf(properties.Metadata), 0, properties.ContentLength);
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static long TotalOf(BlobDownloadDetails details)
    {
        // "bytes 100-149/1000" on a range read; without a range the whole blob came back.
        var contentRange = details.ContentRange;
        if (contentRange is not null && contentRange.LastIndexOf('/') is var slash and >= 0
            && long.TryParse(contentRange.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var total))
            return total;
        return details.ContentLength;
    }

    private static string? FileNameOf(IDictionary<string, string>? metadata) =>
        metadata is not null && metadata.TryGetValue(FileNameMetadata, out var value) ? Uri.UnescapeDataString(value) : null;

    /// <inheritdoc />
    public override async ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return (await _container.GetBlobClient(ToBlobName(key)).ExistsAsync(cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <inheritdoc />
    public override async ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await _container.GetBlobClient(ToBlobName(key)).DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ArtifactObjectEntry> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        // Only committed blobs are listed. Last-Modified is set by Put Block List, so for a block blob it is the moment the
        // commit finished: Put Block (staging) does not change it, and the blob does not exist before the commit. (Creation-Time
        // is not used: the service does not document it as the commit time.) Nothing here changes a blob afterwards, which
        // would move Last-Modified. A listing error is thrown, never turned into a date.
        await foreach (var item in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, ToBlobName(prefix), cancellationToken).ConfigureAwait(false))
        {
            var modified = item.Properties.LastModified
                ?? throw new InvalidOperationException($"The blob listing gave no Last-Modified date for '{item.Name}'.");
            yield return new ArtifactObjectEntry(FromBlobName(item.Name), modified) { SizeBytes = item.Properties.ContentLength };
        }
    }

    /// <inheritdoc />
    public override ValueTask<Uri?> TryCreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var blob = _container.GetBlobClient(ToBlobName(key));
        if (!blob.CanGenerateSasUri) return ValueTask.FromResult<Uri?>(null);
        var now = DateTimeOffset.UtcNow;
        var sas = new BlobSasBuilder(BlobSasPermissions.Read, now + lifetime)
        {
            BlobContainerName = blob.BlobContainerName,
            BlobName = blob.Name,
            Resource = "b",
            // A little before now, so a slightly slow clock on the storage side still accepts the link.
            StartsOn = now - TimeSpan.FromMinutes(1),
            // Redirect is the default delivery, so without this a stored HTML file would render inline from the storage origin.
            ContentDisposition = "attachment",
        };
        return ValueTask.FromResult<Uri?>(blob.GenerateSasUri(sas));
    }
}
