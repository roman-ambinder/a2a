using System.Diagnostics.CodeAnalysis;

namespace A2A.Extensions.AgentFramework;

/// <summary>What an <see cref="AIAgentExecutor" /> does when the agent asks for a tool approval.</summary>
[Experimental("A2AEXP006", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public enum ToolApprovalMode
{
    /// <summary>
    ///     An approval request is not handled: with no other output, the task fails, as for a tool call that nothing ran.
    ///     The default.
    /// </summary>
    Off,

    /// <summary>
    ///     The caller is asked to confirm the exact tool call, bound by a hash. Not available yet: choosing it throws
    ///     <see cref="NotSupportedException" />.
    /// </summary>
    Bound
}
