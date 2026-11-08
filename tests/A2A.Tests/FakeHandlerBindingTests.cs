using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using A2A.Json;
using A2A.Server;
using A2A.Server.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests;

/// <summary>The bindings talk to the server core only through <see cref="IA2ARequestHandler"/>: a fake one receives every call.</summary>
public sealed class FakeHandlerBindingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Default_registration_resolves_the_interface_to_the_handler()
    {
        await using var host = await TestAgentHost.StartAsync();
        Assert.Same(host.Handler, host.App.Services.GetRequiredService<IA2ARequestHandler>());
    }

    [Fact]
    public async Task JsonRpc_GetTask_reaches_fake()
    {
        var fake = new FakeHandler();
        await using var host = await StartAsync(fake);

        using var response = await host.Http("alice").SendAsync(Rpc(A2AProtocol.Methods.GetTask, """{"id":"t1"}"""));
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("\"fake-task\"", body);
        Assert.Equal("t1", Assert.IsType<GetTaskRequest>(Assert.Single(fake.Calls).Request).Id);
    }

    [Fact]
    public async Task Rest_GetTask_reaches_fake()
    {
        var fake = new FakeHandler();
        await using var host = await StartAsync(fake);

        var request = new HttpRequestMessage(HttpMethod.Get, "/a2a/tasks/t1");
        request.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        using var response = await host.Http("alice").SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("\"fake-task\"", body);
        Assert.Equal("t1", Assert.IsType<GetTaskRequest>(Assert.Single(fake.Calls).Request).Id);
    }

    [Fact]
    public async Task JsonRpc_stream_reaches_fake_with_last_event_id()
    {
        var fake = new FakeHandler();
        await using var host = await StartAsync(fake);

        var send = A2AJson.SerializeToElement(new SendMessageRequest { Message = Message.FromUser("hi") }).GetRawText();
        var request = Rpc(A2AProtocol.Methods.SendStreamingMessage, send);
        request.Headers.TryAddWithoutValidation("Last-Event-ID", "t1:7");
        using var cts = new CancellationTokenSource(Timeout);
        using var response = await host.Http("alice").SendAsync(request, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("id: t1:8", body);
        Assert.Contains("\"fake-context\"", body);
        Assert.Contains("id: t1:9", body);
        Assert.Contains("TASK_STATE_COMPLETED", body);
        var call = Assert.Single(fake.Calls);
        Assert.Equal(nameof(IA2ARequestHandler.SendStreamingMessageAsync), call.Method);
        Assert.Equal("t1:7", call.LastEventId);
    }

    private static Task<TestAgentHost> StartAsync(FakeHandler fake) =>
        TestAgentHost.StartAsync(services: s => s.Replace(ServiceDescriptor.Singleton<IA2ARequestHandler>(fake)));

    private static HttpRequestMessage Rpc(string method, string paramsJson)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/a2a")
        {
            Content = new StringContent($$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}","params":{{paramsJson}}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        return request;
    }

    private sealed record Call(string Method, object Request, string? LastEventId = null);

    /// <summary>Records each call and returns canned results.</summary>
    private sealed class FakeHandler : IA2ARequestHandler
    {
        public ConcurrentQueue<Call> Calls { get; } = new();

        public AgentCard AgentCard { get; } = TestAgentHost.DefaultCard();

        private static AgentTask Task(TaskState state = TaskState.Working, string id = "fake-task") =>
            new() { Id = id, ContextId = "fake-context", Status = new() { State = state } };

        private T Record<T>(string method, object request, T result)
        {
            Calls.Enqueue(new Call(method, request));
            return result;
        }

        public void ValidateCall(ServerCallContext call) { }

        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, ServerCallContext call, CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(Record<SendMessageResponse>(nameof(SendMessageAsync), request, new TaskResponse(Task())));

        public IAsyncEnumerable<SequencedEvent> SendStreamingMessageAsync(SendMessageRequest request, ServerCallContext call,
            string? lastEventId = null, Action? onCaughtUp = null, CancellationToken ct = default)
        {
            Calls.Enqueue(new Call(nameof(SendStreamingMessageAsync), request, lastEventId));
            return Events(ct);
        }

        private static async IAsyncEnumerable<SequencedEvent> Events([EnumeratorCancellation] CancellationToken ct)
        {
            await System.Threading.Tasks.Task.Yield();
            yield return new SequencedEvent(8, StreamResponse.Of(Task(id: "t1")));
            yield return new SequencedEvent(9, StreamResponse.Of(new TaskStatusUpdateEvent
            {
                TaskId = "t1", ContextId = "fake-context", Status = new() { State = TaskState.Completed },
            }));
        }

        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, ServerCallContext call, CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(Record(nameof(GetTaskAsync), request, Task()));

        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, ServerCallContext call, CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(Record(nameof(ListTasksAsync), request, new ListTasksResponse { Tasks = [Task()] }));

        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, ServerCallContext call, CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(Record(nameof(CancelTaskAsync), request, Task(TaskState.Canceled)));

        public IAsyncEnumerable<SequencedEvent> SubscribeToTaskAsync(SubscribeToTaskRequest request, ServerCallContext call,
            long? resumeAfter = null, Action? onCaughtUp = null, CancellationToken ct = default)
        {
            Calls.Enqueue(new Call(nameof(SubscribeToTaskAsync), request));
            return Events(ct);
        }

        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, ServerCallContext call, CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(Record(nameof(CreateTaskPushNotificationConfigAsync), config, config));

        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken ct = default) =>
            throw A2AException.TaskNotFound(request.TaskId);

        public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, ServerCallContext call, CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(Record(nameof(ListTaskPushNotificationConfigsAsync), request, new ListTaskPushNotificationConfigsResponse()));

        public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken ct = default) =>
            Record<Task>(nameof(DeleteTaskPushNotificationConfigAsync), request, System.Threading.Tasks.Task.CompletedTask);

        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, ServerCallContext call, CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(Record(nameof(GetExtendedAgentCardAsync), request, AgentCard));

        public Task<System.Text.Json.JsonElement> InvokeExtensionAsync(string method, System.Text.Json.JsonElement parameters, ServerCallContext call, CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(Record(nameof(InvokeExtensionAsync), parameters, parameters));
    }
}
