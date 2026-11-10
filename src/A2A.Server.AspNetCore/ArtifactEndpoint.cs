using System.Buffers;
using System.Diagnostics.Metrics;
using System.Globalization;
using A2A.Diagnostics;
using A2A.Server.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace A2A.Server.AspNetCore;

/// <summary>
///     Serves <c>GET /artifacts/{artifactTenant}/{taskId}/{objectId}</c>: the bytes behind the <c>url</c> parts that the
///     object store
///     produces. The caller uses the same credentials as for A2A calls, and the read is authorized as <c>GetTask</c>
///     (see <see cref="A2ARequestHandler.OpenArtifactAsync" />). Every miss is HTTP 404.
/// </summary>
internal sealed class ArtifactEndpoint(
    A2ARequestHandler handler,
    IOptions<A2AServerOptions> serverOptions,
    IHostEnvironment environment,
    ILogger<ArtifactEndpoint> logger)
{
    internal const string DisplayName = "A2A Artifact";

    /// <summary>The link spelling of the default tenant.</summary>
    private const string DefaultTenant = "_";

    private const int BufferSize = 81920;

    private static readonly Meter Meter = new(A2ATelemetry.ServerMeterName);

    private static readonly Counter<long> BytesServed =
        Meter.CreateCounter<long>("a2a.server.artifact.bytes.served", "By",
            "Artifact bytes streamed to callers by the server (not redirects).");

    private readonly bool _includeDetails = HttpPlumbing.ShouldIncludeDetails(serverOptions.Value, environment);

    public async Task GetAsync(HttpContext http)
    {
        var response = http.Response;
        // Set first, so errors carry them too.
        response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        response.Headers[HeaderNames.CacheControl] = "private, no-store";

        ServerCallContext call;
        // A download is a plain GET by a downloader or a browser, which sends no A2A-Version or A2A-Extensions header: it is always a
        // current-version call, and the card's required extensions are not enforced. Authentication and ownership still are.
        try
        {
            call = HttpPlumbing.CreateCallContext(http, ProtocolBindings.HttpJson, A2AProtocol.Version,
                skipRequiredExtensions: true);
        }
        catch (Exception ex) when (!response.HasStarted)
        {
            await WriteErrorAsync(http, ServerCallContext.Anonymous(),
                HttpPlumbing.Normalize(ex, logger, _includeDetails)).ConfigureAwait(false);
            return;
        }

        try
        {
            handler.ValidateCall(call);
            var taskId = RouteString(http, "taskId");
            var objectId = RouteString(http, "objectId");
            var linkTenant = RouteString(http, "artifactTenant");
            linkTenant = linkTenant == DefaultTenant ? "" : linkTenant;
            // The tenant resolver and the link must agree, or the caller is on the wrong origin for this link.
            if (call.Tenant.Length != 0 && !string.Equals(call.Tenant, linkTenant, StringComparison.Ordinal))
                throw A2AException.TaskNotFound(taskId);
            call.BindTenant(linkTenant);

            var range = ParseRange(http.Request.Headers.Range);
            await using var download = await handler
                .OpenArtifactAsync(taskId, objectId, range, call, http.RequestAborted).ConfigureAwait(false);
            if (download.RedirectTo is { } url)
            {
                response.Redirect(url.AbsoluteUri);
                return;
            }

            await WriteContentAsync(http, download.Content!, range).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!response.HasStarted)
        {
            await WriteErrorAsync(http, call, HttpPlumbing.Normalize(ex, logger, _includeDetails))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Writes an open object as the response (also used by <see cref="UploadEndpoint" />): <c>Range</c>, attachment
    ///     disposition, length, byte count.
    /// </summary>
    internal static async Task WriteContentAsync(HttpContext http, ArtifactObjectContent content,
        ArtifactByteRange? range)
    {
        var response = http.Response;
        response.Headers[HeaderNames.AcceptRanges] = "bytes";
        if (range is not null)
        {
            if (range.Offset >= content.TotalLength)
            {
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                response.Headers[HeaderNames.ContentRange] = $"bytes */{content.TotalLength}";
                return;
            }

            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers[HeaderNames.ContentRange] =
                $"bytes {range.Offset}-{range.Offset + content.Length - 1}/{content.TotalLength}";
        }

        response.ContentType = MediaTypeHeaderValue.TryParse(content.ContentType, out var type)
            ? type.ToString()
            : "application/octet-stream";
        response.Headers[HeaderNames.ContentDisposition] = Disposition(content.FileName);
        response.ContentLength = content.Length;
        await CopyAsync(content.Content, response.Body, http.RequestAborted).ConfigureAwait(false);
    }

    private static string Disposition(string? fileName)
    {
        var header = new ContentDispositionHeaderValue("attachment");
        if (!string.IsNullOrEmpty(fileName)) header.SetHttpFileName(fileName);
        return header.ToString();
    }

    /// <summary>Copies the object to the response and counts what was sent, also when the caller leaves early.</summary>
    private static async Task CopyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, true);
            if (total > 0) BytesServed.Add(total);
        }
    }

    /// <summary>
    ///     A single <c>bytes=first-last</c> or <c>bytes=first-</c> range. Anything else (several ranges, a suffix range,
    ///     another unit,
    ///     <c>last</c> before <c>first</c>) is ignored, and the whole object is sent.
    /// </summary>
    internal static ArtifactByteRange? ParseRange(StringValues header)
    {
        if (header.Count != 1 || !RangeHeaderValue.TryParse(header[0], out var parsed)) return null;
        if (!string.Equals(parsed.Unit.Value, "bytes", StringComparison.OrdinalIgnoreCase) ||
            parsed.Ranges.Count != 1) return null;
        var range = parsed.Ranges.Single();
        if (range.From is not { } from || (range.To is { } to && to < from)) return null;
        // A last byte so far past the first that the length does not fit in a long means "to the end".
        return new ArtifactByteRange(from,
            range.To is { } last && last - from < long.MaxValue ? last - from + 1 : null);
    }

    internal static string RouteString(HttpContext http, string name)
    {
        return Convert.ToString(http.Request.RouteValues[name], CultureInfo.InvariantCulture) ??
               "";
    }

    internal static async Task WriteErrorAsync(HttpContext http, ServerCallContext call, A2AException ex)
    {
        await HttpPlumbing.SetAuthChallengeAsync(http, ex, call).ConfigureAwait(false);
        HttpPlumbing.SetRetryAfter(http, ex);
        await HttpPlumbing
            .WriteJsonAsync(http, RestEndpoint.StatusBody(ex), A2AProtocol.MediaType, HttpPlumbing.StatusFor(ex))
            .ConfigureAwait(false);
    }
}