using System.Buffers;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using A2A.Integrity;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace A2A.Server.Storage.S3;

/// <summary>
///     Stores artifact objects in an Amazon S3 bucket, or in any S3-compatible service. Uploads are multipart and buffer
///     one 8 MiB
///     part at a time, so a non-seekable stream never needs a full buffer; a stream shorter than one part is a single PUT.
///     A failed
///     or oversized upload is aborted and leaves no object. Content type and file name are kept as object metadata.
///     Downloads can
///     be redirected to a presigned URL (<see cref="TryCreateDownloadUrlAsync" />) that forces
///     <c>Content-Disposition: attachment</c>.
/// </summary>
/// <remarks>
///     The credentials need <c>s3:PutObject</c>, <c>s3:GetObject</c>, <c>s3:DeleteObject</c>, <c>s3:ListBucket</c>,
///     <c>s3:AbortMultipartUpload</c> and <c>s3:PutObjectTagging</c> / <c>s3:GetObjectTagging</c> (see <c>CreatedAt</c> in
///     the README).
/// </remarks>
public sealed class S3ArtifactObjectStore : ArtifactObjectStore, IDisposable
{
    /// <summary>S3 needs every part but the last to be at least 5 MiB.</summary>
    private const int PartSize = 8 * 1024 * 1024;

    private const string FileNameMetadata = "filename";
    private const string CompletedAtTag = "a2a-completed-at";


    /// <summary>The marker for an empty segment. It cannot come out of percent-encoding, so no key spells it by accident.</summary>
    private const string EmptySegment = "%-";

    private readonly string _bucket;

    private readonly IAmazonS3 _client;
    private readonly string _keyPrefix;
    private readonly bool _ownsClient;

    /// <summary>
    ///     Creates the store. When <see cref="S3ArtifactStoreOptions.Client" /> is null it makes a client of its own and
    ///     disposes it with the store.
    /// </summary>
    /// <param name="options">The settings. The bucket name is required.</param>
    public S3ArtifactObjectStore(S3ArtifactStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.BucketName, nameof(options));
        _bucket = options.BucketName;
        // A prefix always ends with "/", so "prod" does not also cover "prod2".
        _keyPrefix = options.KeyPrefix is { Length: > 0 } prefix ? prefix.EndsWith('/') ? prefix : prefix + "/" : "";
        if (options.Client is { } client)
        {
            _client = client;
        }
        else
        {
            var config = new AmazonS3Config
            {
                ForcePathStyle = options.ForcePathStyle,
                // Many S3-compatible services reject the checksum trailers the SDK adds by default.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
            };
            if (options.ServiceUrl is { Length: > 0 } url)
            {
                config.ServiceURL = url;
                // Signing needs a region; a custom endpoint has none of its own.
                config.AuthenticationRegion = "us-east-1";
            }

            _client = new AmazonS3Client(config);
            _ownsClient = true;
        }
    }

    /// <summary>
    ///     Disposes the client when the store made it. A client passed in <see cref="S3ArtifactStoreOptions.Client" /> is
    ///     the caller's.
    /// </summary>
    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    /// <inheritdoc />
    public override async ValueTask<ArtifactObjectWriteResult> PutAsync(string key, Stream content,
        ArtifactObjectInfo info, long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(info);
        var fullKey = ToS3Key(key);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(PartSize);
        string? uploadId = null;
        var committing = false; // the call that makes the object visible is under way

        // One part from the stream, counted and hashed. It never reads past maxBytes + 1, so an oversized stream stops early.
        async Task<int> ReadPartAsync()
        {
            var room = maxBytes - total; // may be long.MaxValue
            var want = room >= PartSize ? PartSize : (int)Math.Max(room + 1, 0);
            var n = want == 0 ? 0 : await FillAsync(content, buffer, want, cancellationToken).ConfigureAwait(false);
            total += n;
            if (total > maxBytes) throw new ArtifactObjectTooLargeException(maxBytes);
            hash.AppendData(buffer, 0, n);
            return n;
        }

        try
        {
            var filled = await ReadPartAsync().ConfigureAwait(false);
            if (filled < PartSize)
            {
                // Short stream (or empty): one PUT.
                using var body = new MemoryStream(buffer, 0, filled, false);
                var put = new PutObjectRequest
                {
                    BucketName = _bucket, Key = fullKey, InputStream = body, ContentType = info.ContentType,
                    AutoCloseStream = false
                };
                AddFileName(put.Metadata, info);
                committing = true;
                await _client.PutObjectAsync(put, cancellationToken).ConfigureAwait(false);
                return new ArtifactObjectWriteResult(total, ArtifactHash.Format(hash.GetHashAndReset()));
            }

            var init = new InitiateMultipartUploadRequest
                { BucketName = _bucket, Key = fullKey, ContentType = info.ContentType };
            AddFileName(init.Metadata, info);
            uploadId = (await _client.InitiateMultipartUploadAsync(init, cancellationToken).ConfigureAwait(false))
                .UploadId;

            var parts = new List<PartETag>();
            while (filled > 0)
            {
                var number = parts.Count + 1;
                using var body = new MemoryStream(buffer, 0, filled, false);
                var part = await _client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = _bucket, Key = fullKey, UploadId = uploadId, PartNumber = number, InputStream = body,
                    PartSize = filled
                }, cancellationToken).ConfigureAwait(false);
                parts.Add(new PartETag(number, part.ETag));
                filled = await ReadPartAsync().ConfigureAwait(false);
            }

            committing = true;
            await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _bucket, Key = fullKey, UploadId = uploadId, PartETags = parts
            }, cancellationToken).ConfigureAwait(false);
            uploadId = null;
            committing = false; // the stamp below deletes the object itself when it fails
            try
            {
                await StampCompletionAsync(fullKey).ConfigureAwait(false);
            }
            catch
            {
                // Without its stamp the object would look as old as the upload start for ever, so it is not kept.
                await DeleteQuietlyAsync(fullKey).ConfigureAwait(false);
                throw;
            }

            return new ArtifactObjectWriteResult(total, ArtifactHash.Format(hash.GetHashAndReset()));
        }
        catch
        {
            if (uploadId is not null) await AbortAsync(fullKey, uploadId).ConfigureAwait(false);
            // A PUT or a CompleteMultipartUpload that fails (or is cancelled) may still have taken effect on the service, as
            // with a lost response: remove the object, as the Azure store does. The original failure is the one to report.
            if (committing) await DeleteQuietlyAsync(fullKey).ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, true);
        }
    }

    /// <summary>
    ///     The bucket key for a store key. A key with an empty segment (<c>t//id</c>, an empty task id) is valid in S3 but
    ///     some
    ///     S3-compatible services refuse <c>//</c>, so an empty segment is written as <see cref="EmptySegment" />. The last
    ///     segment of a prefix stays empty: it is what makes the prefix end at a slash.
    /// </summary>
    private string ToS3Key(string key, bool isPrefix = false)
    {
        if (!key.Contains("//", StringComparison.Ordinal) && !key.StartsWith('/')) return _keyPrefix + key;
        var segments = key.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
            if (segments[i].Length == 0)
                segments[i] = EmptySegment;
        if (!isPrefix && segments[^1].Length == 0) segments[^1] = EmptySegment;
        return _keyPrefix + string.Join('/', segments);
    }

    private string FromS3Key(string fullKey)
    {
        return string.Join('/',
            fullKey.Substring(_keyPrefix.Length).Split('/').Select(segment => segment == EmptySegment ? "" : segment));
    }

    /// <summary>Reads until the buffer holds <paramref name="count" /> bytes or the stream ends. Returns the byte count.</summary>
    private static async Task<int> FillAsync(Stream stream, byte[] buffer, int count,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }

        return total;
    }

    private static void AddFileName(MetadataCollection metadata, ArtifactObjectInfo info)
    {
        // Metadata travels in HTTP headers, so keep it ASCII.
        if (info.FileName is not null) metadata[FileNameMetadata] = Uri.EscapeDataString(info.FileName);
    }

    private async Task AbortAsync(string fullKey, string uploadId)
    {
        try
        {
            // The caller may have cancelled, so the abort must not use their token.
            await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                { BucketName = _bucket, Key = fullKey, UploadId = uploadId }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing more to do; the original failure is the one to report. A bucket lifecycle rule for incomplete uploads cleans up.
        }
    }

    /// <summary>
    ///     A completed multipart object has no write time of its own: S3 dates it from when the upload was initiated (its ETag
    ///     ends
    ///     in <c>-N</c>), which for a slow upload is long before the object exists, and the orphan sweep ages objects by that
    ///     date.
    ///     So the completion time is stored as an object tag, which does not change the object, and <see cref="ListAsync" />
    ///     uses it.
    ///     A failure here fails the upload (the caller deletes the object), so every multipart object has its stamp.
    /// </summary>
    private async Task StampCompletionAsync(string fullKey)
    {
        await _client.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = _bucket,
            Key = fullKey,
            Tagging = new Tagging
            {
                TagSet =
                [
                    new Tag
                    {
                        Key = CompletedAtTag,
                        Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
                    }
                ]
            }
        }).ConfigureAwait(false);
    }

    private async Task DeleteQuietlyAsync(string fullKey)
    {
        try
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = fullKey })
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The failure that led here is the one to report.
        }
    }

    /// <inheritdoc />
    public override async ValueTask<ArtifactObjectContent?> OpenReadAsync(string key, ArtifactByteRange? range = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (range is { Offset: < 0 } or { Length: < 0 }) throw new ArgumentOutOfRangeException(nameof(range));
        var fullKey = ToS3Key(key);
        if (range is { Length: 0 }) return await OpenEmptyAsync(fullKey, cancellationToken).ConfigureAwait(false);

        var request = new GetObjectRequest { BucketName = _bucket, Key = fullKey };
        if (range is not null)
        {
            var end = range.Length is { } length
                ? (range.Offset + length - 1).ToString(CultureInfo.InvariantCulture)
                : "";
            request.ByteRange = new ByteRange($"bytes={range.Offset.ToString(CultureInfo.InvariantCulture)}-{end}");
        }

        try
        {
            var response = await _client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
            var total = TotalOf(response);
            return new ArtifactObjectContent(response.ResponseStream, response.Headers.ContentType,
                FileNameOf(response.Metadata), response.ContentLength, total);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The range starts at or past the end, or the object is empty: the clamped result is no bytes.
            return await OpenEmptyAsync(fullKey, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<ArtifactObjectContent?> OpenEmptyAsync(string fullKey, CancellationToken cancellationToken)
    {
        try
        {
            var head = await _client
                .GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = fullKey },
                    cancellationToken).ConfigureAwait(false);
            return new ArtifactObjectContent(Stream.Null, head.Headers.ContentType, FileNameOf(head.Metadata), 0,
                head.ContentLength);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static long TotalOf(GetObjectResponse response)
    {
        // "bytes 100-149/1000" on a range read; without a range the whole object came back.
        var contentRange = response.ContentRange;
        if (contentRange is not null && contentRange.LastIndexOf('/') is var slash and >= 0
                                     && long.TryParse(contentRange.AsSpan(slash + 1), NumberStyles.None,
                                         CultureInfo.InvariantCulture, out var total))
            return total;
        return response.ContentLength;
    }

    private static string? FileNameOf(MetadataCollection metadata)
    {
        var value = metadata[FileNameMetadata];
        return value is null ? null : Uri.UnescapeDataString(value);
    }

    /// <inheritdoc />
    public override async ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            await _client
                .GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = ToS3Key(key) },
                    cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public override async ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        // S3 answers 204 for a key that is not there.
        await _client
            .DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = ToS3Key(key) }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ArtifactObjectEntry> ListAsync(string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = ToS3Key(prefix, true) };
        ListObjectsV2Response page;
        do
        {
            page = await _client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (var o in page.S3Objects ?? [])
            {
                if (!o.Key.StartsWith(_keyPrefix, StringComparison.Ordinal)) continue;
                // A multipart object's ETag ends in "-<parts>": its S3 date is the upload start, so read the completion stamp.
                var created = o.ETag is not null && o.ETag.Contains('-', StringComparison.Ordinal)
                    ? await CompletionTimeAsync(o.Key, o.LastModified, cancellationToken).ConfigureAwait(false)
                    : AsOffset(o.LastModified);
                yield return new ArtifactObjectEntry(FromS3Key(o.Key), created) { SizeBytes = o.Size };
            }

            request.ContinuationToken = page.NextContinuationToken;
        } while (page.IsTruncated == true);
    }

    private async ValueTask<DateTimeOffset> CompletionTimeAsync(string fullKey, DateTime? lastModified,
        CancellationToken cancellationToken)
    {
        try
        {
            var tags = await _client
                .GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = _bucket, Key = fullKey },
                    cancellationToken).ConfigureAwait(false);
            var stamp = tags.Tagging?.FirstOrDefault(t => t.Key == CompletedAtTag)?.Value;
            if (stamp is not null && long.TryParse(stamp, NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
                return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // The object went away between the listing and now. Any other failure (throttling, network) is rethrown: a wrong
            // date could make a live object look old, and the sweep retries.
        }

        return AsOffset(lastModified);
    }

    private static DateTimeOffset AsOffset(DateTime? value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value ?? DateTime.UtcNow, DateTimeKind.Utc));
    }

    /// <inheritdoc />
    public override ValueTask<Uri?> TryCreateDownloadUrlAsync(string key, TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = ToS3Key(key),
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow + lifetime,
            // Redirect is the default delivery, so without this a stored HTML file would render inline from the bucket's origin.
            ResponseHeaderOverrides = new ResponseHeaderOverrides { ContentDisposition = "attachment" }
        };
        if (_client.Config.ServiceURL is { } serviceUrl &&
            serviceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            request.Protocol = Protocol.HTTP;
        return ValueTask.FromResult<Uri?>(new Uri(_client.GetPreSignedURL(request)));
    }
}