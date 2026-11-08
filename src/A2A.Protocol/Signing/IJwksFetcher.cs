namespace A2A.Signing;

/// <summary>
/// Fetches the JSON Web Key Set a signature's <c>jku</c> header points to. <see cref="AgentCardVerifier"/> checks the URL against
/// <see cref="CardTrustPolicy"/> (HTTPS, allowlisted host) before calling, caches results, and caps the number of fetches per
/// verification. Implementations do the network call and must refuse targets the caller should not reach; see
/// <c>A2A.Security.Http.GuardedJwksFetcher</c> for an SSRF-guarded implementation.
/// </summary>
public interface IJwksFetcher
{
    /// <summary>Fetches and parses the key set at <paramref name="jku"/>.</summary>
    /// <param name="jku">The absolute HTTPS URL from the signature header.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The key set.</returns>
    /// <exception cref="HttpRequestException">The fetch failed or was refused.</exception>
    /// <exception cref="System.Text.Json.JsonException">The response is not a key set.</exception>
    ValueTask<JwkSet> FetchAsync(Uri jku, CancellationToken cancellationToken);
}
