namespace A2A.Client;

/// <summary>
/// Builds a transport for one protocol binding. Add one to <see cref="A2AClientOptions.TransportFactories"/> so that
/// <see cref="A2AClientFactory"/> can select bindings beyond the built-in JSON-RPC and HTTP+JSON (for example gRPC from the
/// <c>A2A.Client.Grpc</c> package).
/// </summary>
public interface IA2ATransportFactory
{
    /// <summary>The <see cref="AgentInterface.ProtocolBinding"/> this factory serves, e.g. <see cref="ProtocolBindings.Grpc"/>.</summary>
    string ProtocolBinding { get; }

    /// <summary>
    /// True when this factory can connect to <paramref name="agentInterface"/> (for example its URL parses). Interfaces it
    /// rejects are skipped during selection.
    /// </summary>
    bool CanCreate(AgentInterface agentInterface);

    /// <summary>Creates the transport. <paramref name="options"/> is the client's copy, already holding the interface's tenant and version.</summary>
    IA2AClient Create(AgentInterface agentInterface, HttpClient httpClient, A2AClientOptions options);
}
