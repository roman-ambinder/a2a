using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using A2A.Diagnostics;
using A2A.Server.Retention;
using A2A.Server.Storage;
using A2A.Server.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.Server.Uploads;

/// <summary>
///     Inbound file uploads on the <see cref="ArtifactObjectStore" />: the upload with its quota, the check and reference
///     a message
///     leaves on a file it refers to, the owner's read, and the purge of files nobody used. The store is the source of
///     truth for the
///     quota, so it holds across instances; only the number of uploads running at once is kept per instance. The caller's
///     own tenant
///     and principal build every key, so another owner's file is never found: there is no owner field to get wrong.
/// </summary>
internal sealed class InboundFiles
{
    /// <summary>The one text every refused upload link gets, so a refusal tells nothing about another owner's files.</summary>
    internal const string LinkRefusedText =
        "The message refers to an uploaded file that does not exist, is not yours, or has expired.";

    private const string DefaultMediaType = "application/octet-stream";
    private const int MaxFileNameLength = 255;

    /// <summary>
    ///     Longest percent-encoded file name: providers keep the name that way in object metadata, and S3 allows 2 KiB in
    ///     all.
    /// </summary>
    private const int MaxEncodedFileNameLength = 1024;

    private static readonly TimeSpan ConcurrencyRetryAfter = TimeSpan.FromSeconds(1);
    private readonly ILogger _logger;
    private readonly ArtifactUploadOptions _options;
    private readonly Dictionary<(string Tenant, string Principal), int> _running = new();
    private readonly ArtifactStorageOptions _storage;

    private readonly ArtifactObjectStore _store;
    private readonly TimeProvider _time;

    /// <summary>Creates the service.</summary>
    /// <exception cref="InvalidOperationException">An option is out of range.</exception>
    public InboundFiles(ArtifactObjectStore store, ArtifactStorageOptions storage, TimeProvider time,
        ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? NullLogger.Instance;
        _options = storage.Uploads;
        _options.Validate(storage);
    }

    // =====================================================================
    // Upload
    // =====================================================================

    /// <summary>
    ///     Streams <paramref name="content" /> to the store as a new upload of the caller and returns the
    ///     <see cref="UrlPart" /> that stands
    ///     for it (<c>sha256</c> and <c>sizeBytes</c> in its metadata). Never buffers the content.
    /// </summary>
    /// <param name="call">The caller. It must be authenticated, and its tenant is the owner's tenant.</param>
    /// <param name="content">The bytes. Not disposed.</param>
    /// <param name="mediaType">The media type, or null for <c>application/octet-stream</c>.</param>
    /// <param name="fileName">The file name as sent. Any path is removed. Null or empty for none.</param>
    /// <param name="declaredLength">
    ///     The announced length (<c>Content-Length</c>), or null. A different number of bytes is
    ///     refused.
    /// </param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <exception cref="A2AException">
    ///     Unauthenticated; InvalidParams (file name, tenant, a length that differs from <paramref name="declaredLength" />);
    ///     PayloadTooLarge
    ///     (more than <see cref="ArtifactUploadOptions.MaxUploadBytes" />); RateLimited (quota or concurrency, with
    ///     <see cref="A2AException.RetryAfter" />).
    /// </exception>
    public async ValueTask<UrlPart> UploadAsync(ServerCallContext call, Stream content, string? mediaType,
        string? fileName, long? declaredLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(content);
        RequireCaller(call);
        var tenant = call.Tenant;
        if (tenant is "_" or "." or "..")
            throw A2AException.InvalidParams($"The tenant '{tenant}' cannot hold uploads.");
        fileName = CleanFileName(fileName);
        mediaType = string.IsNullOrEmpty(mediaType) ? DefaultMediaType : mediaType;
        var max = _options.MaxUploadBytes;
        if (declaredLength > max) throw A2AException.PayloadTooLarge(max);

        var slot = (tenant, call.Principal);
        if (!TryEnter(slot)) throw A2AException.RateLimited(ConcurrencyRetryAfter);
        try
        {
            using var activity = ServerTelemetry.Source.StartActivity(ServerTelemetry.UploadSpanName);
            if (activity is { IsAllDataRequested: true } && tenant.Length > 0)
                activity.SetTag(A2ATelemetry.Tags.Tenant, tenant);
            try
            {
                return await UploadCoreAsync(tenant, call.Principal, content, mediaType, fileName, declaredLength,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
                throw;
            }
        }
        finally
        {
            Exit(slot);
        }
    }

    private async ValueTask<UrlPart> UploadCoreAsync(string tenant, string principal, Stream content, string mediaType,
        string? fileName,
        long? declaredLength, CancellationToken cancellationToken)
    {
        var prefix = UploadObjectKey.UploadPrefix(tenant, UploadObjectKey.OwnerHash(principal));
        var (count, bytes) = await TotalAsync(prefix, cancellationToken).ConfigureAwait(false);
        if (count >= _options.MaxUploadsPerCaller || bytes >= _options.MaxStoredBytesPerCaller) throw QuotaExceeded();

        // The write is capped by the smaller of the two limits. Passing the quota limit is 429 (try again later), the size limit 413.
        var room = _options.MaxStoredBytesPerCaller - bytes;
        if (declaredLength > room) throw QuotaExceeded(); // known now: do not take the bytes first
        var quotaLimits = room < _options.MaxUploadBytes;
        var objectId = ArtifactObjectKey.NewObjectId();
        var key = UploadObjectKey.CreateUpload(tenant, UploadObjectKey.OwnerHash(principal), objectId);
        ArtifactObjectWriteResult written;
        try
        {
            written = await _store.PutAsync(key, content, new ArtifactObjectInfo(mediaType, fileName),
                    quotaLimits ? room : _options.MaxUploadBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArtifactObjectTooLargeException)
        {
            throw quotaLimits ? QuotaExceeded() : A2AException.PayloadTooLarge(_options.MaxUploadBytes);
        }

        // From here on the object exists: every refusal deletes it, also when the caller is gone (hence no cancellation token).
        try
        {
            if (declaredLength is { } declared && declared != written.SizeBytes)
                throw A2AException.InvalidParams("The body is not as long as its Content-Length says.");
            // Another upload (on this or another instance) may have been written meanwhile. This object is part of the listing now.
            var (countAfter, bytesAfter) = await TotalAsync(prefix, cancellationToken).ConfigureAwait(false);
            if (countAfter > _options.MaxUploadsPerCaller || bytesAfter > _options.MaxStoredBytesPerCaller)
                throw QuotaExceeded();
        }
        catch
        {
            await DeleteQuietlyAsync(key).ConfigureAwait(false);
            throw;
        }

        ServerTelemetry.ObjectsWritten.Add(1);
        ServerTelemetry.ObjectBytesWritten.Add(written.SizeBytes);
        var meta = new Dictionary<string, JsonElement>
        {
            [ArtifactOffload.Sha256Key] =
                ArtifactOffload.Literal($"\"{written.Sha256}\""), // "sha256:" and lowercase hex: nothing to escape
            [ArtifactOffload.SizeBytesKey] =
                ArtifactOffload.Literal(written.SizeBytes.ToString(CultureInfo.InvariantCulture))
        };
        return new UrlPart(ArtifactLink.CreateUpload(BaseUrl(tenant), tenant, objectId))
            { MediaType = mediaType, Filename = fileName, Metadata = meta };
    }

    private A2AException QuotaExceeded()
    {
        return A2AException.RateLimited(_storage.OrphanSweepInterval);
    }

    private async ValueTask DeleteQuietlyAsync(string key)
    {
        try
        {
            await _store.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The sweep deletes an upload that no message ever used; the refusal the caller gets is the real error.
        }
    }

    /// <summary>The number and total size of the uploads under <paramref name="prefix" />.</summary>
    private async ValueTask<(int Count, long Bytes)> TotalAsync(string prefix, CancellationToken cancellationToken)
    {
        var count = 0;
        long bytes = 0;
        await foreach (var entry in _store.ListAsync(prefix, cancellationToken).ConfigureAwait(false))
        {
            if (!UploadObjectKey.TryParseUpload(entry.Key, out _, out _, out _)) continue;
            count++;
            bytes += entry.SizeBytes ?? await SizeOfAsync(entry.Key, cancellationToken).ConfigureAwait(false);
        }

        return (count, bytes);
    }

    /// <summary>The size of an object for a store whose listing gives none: a zero-length range read reports the whole size.</summary>
    private async ValueTask<long> SizeOfAsync(string key, CancellationToken cancellationToken)
    {
        var content = await _store.OpenReadAsync(key, new ArtifactByteRange(0, 0), cancellationToken)
            .ConfigureAwait(false);
        if (content is null) return 0; // deleted since the listing
        await using (content.ConfigureAwait(false))
        {
            return content.TotalLength;
        }
    }

    private bool TryEnter((string Tenant, string Principal) slot)
    {
        lock (_running)
        {
            _running.TryGetValue(slot, out var running);
            if (running >= _options.MaxConcurrentUploadsPerCaller) return false;
            _running[slot] = running + 1;
            return true;
        }
    }

    private void Exit((string Tenant, string Principal) slot)
    {
        lock (_running)
        {
            if (!_running.TryGetValue(slot, out var running)) return;
            if (running <= 1) _running.Remove(slot);
            else _running[slot] = running - 1;
        }
    }

    /// <summary>
    ///     The file name without any path, or null for none. A name longer than 255 characters (or 1024 percent-encoded), with
    ///     a control character, or that is
    ///     <c>.</c> or <c>..</c> is refused.
    /// </summary>
    internal static string? CleanFileName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var c in name)
            if (char.IsControl(c))
                throw A2AException.InvalidParams("The file name must not contain control characters.");
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.Length == 0) return null;
        if (name.Length > MaxFileNameLength)
            throw A2AException.InvalidParams($"The file name must be at most {MaxFileNameLength} characters.");
        if (Uri.EscapeDataString(name).Length > MaxEncodedFileNameLength)
            throw A2AException.InvalidParams("The file name is too long.");
        if (name is "." or "..") throw A2AException.InvalidParams("The file name is not valid.");
        return name;
    }

    // =====================================================================
    // Messages
    // =====================================================================

    /// <summary>
    ///     Checks every upload link of the message and writes the reference of task <paramref name="taskId" /> on each upload,
    ///     so the
    ///     purge keeps it while the task lives. Runs before anything is committed: the agent never sees a message whose
    ///     references
    ///     were not written. A URL under <c>{base}/uploads/</c> of the caller's tenant must be the exact canonical link of an
    ///     upload
    ///     the caller owns that is not older than <see cref="ArtifactUploadOptions.UnusedUploadTtl" />; anything else is
    ///     refused with
    ///     the one text <see cref="LinkRefusedText" />. Other URLs are left alone. Writes the references again without harm
    ///     when it runs twice.
    /// </summary>
    /// <param name="call">The caller, with its tenant bound.</param>
    /// <param name="taskId">The task the message belongs to. The caller must own it already.</param>
    /// <param name="message">The new message.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="A2AException">
    ///     InvalidParams: a link is refused, or the message has more distinct upload links than
    ///     <see cref="ArtifactUploadOptions.MaxUploadsPerCaller" />.
    /// </exception>
    public async ValueTask AttachAsync(ServerCallContext call, string taskId, Message message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(message);
        List<string>? ids = null;
        Uri? baseUrl = null;
        foreach (var part in message.Parts)
        {
            if (part is not UrlPart { Url: var url } || !url.IsAbsoluteUri) continue;
            baseUrl ??= BaseUrl(call.Tenant);
            if (!ArtifactLink.IsUnderUploads(url, baseUrl)) continue;
            var id = CanonicalObjectId(call.Tenant, baseUrl, url) ?? throw A2AException.InvalidParams(LinkRefusedText);
            ids ??= [];
            if (!ids.Contains(id)) ids.Add(id);
        }

        if (ids is null) return;
        if (ids.Count > _options.MaxUploadsPerCaller)
            throw A2AException.InvalidParams(
                $"A message can refer to at most {_options.MaxUploadsPerCaller} uploaded files.");
        // Anonymous callers cannot upload, and all of them share one principal: they own nothing.
        if (!call.IsAuthenticated) throw A2AException.InvalidParams(LinkRefusedText);

        var ownerHash = UploadObjectKey.OwnerHash(call.Principal);
        var now = _time.GetUtcNow();
        foreach (var id in ids)
        {
            var key = UploadObjectKey.CreateUpload(call.Tenant, ownerHash, id);
            DateTimeOffset? created = null;
            await foreach (var entry in _store.ListAsync(key, cancellationToken).ConfigureAwait(false))
                if (entry.Key == key)
                    created = entry.CreatedAt;
            if (created is not { } at || now - at > _options.UnusedUploadTtl)
                throw A2AException.InvalidParams(LinkRefusedText);
        }

        foreach (var id in ids)
        {
            using var empty = new MemoryStream([], false);
            // One byte of room is enough for an empty body, and keeps a provider from treating 0 as "no limit".
            await _store.PutAsync(UploadObjectKey.CreateReference(call.Tenant, ownerHash, id, taskId), empty,
                    new ArtifactObjectInfo(null, null), 1, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    // =====================================================================
    // Reads
    // =====================================================================

    /// <summary>
    ///     Opens the caller's own upload <paramref name="objectId" />. Returns null when there is none under the caller's key:
    ///     another owner's
    ///     file, another tenant's, a missing one and a malformed id all look the same. The caller disposes the result.
    /// </summary>
    /// <exception cref="A2AException">Unauthenticated.</exception>
    public ValueTask<ArtifactObjectContent?> OpenAsync(ServerCallContext call, string objectId,
        ArtifactByteRange? range, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(objectId);
        RequireCaller(call);
        // "_" is the link spelling of the default tenant, so it is no tenant of its own and has no keys.
        if (call.Tenant == "_" || !ArtifactObjectKey.IsValidObjectId(objectId))
            return ValueTask.FromResult<ArtifactObjectContent?>(null);
        var key = UploadObjectKey.CreateUpload(call.Tenant, UploadObjectKey.OwnerHash(call.Principal), objectId);
        return _store.OpenReadAsync(key, range, cancellationToken);
    }

    /// <summary>
    ///     Opens the upload a part refers to, for the agent. Returns null for a part that is not the exact canonical link of
    ///     an upload
    ///     under the caller's key (an external URL, a file of another owner or tenant, a missing file). The caller disposes
    ///     the result.
    /// </summary>
    /// <exception cref="A2AException">Unauthenticated.</exception>
    public ValueTask<ArtifactObjectContent?> OpenAsync(ServerCallContext call, UrlPart part, ArtifactByteRange? range,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(part);
        RequireCaller(call);
        if (!part.Url.IsAbsoluteUri || call.Tenant is "_" or "." or "..")
            return ValueTask.FromResult<ArtifactObjectContent?>(null);
        var id = CanonicalObjectId(call.Tenant, BaseUrl(call.Tenant), part.Url);
        return id is null
            ? ValueTask.FromResult<ArtifactObjectContent?>(null)
            : OpenAsync(call, id, range, cancellationToken);
    }

    // =====================================================================
    // Purge
    // =====================================================================

    /// <summary>
    ///     Deletes the uploads nobody uses, and their references, at most <paramref name="batchSize" /> objects per call.
    ///     Considers
    ///     uploads older than <see cref="ArtifactUploadOptions.UnusedUploadTtl" /> plus
    ///     <see cref="ArtifactStorageOptions.OrphanGracePeriod" />:
    ///     the attach window closed at the TTL, so a reference written at the last moment is always seen. For each reference:
    ///     one younger
    ///     than the grace period keeps the upload (its commit may be in flight); a task that exists and refers to the link
    ///     keeps it;
    ///     otherwise the reference is deleted. An upload left with no live reference is deleted, after its references (so a
    ///     crash leaves
    ///     an upload with none, which the next sweep deletes). Then references older than the grace period whose upload is
    ///     gone are deleted.
    ///     A task that cannot be read, or a failed delete, keeps the upload for this sweep. Returns true when it stopped at
    ///     the batch size
    ///     with more to do.
    /// </summary>
    public async Task<bool> SweepAsync(TaskStore tasks, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var now = _time.GetUtcNow();
        var unusedCutoff = A2ARetentionService.Before(now, _options.UnusedUploadTtl + _storage.OrphanGracePeriod);
        var referenceCutoff = A2ARetentionService.Before(now, _storage.OrphanGracePeriod);
        var deleted = 0;

        await foreach (var entry in _store.ListAsync(UploadObjectKey.UploadsPrefix, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (entry.CreatedAt >= unusedCutoff) continue;
            if (!UploadObjectKey.TryParseUpload(entry.Key, out var tenant, out var ownerHash, out var objectId))
                continue;
            if (deleted >= batchSize) return true;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var references = new List<ArtifactObjectEntry>();
                await foreach (var reference in _store
                                   .ListAsync(UploadObjectKey.ReferencePrefix(tenant, ownerHash, objectId),
                                       cancellationToken).ConfigureAwait(false))
                    references.Add(reference);
                var keep = false;
                foreach (var reference in references)
                {
                    if (!UploadObjectKey.TryParseReference(reference.Key, out _, out _, out _, out var taskId))
                        continue;
                    if (reference.CreatedAt >= referenceCutoff // the commit of its message may be in flight
                        || await TaskRefersAsync(tasks, tenant, taskId, objectId, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        keep = true;
                        continue;
                    }

                    if (deleted >= batchSize) return true;
                    await _store.DeleteAsync(reference.Key, cancellationToken).ConfigureAwait(false);
                    deleted++;
                }

                if (keep) continue;
                if (deleted >= batchSize) return true;
                await _store.DeleteAsync(entry.Key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException ||
                                       !cancellationToken.IsCancellationRequested)
            {
                ServerLog.UploadCheckFailed(_logger, ex, entry.Key);
                continue;
            }

            deleted++;
            ServerTelemetry.UploadsPurged.Add(1);
            ServerLog.UploadPurged(_logger, entry.Key);
        }

        await foreach (var reference in _store.ListAsync(UploadObjectKey.ReferencesPrefix, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (reference.CreatedAt >= referenceCutoff) continue;
            if (!UploadObjectKey.TryParseReference(reference.Key, out var tenant, out var ownerHash, out var objectId,
                    out _)) continue;
            if (deleted >= batchSize) return true;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await _store
                        .ExistsAsync(UploadObjectKey.CreateUpload(tenant, ownerHash, objectId), cancellationToken)
                        .ConfigureAwait(false)) continue;
                await _store.DeleteAsync(reference.Key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException ||
                                       !cancellationToken.IsCancellationRequested)
            {
                ServerLog.UploadCheckFailed(_logger, ex, reference.Key);
                continue;
            }

            deleted++;
        }

        return false;
    }

    /// <summary>
    ///     True when the task exists (whoever owns it) and a part of its history, artifacts or status message links to
    ///     the upload.
    /// </summary>
    private static async ValueTask<bool> TaskRefersAsync(TaskStore tasks, string tenant, string taskId, string objectId,
        CancellationToken cancellationToken)
    {
        var record = await tasks.GetForMaintenanceAsync(tenant, taskId, cancellationToken).ConfigureAwait(false);
        if (record is null) return false;
        var task = record.Task;
        if (task.History is { } history)
            foreach (var message in history)
                if (Refers(message.Parts, objectId))
                    return true;
        if (task.Artifacts is { } artifacts)
            foreach (var artifact in artifacts)
                if (Refers(artifact.Parts, objectId))
                    return true;
        return task.Status?.Message is { } status && Refers(status.Parts, objectId);
    }

    private static bool Refers(IEnumerable<Part>? parts, string objectId)
    {
        if (parts is null) return false;
        foreach (var part in parts)
            if (part is UrlPart url && ArtifactLink.TryGetUploadObjectId(url.Url, out var id) && id == objectId)
                return true;
        return false;
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    /// <summary>
    ///     The object id of <paramref name="url" /> when it is exactly the canonical upload link of the tenant; otherwise
    ///     null.
    /// </summary>
    private static string? CanonicalObjectId(string tenant, Uri baseUrl, Uri url)
    {
        if (!ArtifactLink.TryGetUploadObjectId(url, out var id)) return null;
        try
        {
            var canonical = ArtifactLink.CreateUpload(baseUrl, tenant, id);
            return string.Equals(url.AbsoluteUri, canonical.AbsoluteUri, StringComparison.Ordinal) ? id : null;
        }
        catch (ArgumentException)
        {
            return null; // a tenant that has no link form
        }
    }

    /// <summary>
    ///     Uploads, and reads of them, need a known caller: all anonymous callers share one principal and could read each
    ///     other's files.
    /// </summary>
    private static void RequireCaller(ServerCallContext call)
    {
        if (!call.IsAuthenticated) throw new A2AException(A2AErrorCode.Unauthenticated);
    }

    private Uri BaseUrl(string tenant)
    {
        return _storage.PublicBaseUrlResolver?.Invoke(tenant) ?? _storage.PublicBaseUrl
            ?? throw new InvalidOperationException(
                "ArtifactStorage.PublicBaseUrl or PublicBaseUrlResolver is required when an ArtifactObjectStore is registered.");
    }
}