using A2A.Server;

namespace A2A.Extensions.AI;

/// <summary>
///     What every agent that answers with a model shares: how the reply is stored and streamed, how much history the model
///     gets, and the security switches. The base of <see cref="ChatClientAgentOptions" /> (an <c>IChatClient</c>) and of
///     the Agent Framework adapter's options (an <c>AIAgent</c>). Out-of-range values throw
///     <see cref="ArgumentOutOfRangeException" /> when set.
/// </summary>
public abstract class A2AModelAgentOptions
{
    private long _contextMemoryMaxBytes = ConversationMemory.DefaultMaxBytes;
    private int _contextMemoryMaxTasks = ConversationMemory.DefaultMaxTasks;
    private int _maxHistoryMessages = 100;
    private int _streamChunkSize = 64;

    /// <summary>For the adapters' options, which derive from it.</summary>
    protected A2AModelAgentOptions()
    {
    }

    /// <summary>Name of the artifact that holds the reply. Default <c>response</c>.</summary>
    public string ArtifactName { get; set; } = "response";

    /// <summary>How many of the newest history messages go to the model. At least 1. Default 100.</summary>
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
    ///     Whether URL parts from callers reach the model. Default false: the model provider (or the agent) may fetch the
    ///     URL, which lets a caller make your server request addresses of its choice. A message with a URL part is rejected.
    /// </summary>
    public bool AllowUrlParts { get; set; }

    /// <summary>
    ///     End each turn in <c>INPUT_REQUIRED</c> instead of <c>COMPLETED</c>, so the caller can continue the same task.
    ///     The full reply is stored as a status message, so the next turn sees it.
    /// </summary>
    public bool KeepConversationOpen { get; set; }

    /// <summary>
    ///     Give the model the earlier tasks of the same context (<see cref="AgentRequestContext.Conversation" />), and the
    ///     context's summary when there is one. Default false: a new task sees only its own history. When on, a completed
    ///     task also stores the reply as its status message, because a task's history holds status messages only. So the
    ///     reply is stored twice (as the artifact and as the status message) and counts twice toward the task size limit.
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
}
