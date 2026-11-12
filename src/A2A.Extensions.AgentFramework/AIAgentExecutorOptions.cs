using System.Diagnostics.CodeAnalysis;
using A2A.Server;
using Microsoft.Agents.AI;

namespace A2A.Extensions.AgentFramework;

/// <summary>Options for <see cref="AIAgentExecutor" />.</summary>
/// <remarks>
///     The agent's instructions and chat options belong to the <see cref="AIAgent" />, so they are not options here. The
///     reply, history and security settings are on <see cref="A2A.Extensions.AI.A2AModelAgentOptions" />;
///     <c>MaxHistoryMessages</c> and <c>UseContextMemory</c> apply in <see cref="AgentSessionMode.Stateless" /> mode, where
///     the history comes from the task. Out-of-range values throw <see cref="ArgumentOutOfRangeException" /> when set.
/// </remarks>
[Experimental("A2AEXP006", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class AIAgentExecutorOptions : A2A.Extensions.AI.A2AModelAgentOptions
{
    /// <summary>Where the conversation is kept between turns. Default <see cref="AgentSessionMode.Stateless" />.</summary>
    public AgentSessionMode SessionMode { get; set; }

    /// <summary>What to do when the agent asks for a tool approval. Default <see cref="ToolApprovalMode.Off" />.</summary>
    public ToolApprovalMode ToolApprovals { get; set; }

    /// <summary>
    ///     Called for each run on a new <see cref="AgentRunOptions" />, after the executor set
    ///     <see cref="A2AAgentKeys.TaskId" />, <see cref="A2AAgentKeys.ContextId" /> and
    ///     <see cref="A2AAgentKeys.ReferenceTaskIds" /> in its <c>AdditionalProperties</c>. Null for none.
    /// </summary>
    /// <remarks>
    ///     The request context carries caller data (<see cref="AgentRequestContext.Metadata" />, the message's metadata).
    ///     The executor never copies it to the agent; copy only what you trust.
    /// </remarks>
    public Action<AgentRunOptions, AgentRequestContext>? ConfigureRunOptions { get; set; }

    /// <summary>Throws for an unknown mode or a combination that cannot work.</summary>
    internal void ThrowIfUnsupported()
    {
        if (SessionMode is not (AgentSessionMode.Stateless or AgentSessionMode.Stored))
            throw new ArgumentOutOfRangeException(nameof(SessionMode), SessionMode, "Unknown session mode.");
        if (ToolApprovals is not (ToolApprovalMode.Off or ToolApprovalMode.Bound))
            throw new ArgumentOutOfRangeException(nameof(ToolApprovals), ToolApprovals, "Unknown tool approval mode.");
        // The request must survive until the caller answers, in a later execution.
        if (ToolApprovals == ToolApprovalMode.Bound && SessionMode != AgentSessionMode.Stored)
            throw new InvalidOperationException(
                "ToolApprovals = ToolApprovalMode.Bound needs SessionMode = AgentSessionMode.Stored: the approval request is kept in the session until the caller answers.");
    }
}
