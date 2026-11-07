using System.Text.Json;
using A2A.Json;
using A2A.Signing;

namespace A2A.Security.Http;

/// <summary>
/// Fetches <c>jku</c> key sets through the SSRF guard: by default over a client whose connections to private, loopback and
/// link-local addresses are refused at connect time, without redirects, with a 10 second timeout and a 256 KiB size cap.
/// </summary>
/// <param name="invoker">Sends the requests. <see langword="null"/> uses a shared guarded client. A supplied invoker replaces the
/// guarded handler: the private-address block, the no-redirect rule and the no-proxy rule are then the caller's responsibility.
/// Supply one only for tests or a custom egress policy.</param>
public sealed class GuardedJwksFetcher(HttpMessageInvoker? invoker = null) : IJwksFetcher
{
    private const int MaxJwksBytes = 256 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly Lazy<HttpMessageInvoker> Default = new(() =>
        new HttpMessageInvoker(PrivateAddressGuard.CreateHandler(allowPrivateNetworks: false, "JWKS")));

    private readonly HttpMessageInvoker _invoker = invoker ?? Default.Value;

    /// <inheritdoc/>
    public async ValueTask<JwkSet> FetchAsync(Uri jku, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jku);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, jku);
        using var response = await _invoker.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"JWKS fetch returned {(int)response.StatusCode}.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > MaxJwksBytes) throw TooLarge();

        using var buffer = new MemoryStream();
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var chunk = new byte[8192];
            int read;
            while ((read = await body.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxJwksBytes) throw TooLarge();
                buffer.Write(chunk, 0, read);
            }
        }
        catch (IOException ex)
        {
            // HttpClient.GetByteArrayAsync used to wrap these; callers filter on HttpRequestException.
            throw new HttpRequestException("JWKS fetch failed.", ex);
        }
        return JsonSerializer.Deserialize(buffer.ToArray(), A2AJson.GetTypeInfo<JwkSet>()) ?? new JwkSet();
    }

    private static HttpRequestException TooLarge() => new($"JWKS response exceeds {MaxJwksBytes} bytes.");
}
