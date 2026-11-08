namespace A2A.Server;

/// <summary>
/// Supplies the Agent Card the server serves publicly and checks capabilities against (streaming, push notifications, the
/// extended card, declared extensions).
/// </summary>
/// <remarks>
/// The card is the host's <see cref="A2AServerOptions.AgentCard"/> completed once: a copy, never the host's instance, with one
/// entry in <see cref="AgentCapabilities.Extensions"/> for every registered extension operation that the card does not list.
/// The ASP.NET Core package also fills an empty <see cref="AgentCard.SupportedInterfaces"/> from the mapped bindings.
/// Implementations must return the same instance on every call once the host has started (the ASP.NET Core completer may build a provisional card before start, when the listen addresses are unknown, and does not keep it), and must not change it after returning it: it is shared by
/// every request and by the cached, signed card.
/// </remarks>
public interface IAgentCardProvider
{
    /// <summary>The completed Agent Card. The same instance on every call. Callers must not change it.</summary>
    AgentCard Get();
}
