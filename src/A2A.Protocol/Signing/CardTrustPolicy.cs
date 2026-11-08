namespace A2A.Signing;

/// <summary>
/// What an Agent Card signature must chain to before it counts as <see cref="AgentCardVerificationStatus.Verified"/>
/// (spec §7.5). A key is trusted only when it is pinned for the card's origin in <see cref="TrustedKeys"/>, or when it is
/// served from a <c>jku</c> URL on a host that <see cref="AllowedJkuHosts"/> lists for the card's origin. A key the card
/// itself points at is never trusted on its own.
/// </summary>
public sealed class CardTrustPolicy
{
    /// <summary>
    /// Pinned public keys per card origin. The key is the origin the card is fetched from, <c>"https://host[:port]"</c>
    /// (default ports omitted, compared case-insensitively). A key set trusted for one origin does not verify cards from
    /// another. Keys are matched by <c>kid</c>; a key without a <c>kid</c> never matches.
    /// </summary>
    public IDictionary<string, JwkSet> TrustedKeys { get; } = new Dictionary<string, JwkSet>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Hosts from which a <c>jku</c> key set may be fetched (HTTPS only, through an SSRF-guarded client), per card origin.
    /// The key is the origin the card is fetched from, normalized like <see cref="TrustedKeys"/>; the value lists the
    /// <c>jku</c> hosts (compared case-insensitively). A <c>jku</c> key verifies a card only when its host is listed for
    /// that card's origin: a host allowed for one origin does not verify cards from another. Empty by default, which means
    /// <c>jku</c> is ignored, including a <c>jku</c> on the card's own host. <see cref="AllowJkuHost"/> adds one entry.
    /// </summary>
    public IDictionary<string, ISet<string>> AllowedJkuHosts { get; } = new Dictionary<string, ISet<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Allows <c>jku</c> key sets from <paramref name="host"/> for cards fetched from <paramref name="origin"/>.</summary>
    /// <param name="origin">The card origin, <c>"https://host[:port]"</c>.</param>
    /// <param name="host">The <c>jku</c> host, without scheme or port.</param>
    public CardTrustPolicy AllowJkuHost(string origin, string host)
    {
        ArgumentException.ThrowIfNullOrEmpty(origin);
        ArgumentException.ThrowIfNullOrEmpty(host);
        if (!AllowedJkuHosts.TryGetValue(origin, out var hosts))
            AllowedJkuHosts[origin] = hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        hosts.Add(host);
        return this;
    }

    /// <summary>
    /// Whether unsigned and unverified cards are rejected. <see langword="null"/> (default) means
    /// <see langword="true"/> as soon as <see cref="TrustedKeys"/> or <see cref="AllowedJkuHosts"/> is non-empty.
    /// See <see cref="EffectiveRequireSignature"/>.
    /// </summary>
    public bool? RequireSignature { get; set; }

    /// <summary>Accepted values of the protected <c>typ</c> header (case-insensitive). A missing or other <c>typ</c> fails verification.</summary>
    public ISet<string> AcceptedTyp { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "JOSE", "JOSE+JSON" };

    /// <summary>Accepted <c>alg</c> values. Defaults to <see cref="JwsAlgorithms.All"/>; values outside that set are never accepted.</summary>
    public ISet<string> AllowedAlgorithms { get; } = new HashSet<string>(JwsAlgorithms.Supported, StringComparer.Ordinal);

    /// <summary><see cref="RequireSignature"/> with its default applied.</summary>
    public bool EffectiveRequireSignature => RequireSignature ?? (TrustedKeys.Count > 0 || AllowedJkuHosts.Count > 0);

    /// <summary>The origin of <paramref name="uri"/> as used for <see cref="TrustedKeys"/>: <c>"scheme://host[:port]"</c>, lower case, default port omitted.</summary>
    public static string OriginOf(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).ToLowerInvariant();
    }

    /// <summary>The pinned keys for <paramref name="origin"/> with key id <paramref name="kid"/>.</summary>
    internal IReadOnlyList<Jwk> TrustedKeysFor(string origin, string kid)
    {
        var found = new List<Jwk>();
        foreach (var (configured, set) in TrustedKeys)
        {
            if (!string.Equals(Normalize(configured), origin, StringComparison.Ordinal)) continue;
            found.AddRange(set.Keys.Where(k => string.Equals(k.Kid, kid, StringComparison.Ordinal)));
        }
        return found;
    }

    /// <summary>True when <see cref="AllowedJkuHosts"/> lists <paramref name="host"/> for cards from <paramref name="origin"/>.</summary>
    internal bool IsJkuHostAllowed(string origin, string host)
    {
        foreach (var (configured, hosts) in AllowedJkuHosts)
        {
            if (hosts is null || !string.Equals(Normalize(configured), origin, StringComparison.Ordinal)) continue;
            if (hosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    // Tolerates "https://Host:443/" style configuration.
    private static string Normalize(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? OriginOf(uri) : origin.ToLowerInvariant();
}
