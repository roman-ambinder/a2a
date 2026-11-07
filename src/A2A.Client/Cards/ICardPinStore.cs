namespace A2A.Client.Cards;

/// <summary>What <see cref="A2ACardResolver"/> does when an origin serves a card whose content differs from the pinned one.</summary>
public enum PinPolicy
{
    /// <summary>Raise <see cref="A2ACardResolver.CardChanged"/>, log a warning, count <c>a2a.card.changed</c>, accept the card and re-pin. Default.</summary>
    Notify,

    /// <summary>As <see cref="Notify"/>, then fail resolution with <see cref="AgentCardPinException"/>. The pin is not moved.</summary>
    Strict,

    /// <summary>No pinning.</summary>
    Off,
}

/// <summary>A pinned card: SHA-256 (lower-case hex) of its canonical form, and the <c>kid</c> it was verified with, if any.</summary>
public sealed record CardPin(string Hash, string? KeyId);

/// <summary>
/// Stores one <see cref="CardPin"/> per card origin (<c>"https://host[:port]"</c>). Pinning detects a changed card; protection
/// against a forged card comes from <see cref="A2A.Signing.CardTrustPolicy.TrustedKeys"/>.
/// </summary>
public interface ICardPinStore
{
    /// <summary>Returns the pin of <paramref name="origin"/>, or null when none is stored.</summary>
    ValueTask<CardPin?> GetAsync(string origin, CancellationToken cancellationToken = default);

    /// <summary>Stores the pin of <paramref name="origin"/>, replacing an earlier one.</summary>
    ValueTask SetAsync(string origin, CardPin pin, CancellationToken cancellationToken = default);
}
