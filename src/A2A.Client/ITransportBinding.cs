namespace A2A.Client;

/// <summary>A transport that knows its protocol binding, reported as the <c>a2a.binding</c> tag of client spans.</summary>
internal interface ITransportBinding
{
    /// <summary>A <see cref="ProtocolBindings"/> value.</summary>
    string Binding { get; }
}
