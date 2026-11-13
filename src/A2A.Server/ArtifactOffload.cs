using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using A2A.Diagnostics;
using A2A.Integrity;
using A2A.Server.Storage;
using A2A.Server.Telemetry;

namespace A2A.Server;

/// <summary>
///     Moves the bytes of large raw parts to the <see cref="ArtifactObjectStore" /> before an artifact event is made, so
///     the event, its
///     hash and every reader see an ordinary <see cref="UrlPart" />. One instance serves one task.
/// </summary>
internal sealed class ArtifactOffload(
    ArtifactObjectStore store,
    ArtifactStorageOptions options,
    string tenant,
    string taskId)
{
    /// <summary>Metadata key of a part: SHA-256 of the object's bytes (<see cref="ArtifactHash.Format" />).</summary>
    internal const string Sha256Key = "sha256";

    /// <summary>Metadata key of a part: size of the object in bytes.</summary>
    internal const string SizeBytesKey = "sizeBytes";

    /// <summary>
    ///     Returns <paramref name="parts" /> with every <see cref="RawPart" /> longer than
    ///     <see cref="ArtifactStorageOptions.InlineThresholdBytes" />
    ///     uploaded and replaced by a <see cref="UrlPart" /> (media type, file name and metadata carry over, plus
    ///     <c>sha256</c> and
    ///     <c>sizeBytes</c>). Returns the same list when nothing is replaced.
    /// </summary>
    /// <exception cref="ArtifactObjectTooLargeException">
    ///     A part is longer than
    ///     <see cref="ArtifactStorageOptions.MaxObjectBytes" />.
    /// </exception>
    public async ValueTask<List<Part>> PrepareAsync(List<Part> parts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parts);
        List<Part>? result = null;
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i] is not RawPart { Raw.Length: var length } raw ||
                length <= options.InlineThresholdBytes) continue;
            result ??= new List<Part>(parts);
            result[i] = await UploadPartAsync(raw, cancellationToken).ConfigureAwait(false);
        }

        return result ?? parts;
    }

    /// <summary>
    ///     Uploads a stream and returns the <see cref="UrlPart" /> that stands for it, with <c>sha256</c> and <c>sizeBytes</c>
    ///     in its metadata.
    /// </summary>
    /// <exception cref="ArtifactObjectTooLargeException">
    ///     The stream is longer than
    ///     <see cref="ArtifactStorageOptions.MaxObjectBytes" />.
    /// </exception>
    public async ValueTask<UrlPart> UploadAsync(Stream content, string? mediaType, string? fileName,
        Dictionary<string, JsonElement>? metadata, CancellationToken cancellationToken)
    {
        var objectId = ArtifactObjectKey.NewObjectId();
        var key = ArtifactObjectKey.Create(tenant, taskId, objectId);
        using var activity = ServerTelemetry.Source.StartActivity(ServerTelemetry.UploadSpanName);
        if (activity is { IsAllDataRequested: true })
        {
            A2ATelemetry.SetTask(activity, taskId);
            if (tenant.Length > 0) activity.SetTag(A2ATelemetry.Tags.Tenant, tenant);
        }

        ArtifactObjectWriteResult written;
        try
        {
            written = await store.PutAsync(key, content, new ArtifactObjectInfo(mediaType, fileName),
                options.MaxObjectBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            A2ATelemetry.RecordError(activity, ex, cancellationToken.IsCancellationRequested);
            throw;
        }

        ServerTelemetry.ObjectsWritten.Add(1);
        ServerTelemetry.ObjectBytesWritten.Add(written.SizeBytes);
        var meta = metadata is null
            ? new Dictionary<string, JsonElement>()
            : new Dictionary<string, JsonElement>(metadata);
        meta[Sha256Key] = Literal($"\"{written.Sha256}\""); // "sha256:" and lowercase hex: nothing to escape
        meta[SizeBytesKey] = Literal(written.SizeBytes.ToString(CultureInfo.InvariantCulture));
        return new UrlPart(ArtifactLink.Create(BaseUrl(), tenant, taskId, objectId))
            { MediaType = mediaType, Filename = fileName, Metadata = meta };
    }

    private async ValueTask<UrlPart> UploadPartAsync(RawPart raw, CancellationToken cancellationToken)
    {
        var bytes = raw.Raw;
        using var stream = MemoryMarshal.TryGetArray(bytes, out var segment) && segment.Array is not null
            ? new MemoryStream(segment.Array, segment.Offset, segment.Count, false)
            : new MemoryStream(bytes.ToArray(), false);
        return await UploadAsync(stream, raw.MediaType, raw.Filename, raw.Metadata, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Parses a JSON literal into a standalone element (the caller owns nothing to dispose).</summary>
    internal static JsonElement Literal(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private Uri BaseUrl()
    {
        return options.PublicBaseUrlResolver?.Invoke(tenant) ?? options.PublicBaseUrl
            ?? throw new InvalidOperationException(
                "ArtifactStorage.PublicBaseUrl or PublicBaseUrlResolver is required when an ArtifactObjectStore is registered.");
    }
}