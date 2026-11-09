using A2A.Json;

namespace A2A.Client.Outbox;

/// <summary>How an entry ended: what the dispatcher gives to <see cref="ClientOutboxStore.FinishAsync"/>.</summary>
public sealed class ClientOutboxOutcome
{
    private ClientOutboxOutcome(ClientOutboxStatus status) => Status = status;

    /// <summary><see cref="ClientOutboxStatus.Finished"/> or <see cref="ClientOutboxStatus.Failed"/>.</summary>
    public ClientOutboxStatus Status { get; }

    /// <summary>The peer's task id, or null (a message answer, or the peer never took the message).</summary>
    public string? RemoteTaskId { get; private init; }

    /// <summary>The peer's context id, or null.</summary>
    public string? RemoteContextId { get; private init; }

    /// <summary>The state of the peer's task when the entry ended, or null.</summary>
    public TaskState? RemoteState { get; private init; }

    /// <summary>The final answer as a <see cref="SendMessageResponse"/> in JSON. Empty for <see cref="ClientOutboxStatus.Failed"/>.</summary>
    public ReadOnlyMemory<byte> Result { get; private init; }

    /// <summary>Why the entry failed. Null for <see cref="ClientOutboxStatus.Finished"/>.</summary>
    public string? Error { get; private init; }

    /// <summary>The peer gave a final answer.</summary>
    public static ClientOutboxOutcome Finished(SendMessageResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response switch
        {
            TaskResponse t => new(ClientOutboxStatus.Finished)
            {
                RemoteTaskId = t.Task.Id,
                RemoteContextId = t.Task.ContextId,
                RemoteState = t.Task.Status.State,
                Result = A2AJson.SerializeToUtf8Bytes(response),
            },
            MessageResponse m => new(ClientOutboxStatus.Finished)
            {
                RemoteTaskId = m.Message.TaskId,
                RemoteContextId = m.Message.ContextId,
                Result = A2AJson.SerializeToUtf8Bytes(response),
            },
            _ => throw new ArgumentException("Unknown response type.", nameof(response)),
        };
    }

    /// <summary>The SDK could not get an answer. The remote ids are kept when the peer had already taken the message.</summary>
    public static ClientOutboxOutcome Failed(string error, string? remoteTaskId = null, string? remoteContextId = null, TaskState? remoteState = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(ClientOutboxStatus.Failed) { Error = error, RemoteTaskId = remoteTaskId, RemoteContextId = remoteContextId, RemoteState = remoteState };
    }
}
