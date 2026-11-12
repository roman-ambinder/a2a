using System.Diagnostics.CodeAnalysis;

namespace A2A.Extensions.AgentFramework;

/// <summary>Where an <see cref="AIAgentExecutor" /> keeps the conversation between turns.</summary>
[Experimental("A2AEXP006", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public enum AgentSessionMode
{
    /// <summary>
    ///     Each execution gets a new Agent Framework session, and the prompt is built from the A2A task history. Nothing
    ///     outlives the execution. The default.
    /// </summary>
    Stateless,

    /// <summary>
    ///     Sessions are kept in an <c>AgentSessionStore</c>, one per context and caller, and only the new message is sent.
    ///     Not available yet: choosing it throws <see cref="NotSupportedException" />.
    /// </summary>
    Stored
}
