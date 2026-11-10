using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using A2A.Client.Credentials;
using A2A.Integrity;
using A2A.Json;

namespace A2A.Client;

/// <summary>
///     Uploads a file to the agent's inbound upload endpoint (<c>MapA2AUploads</c> in A2A.Server.AspNetCore) and returns
///     the
///     <see cref="UrlPart" /> to put in a message. The request carries the caller's A2A credentials, so the endpoint and
///     the link it answers
///     with must be on the same origin. The bytes are hashed while they are sent, and the answer's <c>sha256</c> and
///     <c>sizeBytes</c> must match them.
/// </summary>
/// <remarks>
///     Pass the handler stack that adds the caller's A2A credentials, the one used for A2A calls. The constructor rejects
///     a
///     <see cref="SocketsHttpHandler" /> or <see cref="HttpClientHandler" /> (also nested under a
///     <see cref="DelegatingHandler" />) that follows
///     redirects, because such a client would send the file on to the new target before this class could see it. The SDK
///     cannot inspect
///     a handler of any other type: such a handler must not follow redirects. As a backstop, the class throws when a
///     response shows that
///     a redirect was followed. The handler is not disposed. There is no client timeout, because large files take time:
///     cancel with the token.
///     The content is read once; a handler that sends the request again (a retry) fails instead of sending other bytes.
/// </remarks>
public sealed class A2AFileUploader
{
    private const string Sha256Key = "sha256";
    private const string SizeBytesKey = "sizeBytes";

    private readonly HttpMessageHandler agentHandler;
    private readonly string origin;
    private readonly Uri uploadUrl;

    /// <summary>Creates an uploader.</summary>
    /// <param name="agentHandler">
    ///     The caller's handler stack that adds credentials for the agent. It must not follow
    ///     redirects.
    /// </param>
    /// <param name="uploadUrl">The upload URL: <c>{base}/uploads/{tenant or _}</c> of the agent.</param>
    /// <exception cref="ArgumentException">The handler is a known type that follows redirects, or the URL is not absolute.</exception>
    public A2AFileUploader(HttpMessageHandler agentHandler, Uri uploadUrl)
    {
        ArgumentNullException.ThrowIfNull(agentHandler);
        ArgumentNullException.ThrowIfNull(uploadUrl);
        A2ACredentialHandler.EnsureNoAutoRedirect(agentHandler);
        if (!uploadUrl.IsAbsoluteUri)
            throw new ArgumentException("The upload URL must be an absolute URL.", nameof(uploadUrl));
        this.agentHandler = agentHandler;
        this.uploadUrl = uploadUrl;
        origin = uploadUrl.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>Uploads <paramref name="content" /> and returns the part that stands for it.</summary>
    /// <param name="content">The bytes. Read once, from the current position to the end; not disposed.</param>
    /// <param name="mediaType">The media type, sent as <c>Content-Type</c>.</param>
    /// <param name="fileName">The file name, sent as <c>Content-Disposition</c>, or null for none.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    ///     The <see cref="UrlPart" /> with the link, media type, file name and <c>sha256</c> and <c>sizeBytes</c>
    ///     metadata.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///     The agent handler followed a redirect, the returned link is on another
    ///     origin than the upload URL, or the request was sent twice.
    /// </exception>
    /// <exception cref="HttpRequestException">
    ///     The server refused the upload (the status is in
    ///     <see cref="HttpRequestException.StatusCode" />).
    /// </exception>
    /// <exception cref="InvalidDataException">
    ///     The answer is not a <c>url</c> part, or its <c>sha256</c> or <c>sizeBytes</c> is
    ///     missing or differs from what was sent.
    /// </exception>
    public async Task<UrlPart> UploadAsync(Stream content, string mediaType, string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(mediaType);
        using var body = new HashingContent(content);
        body.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        if (!string.IsNullOrEmpty(fileName))
            body.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                { FileNameStar = fileName };
        using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = body };

        using var http = new HttpClient(agentHandler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is not null && finalUri != uploadUrl)
            throw new InvalidOperationException(
                "The agent handler followed a redirect. It must not, or credentials could cross origins.");
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException(
                $"The server answered {(int)response.StatusCode}, which is not a supported answer to an upload.", null,
                response.StatusCode);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The upload returned {(int)response.StatusCode}.", null,
                response.StatusCode);

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Part parsed;
        try
        {
            parsed = A2AJson.Deserialize<Part>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The upload answer is not a part.", ex);
        }

        if (parsed is not UrlPart part) throw new InvalidDataException("The upload answer is not a url part.");
        if (!part.Url.IsAbsoluteUri || !string.Equals(part.Url.GetLeftPart(UriPartial.Authority), origin,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The upload link is not on the upload URL's origin; credentials are never sent to another origin.");

        var metadata = part.Metadata;
        if (metadata is null || !metadata.TryGetValue(SizeBytesKey, out var size)
                             || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var reportedSize) ||
                             reportedSize != body.BytesSent)
            throw new InvalidDataException(
                $"The upload answer's '{SizeBytesKey}' is missing or does not match the bytes sent.");
        if (!metadata.TryGetValue(Sha256Key, out var sha) || sha.ValueKind != JsonValueKind.String
                                                          || !string.Equals(sha.GetString(), body.Sha256,
                                                              StringComparison.Ordinal))
            throw new InvalidDataException(
                $"The upload answer's '{Sha256Key}' is missing or does not match the bytes sent.");
        return part;
    }

    /// <summary>
    ///     Sends a stream and hashes and counts it on the way. Not seekable streams work; the length is known only for a
    ///     seekable one.
    /// </summary>
    private sealed class HashingContent(Stream source) : HttpContent
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly long? length = source.CanSeek ? source.Length - source.Position : null;
        private int sent;
        private string? sha256;

        public long BytesSent { get; private set; }

        /// <summary>
        ///     The hash of the bytes sent, in the form of <see cref="ArtifactHash.Format" />. Set when the body was sent in
        ///     full.
        /// </summary>
        public string? Sha256 => sha256;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            // The bytes are gone after the first pass; a second one would send other bytes than the ones that were hashed.
            if (Interlocked.Exchange(ref sent, 1) != 0)
                throw new InvalidOperationException("The upload content can be sent only once.");
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                BytesSent += read;
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            sha256 = ArtifactHash.Format(hash.GetHashAndReset());
        }

        protected override bool TryComputeLength(out long len)
        {
            len = length ?? 0;
            return length is not null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) hash.Dispose();
            base.Dispose(disposing);
        }
    }
}