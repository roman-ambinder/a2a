namespace A2A.Client;

/// <summary>
///     A transport that knows its protocol binding, reported as the <c>a2a.binding</c> tag of <see cref="A2AClient" />'s
///     spans.
///     The built-in transports implement it; a third-party transport may, so its spans name its binding too.
/// </summary>
public interface ITransportBinding
{
    /// <summary>A <see cref="ProtocolBindings" /> value.</summary>
    string Binding { get; }
}