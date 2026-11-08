using Microsoft.Extensions.AI;

namespace A2A.Extensions.AI;

/// <summary>Options for <see cref="ChatClientAgentExecutor"/>.</summary>
public sealed class ChatClientAgentOptions
{
    private int _maxHistoryMessages = 100;
    private int _streamChunkSize = 64;

    /// <summary>Added as the first system message of every run. Null for none.</summary>
    public string? Instructions { get; set; }

    /// <summary>Options for each model call (tools, temperature, ...). Cloned for each run. <see cref="ChatOptions.ConversationId"/> is always cleared, so callers never share provider state.</summary>
    public ChatOptions? ChatOptions { get; set; }

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
    /// Whether URL parts from callers reach the model. Default false: a model provider may fetch the URL, which lets a
    /// caller make your server request addresses of its choice. A message with a URL part is rejected.
    /// </summary>
    public bool AllowUrlParts { get; set; }

    /// <summary>End each turn in <c>INPUT_REQUIRED</c> instead of <c>COMPLETED</c>, so the caller can continue the same task. The full reply is stored as a status message, so the next turn sees it.</summary>
    public bool KeepConversationOpen { get; set; }
}
