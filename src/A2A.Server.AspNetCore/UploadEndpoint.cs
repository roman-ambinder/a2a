using A2A.Server.Pipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace A2A.Server.AspNetCore;

/// <summary>
///     Serves <c>POST /uploads/{uploadTenant}</c> (store a file the client sends as the raw request body) and
///     <c>GET /uploads/{uploadTenant}/{objectId}</c> (the owner reads it back). The caller uses the same credentials as
///     for A2A calls and
///     must be authenticated, also when anonymous calls are allowed. Both run as the extension operations
///     <see cref="A2ARequestHandler.UploadOperationName" /> and <see cref="A2ARequestHandler.ReadUploadOperationName" />.
///     The body is
///     streamed to the object store and is never buffered.
/// </summary>
internal sealed class UploadEndpoint(
    A2ARequestHandler handler,
    IOptions<A2AServerOptions> serverOptions,
    IHostEnvironment environment,
    ILogger<UploadEndpoint> logger)
{
    internal const string DisplayName = "A2A Upload";

    /// <summary>The link spelling of the default tenant.</summary>
    private const string DefaultTenant = "_";

    private readonly bool _includeDetails = HttpPlumbing.ShouldIncludeDetails(serverOptions.Value, environment);

    public async Task PostAsync(HttpContext http)
    {
        var response = http.Response;
        // Set first, so errors carry them too.
        response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        response.Headers[HeaderNames.CacheControl] = "private, no-store";

        var call = await CreateCallAsync(http).ConfigureAwait(false);
        if (call is null) return;
        var max = serverOptions.Value.ArtifactStorage.Uploads.MaxUploadBytes;
        try
        {
            handler.ValidateCall(call);
            BindRouteTenant(http, call,
                () => A2AException.InvalidParams("The tenant in the URL does not match the tenant of the caller."));
            // The route's authorization policy admitted this call, and the endpoint serves nothing else: the default authorization handler allows it.
            call.Items[A2AExtensionOperation.PolicySatisfiedItem] = true;
            // For this request only: the server's default body limit would refuse a large file before the store sees it.
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                feature.MaxRequestBodySize = max;

            var mediaType = ParseMediaType(http.Request.ContentType);
            var fileName = ParseFileName(http.Request.Headers.ContentDisposition);
            var part = await handler.UploadFileAsync(http.Request.Body, mediaType, fileName, http.Request.ContentLength,
                call, http.RequestAborted).ConfigureAwait(false);

            response.Headers[HeaderNames.Location] = part.Url.AbsoluteUri;
            await HttpPlumbing.WriteJsonAsync<Part>(http, part, A2AProtocol.MediaType, StatusCodes.Status201Created)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
        }
        catch (BadHttpRequestException bad) when (bad.StatusCode == StatusCodes.Status413PayloadTooLarge &&
                                                  !response.HasStarted)
        {
            // The server's own body limit (raised above) fired before the store's count did.
            await ArtifactEndpoint.WriteErrorAsync(http, call, A2AException.PayloadTooLarge(max)).ConfigureAwait(false);
        }
        catch (Exception ex) when (!response.HasStarted)
        {
            await ArtifactEndpoint.WriteErrorAsync(http, call, HttpPlumbing.Normalize(ex, logger, _includeDetails))
                .ConfigureAwait(false);
        }
    }

    public async Task GetAsync(HttpContext http)
    {
        var response = http.Response;
        response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        response.Headers[HeaderNames.CacheControl] = "private, no-store";

        var call = await CreateCallAsync(http).ConfigureAwait(false);
        if (call is null) return;
        try
        {
            handler.ValidateCall(call);
            var objectId = ArtifactEndpoint.RouteString(http, "objectId");
            // Every miss is 404, a wrong tenant too.
            BindRouteTenant(http, call, () => A2AException.TaskNotFound(objectId));
            call.Items[A2AExtensionOperation.PolicySatisfiedItem] = true;

            var range = ArtifactEndpoint.ParseRange(http.Request.Headers.Range);
            await using var download = await handler.OpenUploadAsync(objectId, range, call, http.RequestAborted)
                .ConfigureAwait(false);
            await ArtifactEndpoint.WriteContentAsync(http, download.Content!, range).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!response.HasStarted)
        {
            await ArtifactEndpoint.WriteErrorAsync(http, call, HttpPlumbing.Normalize(ex, logger, _includeDetails))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     A plain HTTP client calls these routes and sends no A2A-Version or A2A-Extensions header: it is always a
    ///     current-version call,
    ///     and the card's required extensions are not enforced. Authentication and ownership still are. Writes the error and
    ///     returns null on failure.
    /// </summary>
    private async Task<ServerCallContext?> CreateCallAsync(HttpContext http)
    {
        try
        {
            return HttpPlumbing.CreateCallContext(http, ProtocolBindings.HttpJson, A2AProtocol.Version,
                skipRequiredExtensions: true);
        }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            await ArtifactEndpoint
                .WriteErrorAsync(http, ServerCallContext.Anonymous(),
                    HttpPlumbing.Normalize(ex, logger, _includeDetails)).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>The tenant resolver and the route must agree, or the caller is on the wrong origin for this link.</summary>
    private static void BindRouteTenant(HttpContext http, ServerCallContext call, Func<A2AException> mismatch)
    {
        var routeTenant = ArtifactEndpoint.RouteString(http, "uploadTenant");
        routeTenant = routeTenant == DefaultTenant ? "" : routeTenant;
        if (call.Tenant.Length != 0 && !string.Equals(call.Tenant, routeTenant, StringComparison.Ordinal))
            throw mismatch();
        call.BindTenant(routeTenant);
    }

    /// <summary>
    ///     The media type of the body, or null when the header is absent. A header that does not parse, or a wildcard, is
    ///     InvalidParams.
    /// </summary>
    internal static string? ParseMediaType(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        if (!MediaTypeHeaderValue.TryParse(header, out var type) || type.MatchesAllTypes || type.MatchesAllSubTypes)
            throw A2AException.InvalidParams("The Content-Type header is not a valid media type.");
        return type.ToString();
    }

    /// <summary>
    ///     The file name of a <c>Content-Disposition</c> header (<c>filename*</c> first), or null when there is none. The name
    ///     is cleaned
    ///     later by the upload (path removed, length and control characters checked).
    /// </summary>
    internal static string? ParseFileName(StringValues header)
    {
        if (header.Count == 0 || string.IsNullOrWhiteSpace(header[0])) return null;
        if (header.Count > 1 || !ContentDispositionHeaderValue.TryParse(header[0], out var disposition))
            throw A2AException.InvalidParams("The Content-Disposition header is not valid.");
        var name = disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName;
        return name.HasValue ? name.Value : null;
    }
}