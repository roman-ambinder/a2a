using System.Runtime.CompilerServices;

namespace A2A.Client;

/// <summary>
///     Shortcuts over the A2A operations for any <see cref="IA2AClient" />: send text or a message, read, cancel or follow
///     a task by id, wait for a task, and list every task page by page.
/// </summary>
public static class A2AClientExtensions
{
    /// <summary>
    ///     Sends a text message. Pass <paramref name="taskId" />/<paramref name="contextId" /> to continue a
    ///     conversation.
    /// </summary>
    public static Task<SendMessageResponse> SendMessageAsync(this IA2AClient client, string text, string? contextId = null, string? taskId = null,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser(text, contextId, taskId) }, options,
            cancellationToken);
    }

    /// <summary>Sends <paramref name="message" /> and waits for the result (spec §3.1.1).</summary>
    public static Task<SendMessageResponse> SendMessageAsync(this IA2AClient client, Message message, SendMessageConfiguration? configuration = null,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.SendMessageAsync(new SendMessageRequest { Message = message, Configuration = configuration }, options,
            cancellationToken);
    }

    /// <summary>Sends <paramref name="message" /> and streams the updates (spec §3.1.2).</summary>
    public static IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(this IA2AClient client, Message message,
        SendMessageConfiguration? configuration = null, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.SendStreamingMessageAsync(new SendMessageRequest { Message = message, Configuration = configuration },
            options, cancellationToken);
    }

    /// <summary>
    ///     Fetches a task (spec §3.1.3). <paramref name="historyLength" /> limits the history; null means the server
    ///     default.
    /// </summary>
    public static Task<AgentTask> GetTaskAsync(this IA2AClient client, string taskId, int? historyLength = null, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.GetTaskAsync(new GetTaskRequest { Id = taskId, HistoryLength = historyLength }, options,
            cancellationToken);
    }

    /// <summary>Asks the agent to cancel a task (spec §3.1.5).</summary>
    public static Task<AgentTask> CancelTaskAsync(this IA2AClient client, string taskId, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, options, cancellationToken);
    }

    /// <summary>Streams updates of an existing task (spec §3.1.6).</summary>
    public static IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(this IA2AClient client, string taskId, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId }, options, cancellationToken);
    }

    /// <summary>
    ///     Polls a task until it reaches a terminal or interrupted state. Use when streaming and push are not available,
    ///     or to recover after a dropped stream.
    /// </summary>
    public static async Task<AgentTask> WaitForTaskAsync(this IA2AClient client, string taskId, TimeSpan? pollInterval = null,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var delay = pollInterval ?? TimeSpan.FromSeconds(1);
        while (true)
        {
            var task = await client.GetTaskAsync(taskId, 0, options, cancellationToken).ConfigureAwait(false);
            if (task.Status.State.IsTerminal() || task.Status.State.IsInterrupted()) return task;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Pages through all tasks matching <paramref name="request" />.</summary>
    public static async IAsyncEnumerable<AgentTask> ListAllTasksAsync(this IA2AClient client, ListTasksRequest? request = null,
        A2ARequestOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        request ??= new ListTasksRequest();
        while (true)
        {
            var page = await client.ListTasksAsync(request, options, cancellationToken).ConfigureAwait(false);
            foreach (var t in page.Tasks) yield return t;
            if (string.IsNullOrEmpty(page.NextPageToken)) yield break;
            request = new ListTasksRequest
            {
                Tenant = request.Tenant, ContextId = request.ContextId, Status = request.Status,
                PageSize = request.PageSize,
                PageToken = page.NextPageToken, HistoryLength = request.HistoryLength,
                StatusTimestampAfter = request.StatusTimestampAfter, IncludeArtifacts = request.IncludeArtifacts
            };
        }
    }
}
