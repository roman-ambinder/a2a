using A2A.Signing;

namespace A2A.Client.Peers;

/// <summary>What an <see cref="IPeerIdentityResolver"/> gets to look at: a card that already passed signature and pin checks.</summary>
/// <param name="Card">The resolved card.</param>
/// <param name="Source">Where the card was fetched.</param>
/// <param name="Verification">How the card was checked.</param>
/// <param name="Baseline">The identity from the card origin and verifying key.</param>
public sealed record PeerIdentityContext(AgentCard Card, Uri Source, AgentCardVerificationResult Verification, PeerIdentity Baseline);

/// <summary>
/// Plug-in point for identity schemes such as DID or SPIFFE. It runs only after signature and pin checks pass. A resolver may
/// add, never change: the result must keep the baseline's <see cref="PeerIdentity.Origin"/> and <see cref="PeerIdentity.KeyId"/>,
/// or resolution fails with <see cref="InvalidOperationException"/>. Return <c>context.Baseline with { ExternalId = ... }</c>
/// after checking your own proof. Exceptions fail resolution and nothing is cached.
/// </summary>
public interface IPeerIdentityResolver
{
    /// <summary>Returns the identity of the peer behind <paramref name="context"/>.</summary>
    ValueTask<PeerIdentity> ResolveAsync(PeerIdentityContext context, CancellationToken cancellationToken = default);
}

/// <summary>The default resolver: returns <see cref="PeerIdentityContext.Baseline"/>.</summary>
public sealed class CardPeerIdentityResolver : IPeerIdentityResolver
{
    private CardPeerIdentityResolver() { }

    /// <summary>The shared instance.</summary>
    public static CardPeerIdentityResolver Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<PeerIdentity> ResolveAsync(PeerIdentityContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult(context.Baseline);
    }
}
