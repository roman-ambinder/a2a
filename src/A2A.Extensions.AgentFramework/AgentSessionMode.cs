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
    ///     Sessions are kept in an <c>AgentSessionStore</c>, one per context and caller (tenant and owner), and only the
    ///     new message is sent. <see cref="AIAgentExecutorOptions.MaxHistoryMessages" /> and
    ///     <see cref="AIAgentExecutorOptions.UseContextMemory" /> do not shape the prompt in this mode. Register an
    ///     <c>AgentSessionStore</c> in DI. Two tasks of one context that run at the same time race on the session: the last
    ///     save wins.
    /// </summary>
    Stored
}
