namespace A2A.Validation;

public static partial class A2AValidator
{
    /// <summary>
    ///     Checks a stream event an agent sent (spec 5.7: messages with missing REQUIRED fields should be rejected). A task,
    ///     message, status update, artifact update or message update must have its REQUIRED fields: ids, a status with a
    ///     state, a message with an id, a role and parts, and an artifact with an id and parts (an append chunk may have
    ///     none). A stream error event is not checked. Throws <see cref="A2AErrorCode.InvalidAgentResponse" />.
    /// </summary>
    public static void ValidateResponse(StreamResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        switch (response)
        {
            case TaskEvent { Task: var task }:
                CheckTask(task, "task");
                break;
            case MessageEvent { Message: var message }:
                CheckMessage(message, "message");
                break;
            case StatusUpdateEvent { StatusUpdate: var update }:
                Required(update, "statusUpdate");
                CheckIds(update.TaskId, update.ContextId, "statusUpdate");
                CheckStatus(update.Status, "statusUpdate.status");
                break;
            case ArtifactUpdateEvent { ArtifactUpdate: var update }:
                Required(update, "artifactUpdate");
                CheckIds(update.TaskId, update.ContextId, "artifactUpdate");
                Required(update.Artifact, "artifactUpdate.artifact");
                ValidateArtifact(update.Artifact, "artifactUpdate.artifact", update.Append);
                break;
            case MessageUpdateEvent { MessageUpdate: var update }:
                Required(update, "messageUpdate");
                CheckIds(update.TaskId, update.ContextId, "messageUpdate");
                CheckMessage(update.Message, "messageUpdate.message");
                break;
        }
    }

    /// <summary>
    ///     Checks the result of SendMessage: the task or the message, as <see cref="ValidateResponse(StreamResponse)" />.
    ///     Throws <see cref="A2AErrorCode.InvalidAgentResponse" />.
    /// </summary>
    public static void ValidateResponse(SendMessageResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        switch (response)
        {
            case TaskResponse { Task: var task }:
                CheckTask(task, "task");
                break;
            case MessageResponse { Message: var message }:
                CheckMessage(message, "message");
                break;
        }
    }

    /// <summary>
    ///     Checks a task an agent returned: an id, a status with a state, and valid artifacts and history messages. Stored
    ///     artifacts may have no parts. Throws <see cref="A2AErrorCode.InvalidAgentResponse" />.
    /// </summary>
    public static void ValidateResponse(AgentTask task)
    {
        CheckTask(task, "task");
    }

    private static void CheckTask(AgentTask? task, string path)
    {
        Required(task, path);
        if (string.IsNullOrWhiteSpace(task!.Id)) throw Invalid($"{path}.id is required");
        CheckStatus(task.Status, $"{path}.status");
        if (task.Artifacts is { } artifacts)
            for (var i = 0; i < artifacts.Count; i++)
            {
                Required(artifacts[i], $"{path}.artifacts[{i}]");
                ValidateArtifact(artifacts[i], $"{path}.artifacts[{i}]", true);
            }

        if (task.History is { } history)
            for (var i = 0; i < history.Count; i++)
                CheckMessage(history[i], $"{path}.history[{i}]");
    }

    private static void CheckStatus(AgentTaskStatus? status, string path)
    {
        Required(status, path);
        if (status!.State == TaskState.Unspecified) throw Invalid($"{path}.state is required");
        if (status.Message is { } message) CheckMessage(message, $"{path}.message");
    }

    private static void CheckMessage(Message? message, string path)
    {
        Required(message, path);
        if (string.IsNullOrWhiteSpace(message!.MessageId)) throw Invalid($"{path}.messageId is required");
        if (message.Role == Role.Unspecified) throw Invalid($"{path}.role is required");
        if (message.Parts is not { Count: > 0 }) throw Invalid($"{path}.parts must contain at least one part");
        for (var i = 0; i < message.Parts.Count; i++)
            if (message.Parts[i] is null)
                throw Invalid($"{path}.parts[{i}] is null");
    }

    private static void CheckIds(string? taskId, string? contextId, string path)
    {
        if (string.IsNullOrWhiteSpace(taskId)) throw Invalid($"{path}.taskId is required");
        if (string.IsNullOrWhiteSpace(contextId)) throw Invalid($"{path}.contextId is required");
    }

    private static void Required(object? value, string path)
    {
        if (value is null) throw Invalid($"{path} is required");
    }

    private static A2AException Invalid(string message)
    {
        return new A2AException(A2AErrorCode.InvalidAgentResponse, $"Invalid agent response: {message} (spec 5.7)");
    }
}
