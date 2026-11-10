using A2A.Signing;
using A2A.Text;

namespace A2A.Client.Peers;

/// <summary>How strongly a <see cref="PeerIdentity" /> is backed.</summary>
public enum PeerIdentityAssurance
{
    /// <summary>
    ///     No key and no external proof. Anyone who controls the origin, or reaches the client through a compromised route on
    ///     plain
    ///     HTTP, is this peer.
    /// </summary>
    OriginOnly = 0,

    /// <summary>A trusted key verified the card, or an external id (such as a DID) was checked.</summary>
    Verified = 1
}

/// <summary>
///     Who a peer is: the card origin, the key that verified the card and an optional external id. The card's <c>name</c>
///     is never
///     part of it. <see cref="Origin" /> and <see cref="KeyId" /> have no setter, so <c>with</c> can only add
///     <see cref="ExternalId" />.
/// </summary>
public sealed record PeerIdentity
{
    private readonly string? _externalId;

    /// <summary>
    ///     Creates an identity. <paramref name="origin" /> must be an absolute http or https URL; it is normalized to its
    ///     origin.
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     <paramref name="origin" /> is not an absolute http or https URL, or
    ///     <paramref name="keyId" /> is empty.
    /// </exception>
    public PeerIdentity(string origin, string? keyId = null)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Origin must be an absolute http or https URL.", nameof(origin));
        if (keyId is { Length: 0 }) throw new ArgumentException("Key id must not be empty.", nameof(keyId));
        Origin = CardTrustPolicy.OriginOf(uri);
        KeyId = keyId;
    }

    /// <summary>The card origin, <c>scheme://host[:port]</c>, lower case.</summary>
    public string Origin { get; }

    /// <summary>The <c>kid</c> of the trusted key that verified the card, or <see langword="null" />.</summary>
    public string? KeyId { get; }

    /// <summary>
    ///     An optional DID or SPIFFE id. Set it only after a real check: it lifts <see cref="Assurance" /> to
    ///     <see cref="PeerIdentityAssurance.Verified" />.
    /// </summary>
    /// <exception cref="ArgumentException">The value is empty or whitespace.</exception>
    public string? ExternalId
    {
        get => _externalId;
        init
        {
            if (value is not null && string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("External id must not be empty.", nameof(value));
            _externalId = value;
        }
    }

    /// <summary><see cref="PeerIdentityAssurance.Verified" /> when <see cref="KeyId" /> or <see cref="ExternalId" /> is set.</summary>
    public PeerIdentityAssurance Assurance =>
        KeyId is not null || ExternalId is not null ? PeerIdentityAssurance.Verified : PeerIdentityAssurance.OriginOnly;

    /// <summary>A form for logs: <c>https://a (origin only)</c>, <c>https://a#k1</c> or <c>https://a#k1 did:web:a</c>.</summary>
    public override string ToString()
    {
        var text = Origin;
        if (KeyId is not null) text += "#" + TextSanitizer.ForMessage(KeyId);
        if (ExternalId is not null) text += " " + TextSanitizer.ForMessage(ExternalId);
        return Assurance == PeerIdentityAssurance.OriginOnly ? text + " (origin only)" : text;
    }

    /// <summary>
    ///     The identity of a resolved card. A <c>kid</c> counts only when the card was
    ///     <see cref="AgentCardVerificationStatus.Verified" />.
    /// </summary>
    internal static PeerIdentity FromCard(Uri source, AgentCardVerificationResult verification)
    {
        return new PeerIdentity(CardTrustPolicy.OriginOf(source),
            verification.Status == AgentCardVerificationStatus.Verified ? verification.KeyId : null);
    }
}