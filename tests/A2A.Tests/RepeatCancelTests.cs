using System.Security.Claims;
using A2A.Client;
using A2A.Server;

namespace A2A.Tests;

/// <summary>A repeat CancelTask: the server default is TaskNotCancelable (TCK CORE-CANCEL-002), IdempotentCancel answers the task, the client softens it.</summary>
public class RepeatCancelTests
{
    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    private static ServerCallContext Call() => new()
    {
        Principal = "alice",
        ProtocolVersion = A2AProtocol.Version,
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "Test")),
    };

    private static async Task<string> CanceledTaskAsync(TestAgentHost host)
    {
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();
        await client.CancelTaskAsync(task.Id);
        return task.Id;
    }

    [Fact]
    public async Task Server_default_refuses_a_repeat_cancel()
    {
        await using var host = await TestAgentHost.StartAsync();
        var id = await CanceledTaskAsync(host);

        var ex = await Assert.ThrowsAsync<A2AException>(async () => await host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = id }, Call()));

        Assert.Equal(A2AErrorCode.TaskNotCancelable, ex.Code);
    }

    [Fact]
    public async Task Idempotent_cancel_answers_the_canceled_task()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.IdempotentCancel = true);
        var id = await CanceledTaskAsync(host);

        var again = await host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = id }, Call());

        Assert.Equal(id, again.Id);
        Assert.Equal(TaskState.Canceled, again.Status.State);
    }

    [Fact]
    public async Task Idempotent_cancel_still_refuses_other_terminal_states()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.IdempotentCancel = true);
        var client = host.Client(ProtocolBindings.JsonRpc);
        var done = (await client.SendMessageAsync("task")).RequireTask();

        var ex = await Assert.ThrowsAsync<A2AException>(async () => await host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = done.Id }, Call()));

        Assert.Equal(A2AErrorCode.TaskNotCancelable, ex.Code);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Client_turns_a_repeat_cancel_into_the_canceled_task(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding, options: new A2AClientOptions { MaxRetries = 0 });
        var task = (await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();
        await client.CancelTaskAsync(task.Id);

        var again = await client.CancelTaskAsync(task.Id);

        Assert.Equal(task.Id, again.Id);
        Assert.Equal(TaskState.Canceled, again.Status.State);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Client_still_throws_for_a_completed_task(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding, options: new A2AClientOptions { MaxRetries = 0 });
        var task = (await client.SendMessageAsync("task")).RequireTask();
        Assert.Equal(TaskState.Completed, task.Status.State);

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.CancelTaskAsync(task.Id));

        Assert.Equal(A2AErrorCode.TaskNotCancelable, ex.Code);
    }

    [Fact]
    public async Task Client_rethrows_the_original_error_when_get_task_fails()
    {
        var transport = new StubTransport
        {
            Cancel = () => throw new A2AException(A2AErrorCode.TaskNotCancelable, "original"),
            Get = () => throw new A2AException(A2AErrorCode.TaskNotFound, "other"),
        };
        var client = new A2AClient(transport, new A2AClientOptions { MaxRetries = 0 });

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.CancelTaskAsync("t1"));

        Assert.Equal(A2AErrorCode.TaskNotCancelable, ex.Code);
        Assert.Contains("original", ex.Message);
    }

    [Fact]
    public async Task Client_fallback_passes_through_message_inspection_and_keeps_the_tenant()
    {
        string? tenant = null;
        var seen = new List<string>();
        var transport = new StubTransport
        {
            Cancel = () => throw new A2AException(A2AErrorCode.TaskNotCancelable, "original"),
            Get = () => new AgentTask
            {
                Id = "t1", ContextId = "c1",
                Status = new AgentTaskStatus { State = TaskState.Canceled, Message = Message.FromAgent("bye") },
            },
            OnGet = r => tenant = r.Tenant,
        };
        var options = new A2AClientOptions { MaxRetries = 0, OnMessageReceived = (m, _) => { seen.Add(m.Message.GetText()); return ValueTask.CompletedTask; } };
        var client = new A2AClient(transport, options);

        var task = await client.CancelTaskAsync(new CancelTaskRequest { Id = "t1", Tenant = "acme" });

        Assert.Equal(TaskState.Canceled, task.Status.State);
        Assert.Equal("acme", tenant);
        Assert.Equal(["bye"], seen);
    }

    [Fact]
    public async Task Client_rethrows_when_the_task_is_not_canceled()
    {
        var transport = new StubTransport
        {
            Cancel = () => throw new A2AException(A2AErrorCode.TaskNotCancelable, "original"),
            Get = () => new AgentTask { Id = "t1", ContextId = "c1", Status = new AgentTaskStatus { State = TaskState.Working } },
        };
        var client = new A2AClient(transport, new A2AClientOptions { MaxRetries = 0 });

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.CancelTaskAsync("t1"));

        Assert.Contains("original", ex.Message);
    }

    private sealed class StubTransport : IA2AClient
    {
        public Func<AgentTask>? Cancel { get; init; }
        public Func<AgentTask>? Get { get; init; }
        public Action<GetTaskRequest>? OnGet { get; init; }

        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Cancel!());

        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            OnGet?.Invoke(request);
            return Task.FromResult(Get!());
        }

        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
