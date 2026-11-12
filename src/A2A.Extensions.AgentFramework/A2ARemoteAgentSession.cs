using System.Diagnostics.CodeAnalysis;
using Microsoft.Agents.AI;

namespace A2A.Extensions.AgentFramework;

/// <summary>
///     The session of an <see cref="A2ARemoteAIAgent" />: the remote A2A context, and the remote task while it waits for
///     input. A2A keeps the conversation on the remote agent, so this is all the session holds.
/// </summary>
/// <remarks>
///     It serializes to <c>{"contextId":…,"taskId":…}</c> and nothing else: values put in
///     <see cref="AgentSession.StateBag" /> are not kept.
/// </remarks>
[Experimental("A2AEXP006", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class A2ARemoteAgentSession : AgentSession
{
    /// <summary>The remote A2A context id, once the remote agent named one. Null for a new conversation.</summary>
    public string? ContextId { get; internal set; }

    /// <summary>
    ///     The remote task id while that task waits for input (<c>input-required</c> or <c>auth-required</c>): the next run
    ///     continues it. Null otherwise; the next run then starts a new task in the context.
    /// </summary>
    public string? TaskId { get; internal set; }
}
