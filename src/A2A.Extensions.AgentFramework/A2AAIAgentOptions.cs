using System.Diagnostics.CodeAnalysis;
using A2A.Extensions.AI;

namespace A2A.Extensions.AgentFramework;

/// <summary>Options for <see cref="A2ARemoteAIAgent" />.</summary>
/// <remarks>
///     Name and description come only from here, never from the remote Agent Card: Agent Framework can put an agent's
///     name and description in front of a model (orchestration, an agent used as a tool), and a card is remote text.
/// </remarks>
[Experimental("A2AEXP006", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class A2AAIAgentOptions : A2ARemoteAgentOptions
{
    /// <summary>The agent id. Null for a random id per agent instance.</summary>
    public string? Id { get; set; }

    /// <summary>The agent name. Null for none.</summary>
    public string? Name { get; set; }

    /// <summary>The agent description. Null for none.</summary>
    public string? Description { get; set; }
}
