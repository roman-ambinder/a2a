namespace A2A.Signing;

/// <summary>
///     Fetches the JSON Web Key Set a signature's <c>jku</c> header points to. <see cref="AgentCardVerifier" /> checks the
///     URL against
///     <see cref="CardTrustPolicy" /> (HTTPS, allowlisted host) before calling, caches results, and caps the number of
///     fetches per
///     verification. Implementations do the network call and must refuse targets the caller should not reach; see
///     <c>A2A.Security.Http.GuardedJwksFetcher</c> for an SSRF-guarded implementation.
/// </summary>
public interface IJwksFetcher
{
    /// <summary>Fetches and parses the key set at <paramref name="jku" />.</summary>
    /// <param name="jku">The absolute HTTPS URL from the signature header.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The key set.</returns>
    /// <exception cref="HttpRequestException">The fetch failed or was refused.</exception>
    /// <exception cref="System.Text.Json.JsonException">The response is not a key set.</exception>
    ValueTask<JwkSet> FetchAsync(Uri jku, CancellationToken cancellationToken);
}
/// <summary>
///     A key set and how long it may be cached, as the server that served it says.
/// </summary>
/// <param name="Keys">The key set.</param>
/// <param name="MaxAge">
///     How long the key set may be used before it is fetched again: zero for <c>no-store</c> / <c>no-cache</c> or a
///     past <c>Expires</c>, the remaining <c>max-age</c> or <c>Expires</c> otherwise, <see langword="null" /> when the
///     response says nothing (the verifier then uses its default).
/// </param>
public sealed record JwksFetchResult(JwkSet Keys, TimeSpan? MaxAge);

/// <summary>
///     An <see cref="IJwksFetcher" /> that also reports how long the key set may be cached. <see cref="AgentCardVerifier" />
///     uses that lifetime, capped at its default of 10 minutes, so a key the publisher removes stops verifying cards as
///     soon as the published cache lifetime ends (spec §8.4.3: expired or revoked keys MUST NOT be used).
/// </summary>
public interface IJwksLifetimeFetcher : IJwksFetcher
{
    /// <summary>Fetches and parses the key set at <paramref name="jku" />, with its cache lifetime.</summary>
    /// <param name="jku">The absolute HTTPS URL from the signature header.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <exception cref="HttpRequestException">The fetch failed or was refused.</exception>
    /// <exception cref="System.Text.Json.JsonException">The response is not a key set.</exception>
    ValueTask<JwksFetchResult> FetchWithLifetimeAsync(Uri jku, CancellationToken cancellationToken);
}
