using System.Collections.Concurrent;
using A2A.Client;
using A2A.Client.Outbox;

namespace A2A.Tests;

/// <summary>An <see cref="IA2AClient"/> whose send and get-task are scripted by the test. Everything else throws. Compiled into A2A.Tests and A2A.Stores.Tests.</summary>
public sealed class ScriptedA2AClient : IA2AClient
{
    private readonly ConcurrentQueue<string> _sendKeys = new();
    private readonly ConcurrentQueue<string> _gets = new();

    /// <summary>What a send does. Default: a working task named after the idempotency key.</summary>
    public Func<SendMessageRequest, A2ARequestOptions?, CancellationToken, Task<SendMessageResponse>> OnSend { get; set; } =
        (_, options, _) => System.Threading.Tasks.Task.FromResult<SendMessageResponse>(new TaskResponse(Make("task-" + options?.IdempotencyKey, TaskState.Working)));

    /// <summary>What a get-task does. Default: the task, completed.</summary>
    public Func<GetTaskRequest, A2ARequestOptions?, CancellationToken, Task<AgentTask>> OnGetTask { get; set; } =
        (request, _, _) => System.Threading.Tasks.Task.FromResult(Make(request.Id, TaskState.Completed));

    /// <summary>The <c>Idempotency-Key</c> of every send, in call order.</summary>
    public IReadOnlyCollection<string> SendKeys => _sendKeys;

    /// <summary>The task id of every get-task, in call order.</summary>
    public IReadOnlyCollection<string> Gets => _gets;

    public static AgentTask Make(string id, TaskState state) => new() { Id = id, ContextId = "ctx-" + id, Status = new AgentTaskStatus { State = state } };

    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        _sendKeys.Enqueue(options?.IdempotencyKey ?? "");
        return OnSend(request, options, cancellationToken);
    }

    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        _gets.Enqueue(request.Id);
        return OnGetTask(request, options, cancellationToken);
    }

    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>
/// Wraps a store and throws at the writes the test names, to simulate a crash at that write: the write does not happen.
/// Compiled into A2A.Tests and A2A.Stores.Tests.
/// </summary>
public sealed class CrashingClientOutboxStore(ClientOutboxStore inner) : ClientOutboxStore
{
    public sealed class CrashException : Exception;

    /// <summary>Names of store methods that crash instead of writing, for example <c>MarkTrackingAsync</c>.</summary>
    public HashSet<string> CrashOn { get; } = [];

    private void Check(string method)
    {
        if (CrashOn.Contains(method)) throw new CrashException();
    }

    public override ValueTask<bool> EnqueueAsync(ClientOutboxMessage message, CancellationToken cancellationToken = default) => inner.EnqueueAsync(message, cancellationToken);
    public override ValueTask<ClientOutboxEntry?> GetAsync(string id, CancellationToken cancellationToken = default) => inner.GetAsync(id, cancellationToken);
    public override ValueTask<IReadOnlyList<ClientOutboxEntry>> LeaseDueAsync(int max, TimeSpan lease, CancellationToken cancellationToken = default) => inner.LeaseDueAsync(max, lease, cancellationToken);
    public override ValueTask<IReadOnlyList<ClientOutboxEntry>> LeaseResultsAsync(string? correlation, int max, TimeSpan lease, CancellationToken cancellationToken = default) => inner.LeaseResultsAsync(correlation, max, lease, cancellationToken);
    public override ValueTask<bool> HastenAsync(string peer, string taskId, CancellationToken cancellationToken = default) => inner.HastenAsync(peer, taskId, cancellationToken);
    /// <summary>How many times purge was called, and (when set) what it answers for the given max.</summary>
    public int PurgeCalls;

    public Func<int, int>? OnPurge { get; set; }

    public override ValueTask<int> PurgeCollectedAsync(TimeSpan olderThan, int max, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref PurgeCalls);
        return OnPurge is { } f ? new(f(max)) : inner.PurgeCollectedAsync(olderThan, max, cancellationToken);
    }

    public override ValueTask<bool> RescheduleAsync(ClientOutboxEntry leased, TimeSpan delay, string? error, bool inDoubt, CancellationToken cancellationToken = default)
    {
        Check(nameof(RescheduleAsync));
        return inner.RescheduleAsync(leased, delay, error, inDoubt, cancellationToken);
    }

    public override ValueTask<bool> MarkTrackingAsync(ClientOutboxEntry leased, string taskId, string? contextId, TaskState state, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        Check(nameof(MarkTrackingAsync));
        return inner.MarkTrackingAsync(leased, taskId, contextId, state, delay, cancellationToken);
    }

    public override ValueTask<bool> FinishAsync(ClientOutboxEntry leased, ClientOutboxOutcome outcome, CancellationToken cancellationToken = default)
    {
        Check(nameof(FinishAsync));
        return inner.FinishAsync(leased, outcome, cancellationToken);
    }

    public override ValueTask<bool> AcknowledgeAsync(ClientOutboxEntry leased, CancellationToken cancellationToken = default)
    {
        Check(nameof(AcknowledgeAsync));
        return inner.AcknowledgeAsync(leased, cancellationToken);
    }
}
