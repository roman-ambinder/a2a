using A2A.Json;

namespace A2A.Server;

/// <summary>
/// Writes <see cref="A2AProtocol.MessageRecordedAtKey"/>, the time a message was added to the task history. Only the server
/// writes it: what a client or an agent sent under this key is overwritten.
/// </summary>
internal static class MessageStamp
{
    /// <summary>Sets the key on <paramref name="message"/> itself. Only for a message the server owns (a copy of the caller's).</summary>
    public static void Set(Message message, DateTimeOffset at) =>
        (message.Metadata ??= [])[A2AProtocol.MessageRecordedAtKey] = Value(at);

    /// <summary>
    /// A shallow copy of <paramref name="message"/> with the key set, in a new metadata dictionary. Neither the message nor its
    /// dictionary is changed (the agent owns them); the parts and the other lists are shared.
    /// </summary>
    public static Message Copy(Message message, DateTimeOffset at)
    {
        var copy = new Message
        {
            MessageId = message.MessageId,
            ContextId = message.ContextId,
            TaskId = message.TaskId,
            Role = message.Role,
            Parts = message.Parts,
            Metadata = message.Metadata is { } m ? new(m) : [],
            Extensions = message.Extensions,
            ReferenceTaskIds = message.ReferenceTaskIds,
        };
        copy.Metadata[A2AProtocol.MessageRecordedAtKey] = Value(at);
        return copy;
    }

    private static System.Text.Json.JsonElement Value(DateTimeOffset at) => A2AJson.SerializeToElement(TimestampConverter.FormatUtc(at));
}
