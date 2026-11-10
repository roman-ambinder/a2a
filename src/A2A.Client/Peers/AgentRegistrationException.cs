namespace A2A.Client.Peers;

/// <summary>Why <see cref="AgentRegistry.Register" /> refused a card.</summary>
public enum AgentRegistrationRejection
{
    /// <summary>Another identity already holds the same name key. The first one registered wins.</summary>
    DuplicateName,

    /// <summary>The name is empty after sanitizing.</summary>
    InvalidName,

    /// <summary>The same identity is already registered at another card URL.</summary>
    IdentityInUse,

    /// <summary>The card URL has a registered agent with a stronger identity (a signed agent is now served unsigned).</summary>
    IdentityDowngrade,

    /// <summary>A new entry would go beyond <see cref="AgentRegistryOptions.MaxAgents" />.</summary>
    RegistryFull
}

/// <summary>
///     Thrown by <see cref="AgentRegistry.Register" /> when a card is refused. The registry is unchanged. The message
///     holds only
///     sanitized peer text.
/// </summary>
/// <param name="reason">Why the card was refused.</param>
/// <param name="identity">The identity of the refused card.</param>
/// <param name="conflictingIdentity">The registered identity that caused the refusal, if any.</param>
/// <param name="message">The error message.</param>
public sealed class AgentRegistrationException(
    AgentRegistrationRejection reason,
    PeerIdentity identity,
    PeerIdentity? conflictingIdentity,
    string message)
    : A2AException(A2AErrorCode.InvalidAgentResponse, message)
{
    /// <summary>Why the card was refused.</summary>
    public AgentRegistrationRejection Reason { get; } = reason;

    /// <summary>The identity of the refused card.</summary>
    public PeerIdentity Identity { get; } = identity;

    /// <summary>The registered identity that caused the refusal, or <see langword="null" /> when none did.</summary>
    public PeerIdentity? ConflictingIdentity { get; } = conflictingIdentity;
}