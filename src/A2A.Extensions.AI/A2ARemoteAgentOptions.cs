using A2A.Client;

namespace A2A.Extensions.AI;

/// <summary>Options shared by <see cref="A2AAgentFunction"/> and <see cref="A2AChatClient"/>.</summary>
public abstract class A2ARemoteAgentOptions
{
    /// <summary>Request options (headers, credentials, time limit) passed to every call to the remote agent. Null for the defaults.</summary>
    public A2ARequestOptions? RequestOptions { get; set; }

    /// <summary>
    /// Called for every artifact the remote agent sends, before the content is used. <see cref="A2AClientOptions.OnMessageReceived"/>
    /// only sees messages, and agents mostly answer in artifacts. Throw to abort. Exceptions reach the caller. For a stream
    /// the hook sees each chunk, not the joined artifact.
    /// </summary>
    public Func<Artifact, CancellationToken, ValueTask>? OnArtifactReceived { get; set; }
}
