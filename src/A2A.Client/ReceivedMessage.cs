namespace A2A.Client;

/// <summary>Where in a response <see cref="A2AClientOptions.OnMessageReceived" /> found a message.</summary>
public enum ReceivedMessageSource
{
    /// <summary>A direct reply: the agent answered with a message instead of a task.</summary>
    DirectReply,

    /// <summary>The message of a status update event (streaming).</summary>
    StatusUpdate,

    /// <summary>The latest status message of a task snapshot (a task response, a stream's task event, GetTask or CancelTask).</summary>
    TaskSnapshot
}

/// <summary>A message from the agent, passed to <see cref="A2AClientOptions.OnMessageReceived" />.</summary>
/// <param name="Message">The message as received. Changes to it are seen by the caller.</param>
/// <param name="Source">Where the message was found.</param>
/// <param name="TaskId">The task the message belongs to, if any.</param>
/// <param name="ContextId">The conversation context, if known.</param>
public sealed record ReceivedMessage(Message Message, ReceivedMessageSource Source, string? TaskId, string? ContextId);