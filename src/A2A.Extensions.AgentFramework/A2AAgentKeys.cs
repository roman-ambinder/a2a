using System.Diagnostics.CodeAnalysis;
using A2A.Extensions.AI;

namespace A2A.Extensions.AgentFramework;

/// <summary>
///     Keys of the <c>AdditionalProperties</c> this package reads and writes: on the <c>AgentRunOptions</c> a hosted
///     agent receives, and on the responses of a remote agent. They are local data and never go on the wire.
/// </summary>
[Experimental("A2AEXP006", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public static class A2AAgentKeys
{
    /// <summary>The A2A task id, as a string. Same value as <see cref="A2AChatKeys.TaskId" />.</summary>
    public const string TaskId = A2AChatKeys.TaskId;

    /// <summary>The A2A context id, as a string.</summary>
    public const string ContextId = "a2a.contextId";

    /// <summary>
    ///     The ids of the tasks the caller's message refers to (<c>Message.ReferenceTaskIds</c>), as a <c>string[]</c>.
    ///     Present only when the message has some. The caller chose them: treat them as untrusted.
    /// </summary>
    public const string ReferenceTaskIds = "a2a.referenceTaskIds";

    /// <summary>
    ///     The task state, as a string (<c>completed</c>, <c>input-required</c> and so on). Same value as
    ///     <see cref="A2AChatKeys.TaskState" />.
    /// </summary>
    public const string TaskState = A2AChatKeys.TaskState;
}
