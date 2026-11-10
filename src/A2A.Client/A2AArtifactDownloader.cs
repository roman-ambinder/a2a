using System.Security.Cryptography;
using System.Text.Json;
using A2A.Client.Credentials;
using A2A.Integrity;
using A2A.Security.Http;

namespace A2A.Client;

/// <summary>
///     Downloads the bytes behind an offloaded artifact part (a <see cref="UrlPart" /> the server wrote). The request
///     carries the caller's
///     A2A credentials, so it only goes to the agent's own origin. When the server answers with a redirect to a presigned
///     storage URL,
///     that URL is fetched once with a separate client that has no credentials and the <see cref="SafeHttp" /> address
///     guard.
/// </summary>
/// <remarks>
///     Pass the handler stack that adds the caller's A2A credentials, the one used for A2A calls. The constructor rejects
///     a
///     <see cref="SocketsHttpHandler" /> or <see cref="HttpClientHandler" /> (also nested under a
///     <see cref="DelegatingHandler" />) that follows
///     redirects, because such a client would send the request on to the new target before this class could see it. The
///     SDK cannot inspect
///     a handler of any other type: such a handler must not follow redirects. As a backstop, the class throws when a
///     response shows that
///     a redirect was followed. The handler is not disposed. The <c>sizeBytes</c> and <c>sha256</c> metadata of the part
///     are checked when present.
/// </remarks>
public sealed class A2AArtifactDownloader
{
    private const string Sha256Key = "sha256";
    private const string SizeBytesKey = "sizeBytes";

    private readonly HttpMessageHandler agentHandler;
    private readonly string agentOrigin;
    private readonly Func<HttpClient> storageClientFactory;
    private readonly SafeHttpOptions? storageOptions;

    /// <summary>Creates a downloader.</summary>
    /// <exception cref="ArgumentException">The handler is a known type that follows redirects.</exception>
    /// <param name="agentHandler">
    ///     The caller's handler stack that adds credentials for the agent. It must not follow
    ///     redirects.
    /// </param>
    /// <param name="agentOrigin">The agent's origin, from its card. Part URLs on any other origin are refused.</param>
    /// <param name="storageOptions">
    ///     The URL policy for the storage redirect. Default: HTTPS only, no private targets. Set it
    ///     to allow a private store such as MinIO.
    /// </param>
    public A2AArtifactDownloader(HttpMessageHandler agentHandler, Uri agentOrigin,
        SafeHttpOptions? storageOptions = null)
        : this(agentHandler, agentOrigin, storageOptions, null)
    {
    }

    internal A2AArtifactDownloader(HttpMessageHandler agentHandler, Uri agentOrigin, SafeHttpOptions? storageOptions,
        Func<HttpClient>? storageClientFactory)
    {
        ArgumentNullException.ThrowIfNull(agentHandler);
        ArgumentNullException.ThrowIfNull(agentOrigin);
        A2ACredentialHandler.EnsureNoAutoRedirect(agentHandler);
        if (!agentOrigin.IsAbsoluteUri)
            throw new ArgumentException("The agent origin must be an absolute URL.", nameof(agentOrigin));
        this.agentHandler = agentHandler;
        this.agentOrigin = agentOrigin.GetLeftPart(UriPartial.Authority);
        this.storageOptions = storageOptions;
        this.storageClientFactory = storageClientFactory ?? (() => SafeHttp.CreateClient(storageOptions));
    }

    /// <summary>Downloads the content behind <paramref name="part" /> into <paramref name="destination" />.</summary>
    /// <param name="part">The URL part from an artifact.</param>
    /// <param name="destination">Where the bytes go. On a failure it may hold a partial write, so discard it.</param>
    /// <param name="maxBytes">Largest body accepted.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="InvalidOperationException">
    ///     The URL is on another origin, or the agent client followed a redirect, or
    ///     the storage URL redirected again.
    /// </exception>
    /// <exception cref="HttpRequestException">The server answered with an error status, or the storage URL is not allowed.</exception>
    /// <exception cref="InvalidDataException">
    ///     The body is larger than <paramref name="maxBytes" />, or does not match the
    ///     part's <c>sizeBytes</c> or <c>sha256</c>.
    /// </exception>
    public async Task DownloadAsync(UrlPart part, Stream destination, long maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        var url = part.Url;
        if (!url.IsAbsoluteUri || !string.Equals(url.GetLeftPart(UriPartial.Authority), agentOrigin,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The artifact URL is not on the agent's origin; credentials are never sent to another origin.");

        using var agentHttpClient = new HttpClient(agentHandler, disposeHandler: false);
        using var response = await agentHttpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is not null && finalUri != url)
            throw new InvalidOperationException(
                "The agent handler followed a redirect. It must not, or credentials could cross origins.");

        if (response.StatusCode is System.Net.HttpStatusCode.Found or System.Net.HttpStatusCode.TemporaryRedirect)
        {
            var location = response.Headers.Location ??
                           throw new HttpRequestException("The redirect has no Location.", null, response.StatusCode);
            if (!location.IsAbsoluteUri) location = new Uri(url, location);
            SafeHttp.EnsureAllowed(location, storageOptions);
            using var storageClient = storageClientFactory();
            using var stored = await storageClient
                .GetAsync(location, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)stored.StatusCode is >= 300 and < 400)
                throw new InvalidOperationException("The storage URL redirected again; only one redirect is followed.");
            await CopyAsync(stored, part, destination, maxBytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        await CopyAsync(response, part, destination, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyAsync(HttpResponseMessage response, UrlPart part, Stream destination, long maxBytes,
        CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException(
                $"The server answered {(int)response.StatusCode}, which is not a supported redirect.", null,
                response.StatusCode);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The artifact download returned {(int)response.StatusCode}.", null,
                response.StatusCode);
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException($"The artifact is larger than {maxBytes} bytes.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes) throw new InvalidDataException($"The artifact is larger than {maxBytes} bytes.");
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        var metadata = part.Metadata;
        if (metadata is not null && metadata.TryGetValue(SizeBytesKey, out var size))
        {
            if (size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var expectedSize) ||
                expectedSize != total)
                throw new InvalidDataException($"The artifact size does not match its '{SizeBytesKey}' metadata.");
        }

        if (metadata is not null && metadata.TryGetValue(Sha256Key, out var sha))
        {
            var actual = ArtifactHash.Format(hash.GetHashAndReset());
            if (sha.ValueKind != JsonValueKind.String ||
                !string.Equals(sha.GetString(), actual, StringComparison.Ordinal))
                throw new InvalidDataException($"The artifact content does not match its '{Sha256Key}' metadata.");
        }
    }
}