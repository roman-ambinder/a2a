using System.Diagnostics.CodeAnalysis;
using A2A.Client;

namespace A2A.Extensions.AgentFramework;

/// <summary>Uses a remote A2A agent as a Microsoft Agent Framework agent.</summary>
[Experimental("A2AEXP006", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public static class A2AClientAgentExtensions
{
    /// <summary>
    ///     Returns an <see cref="A2ARemoteAIAgent" /> that sends to the remote agent behind <paramref name="client" />.
    ///     <paramref name="options" /> defaults to a new <see cref="A2AAIAgentOptions" />.
    /// </summary>
    public static A2ARemoteAIAgent AsAIAgent(this IA2AClient client, A2AAIAgentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new A2ARemoteAIAgent(client, options ?? new A2AAIAgentOptions());
    }
}
