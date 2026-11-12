using System.Text.Json;
using A2A.Deletion;

namespace A2A.Client;

/// <summary>
///     Client helper for the task-deletion extension (<see cref="TaskDeletion" />, <c>AddTaskDeletion()</c> on the
///     server): deletes a task the caller owns, with all its data.
/// </summary>
public static class TaskDeletionClientExtensions
{
    /// <summary>
    ///     Deletes the task <paramref name="taskId" /> with all its data: the SDK extension operation
    ///     <see cref="TaskDeletion.Method" />, with <see cref="TaskDeletion.ExtensionUri" /> activated for the call (see
    ///     <see
    ///         cref="ExtensionOperationClientExtensions.InvokeExtensionOperationAsync(IA2AClient, string, string, JsonElement, A2ARequestOptions?, CancellationToken)" />
    ///     ). The tenant is the <see cref="A2AClientOptions.Tenant" /> of the client. Only a task in a terminal state can be
    ///     deleted: cancel a running task first. The call is not retried once it was sent.
    /// </summary>
    /// <param name="client">
    ///     The client of the agent. It must implement <see cref="IA2AExtensionOperationClient" />, as
    ///     <see cref="A2AClient" /> and the built-in transports do.
    /// </param>
    /// <param name="taskId">The id of the task.</param>
    /// <param name="options">Options for the call, or null for new ones. These options are changed.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentException"><paramref name="taskId" /> is null or empty.</exception>
    /// <exception cref="A2AException">
    ///     TaskNotFound when the caller has no such task (it does not exist, or another caller owns it);
    ///     UnsupportedOperation when the task is not in a terminal state, or the client cannot call custom operations;
    ///     MethodNotFound or ExtensionSupportRequired when the agent does not serve task deletion; otherwise the error of the
    ///     agent.
    /// </exception>
    public static async Task DeleteTaskAsync(this IA2AClient client, string taskId,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(taskId);
        await client.InvokeExtensionOperationAsync(TaskDeletion.Method, TaskDeletion.ExtensionUri, Params(taskId),
                options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The params <c>{"id": taskId}</c>.</summary>
    private static JsonElement Params(string taskId)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(TaskDeletion.IdParam, taskId);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
}
