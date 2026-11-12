using System.Diagnostics.CodeAnalysis;
using A2A.Server;
using Microsoft.Agents.AI;

namespace A2A.Extensions.AgentFramework;

/// <summary>Options for <see cref="AIAgentExecutor" />.</summary>
/// <remarks>
///     The agent's instructions and chat options belong to the <see cref="AIAgent" />, so they are not options here.
///     Out-of-range values throw <see cref="ArgumentOutOfRangeException" /> when set.
/// </remarks>
[Experimental("A2AEXP006", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class AIAgentExecutorOptions
{
    private long _contextMemoryMaxBytes = ConversationMemory.DefaultMaxBytes;
    private int _contextMemoryMaxTasks = ConversationMemory.DefaultMaxTasks;
    private int _maxHistoryMessages = 100;
    private int _streamChunkSize = 64;

    /// <summary>Name of the artifact that holds the reply. Default <c>response</c>.</summary>
    public string ArtifactName { get; set; } = "response";

    /// <summary>
    ///     How many of the newest history messages go to the agent, in <see cref="AgentSessionMode.Stateless" /> mode. At
    ///     least 1. Default 100.
    /// </summary>
    public int MaxHistoryMessages
    {
        get => _maxHistoryMessages;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxHistoryMessages = value;
        }
    }

    /// <summary>How many text characters to collect before a chunk is sent to the caller. At least 1. Default 64.</summary>
    public int StreamChunkSize
    {
        get => _streamChunkSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _streamChunkSize = value;
        }
    }

    /// <summary>
    ///     Whether URL parts from callers reach the agent. Default false: the agent or its model provider may fetch the URL,
    ///     which lets a caller make your server request addresses of its choice. A message with a URL part is rejected.
    /// </summary>
    public bool AllowUrlParts { get; set; }

    /// <summary>
    ///     End each turn in <c>INPUT_REQUIRED</c> instead of <c>COMPLETED</c>, so the caller can continue the same task.
    ///     The full reply is stored as a status message, so the next turn sees it.
    /// </summary>
    public bool KeepConversationOpen { get; set; }

    /// <summary>
    ///     In <see cref="AgentSessionMode.Stateless" /> mode, give the agent the earlier tasks of the same context
    ///     (<see cref="AgentRequestContext.Conversation" />) and the context's summary when there is one, as user messages.
    ///     Default false. When on, a completed task also stores the reply as its status message, so the reply is stored
    ///     twice and counts twice toward the task size limit.
    /// </summary>
    public bool UseContextMemory { get; set; }

    /// <summary>With <see cref="UseContextMemory" />: how many earlier tasks to read. At least 1. Default 20.</summary>
    public int ContextMemoryMaxTasks
    {
        get => _contextMemoryMaxTasks;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _contextMemoryMaxTasks = value;
        }
    }

    /// <summary>
    ///     With <see cref="UseContextMemory" />: the size limit for earlier messages, as the UTF-8 length of their JSON.
    ///     At least 1. Default 262144.
    /// </summary>
    public long ContextMemoryMaxBytes
    {
        get => _contextMemoryMaxBytes;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1L);
            _contextMemoryMaxBytes = value;
        }
    }

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

    /// <summary>Throws for a mode this version cannot run yet.</summary>
    internal void ThrowIfUnsupported()
    {
        if (SessionMode != AgentSessionMode.Stateless)
            throw new NotSupportedException($"SessionMode {SessionMode} is not available yet. Use AgentSessionMode.Stateless.");
        if (ToolApprovals != ToolApprovalMode.Off)
            throw new NotSupportedException($"ToolApprovals {ToolApprovals} is not available yet. Use ToolApprovalMode.Off.");
    }
}
