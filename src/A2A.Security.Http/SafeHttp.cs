using System.Net;

namespace A2A.Security.Http;

/// <summary>Settings for <see cref="SafeHttp" />.</summary>
public sealed class SafeHttpOptions
{
    /// <summary>Allow private, loopback and link-local targets. Default false. For development only.</summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>Private networks that are allowed anyway (for example an internal file store), as network and prefix length.</summary>
    public IList<(IPAddress Network, int PrefixLength)> AllowedNetworks { get; } = new List<(IPAddress, int)>();

    /// <summary>Allow plain <c>http</c> URLs. Default false (HTTPS only).</summary>
    public bool AllowHttp { get; set; }

    /// <summary>
    ///     Outbound proxy. Default none (the system proxy is ignored). Egress policy for proxied requests is the proxy's
    ///     job.
    /// </summary>
    public IWebProxy? Proxy { get; set; }
}

/// <summary>
///     Fetches URLs that a peer chose, such as a <see cref="UrlPart" /> in an incoming message (spec §14.1.1 requires SSRF
///     checks). Uses the same guard as push webhooks and <c>jku</c> fetches: private, loopback, link-local, CGNAT,
///     multicast and
///     reserved ranges are blocked by the resolved address at connect time (DNS rebinding included), and redirects and
///     cookies are off.
/// </summary>
/// <example>
///     <code>
/// using var http = SafeHttp.CreateClient();
/// var bytes = await SafeHttp.DownloadAsync(http, urlPart.Url, maxBytes: 10_000_000, cancellationToken: cancellationToken);
/// </code>
/// </example>
public static class SafeHttp
{
    /// <summary>
    ///     A primary handler with the SSRF guard. Wrap it in your own <see cref="HttpClient" /> or
    ///     <c>IHttpClientFactory</c> client.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(SafeHttpOptions? options = null)
    {
        options ??= new SafeHttpOptions();
        return PrivateAddressGuard.CreateHandler(options.AllowPrivateNetworks, "URL", options.AllowedNetworks.ToArray(),
            options.Proxy);
    }

    /// <summary>An <see cref="HttpClient" /> over <see cref="CreateHandler" /> with a 30-second timeout.</summary>
    public static HttpClient CreateClient(SafeHttpOptions? options = null)
    {
        return new HttpClient(CreateHandler(options)) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    ///     Checks <paramref name="url" /> before any connection: absolute <c>https</c> (or <c>http</c> when allowed), no
    ///     ambiguous numeric host, no blocked IP literal. Throws <see cref="HttpRequestException" /> when refused.
    /// </summary>
    public static void EnsureAllowed(Uri url, SafeHttpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        options ??= new SafeHttpOptions();
        if (!url.IsAbsoluteUri ||
            !(url.Scheme == Uri.UriSchemeHttps || (options.AllowHttp && url.Scheme == Uri.UriSchemeHttp)))
            throw new HttpRequestException(options.AllowHttp
                ? "URL must be an absolute http(s) URL."
                : "URL must be an absolute https URL.");
        if (!string.IsNullOrEmpty(url.UserInfo)) throw new HttpRequestException("URL must not contain user info.");
        var host = PrivateAddressGuard.TrimHost(url.Host);
        if (PrivateAddressGuard.TryGetLiteral(url) is { } ip)
        {
            if (!options.AllowPrivateNetworks && PrivateAddressGuard.IsBlocked(ip, options.AllowedNetworks))
                throw new HttpRequestException($"URL host '{host}' is a private address.");
        }
        else if (PrivateAddressGuard.IsAmbiguousNumericHost(host))
        {
            throw new HttpRequestException($"URL host '{host}' is an ambiguous numeric address.");
        }
    }

    /// <summary>
    ///     Downloads <paramref name="url" /> with <paramref name="client" /> (create it with <see cref="CreateClient" /> so
    ///     the
    ///     connect-time guard applies). Refuses redirects, non-success statuses and bodies over <paramref name="maxBytes" />.
    /// </summary>
    /// <param name="client">The client; its handler must come from <see cref="CreateHandler" />.</param>
    /// <param name="url">The peer-chosen URL.</param>
    /// <param name="maxBytes">Largest body accepted.</param>
    /// <param name="options">
    ///     The URL policy checked before connecting (<see cref="EnsureAllowed" />); default HTTPS only, no
    ///     private targets.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<byte[]> DownloadAsync(HttpClient client, Uri url, long maxBytes,
        SafeHttpOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        EnsureAllowed(url, options);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException("URL redirected; redirects are not followed.", null, response.StatusCode);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"URL returned {(int)response.StatusCode}.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new HttpRequestException($"URL body is larger than {maxBytes} bytes.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new HttpRequestException($"URL body is larger than {maxBytes} bytes.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}