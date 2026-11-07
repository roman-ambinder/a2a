using System.Net;
using System.Text;
using System.Text.Json;
using A2A.Client;
using A2A.Server;
using A2A.Server.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests.Parity;

/// <summary>
/// Transport parity (design §7.10): every method behaves the same over JSON-RPC and HTTP+JSON. Test names are
/// <c>{Method}_{case}</c>. A case that cannot exist for a method is left out:
/// metadata round-trips only where the request has metadata (SendMessage, SendStreamingMessage, CancelTask);
/// historyLength only where a task is returned with history (SendMessage, GetTask, ListTasks);
/// a tenant mismatch needs a body tenant, which REST requests without a body do not have.
/// </summary>
public class ParityTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    public static TheoryData<string> Bindings => BindingTests.Bindings;

    /// <summary>JSON-RPC only: the REST form of the method has no body to carry a second tenant.</summary>
    public static TheoryData<string> JsonRpcOnly => new() { ProtocolBindings.JsonRpc };

    private enum Op
    {
        SendMessage, SendStreamingMessage, GetTask, ListTasks, CancelTask, SubscribeToTask,
        CreatePush, GetPush, ListPush, DeletePush, GetExtendedAgentCard,
    }

    private sealed class Switchable : A2AAuthorizationHandler
    {
        public volatile bool Allow = true;

        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2A.Server.Pipeline.A2AOperationDescriptor op, object request, CancellationToken ct) =>
            ValueTask.FromResult(Allow);
    }

    private static async Task<(AgentTask Task, string ConfigId)> SeedAsync(A2AClient client)
    {
        var task = (await client.SendMessageAsync("input")).RequireTask();
        var config = await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = task.Id, Url = "http://hooks.test/seed" });
        return (task, config.Id!);
    }

    /// <summary>Runs one method through the client. Streaming methods are driven to their first event.</summary>
    private static async Task InvokeAsync(A2AClient c, Op op, string? taskId, string configId = "x")
    {
        using var cts = new CancellationTokenSource(Wait);
        switch (op)
        {
            case Op.SendMessage:
                await c.SendMessageAsync("more", null, taskId);
                break;
            case Op.SendStreamingMessage:
            {
                await using var e = c.SendStreamingMessageAsync(Message.FromUser("more", null, taskId), null, null, cts.Token).GetAsyncEnumerator(cts.Token);
                await e.MoveNextAsync();
                break;
            }
            case Op.GetTask:
                await c.GetTaskAsync(taskId!);
                break;
            case Op.ListTasks:
                await c.ListTasksAsync(new ListTasksRequest());
                break;
            case Op.CancelTask:
                await c.CancelTaskAsync(taskId!);
                break;
            case Op.SubscribeToTask:
            {
                await using var e = c.SubscribeToTaskAsync(taskId!, null, cts.Token).GetAsyncEnumerator(cts.Token);
                await e.MoveNextAsync();
                break;
            }
            case Op.CreatePush:
                await c.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = taskId!, Url = "http://hooks.test/new" });
                break;
            case Op.GetPush:
                await c.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = taskId!, Id = configId });
                break;
            case Op.ListPush:
                await c.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = taskId! });
                break;
            case Op.DeletePush:
                await c.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = taskId!, Id = configId });
                break;
            case Op.GetExtendedAgentCard:
                await c.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(op));
        }
    }

    // ================================================================== happy path

    [Theory, MemberData(nameof(Bindings))]
    public async Task SendMessage_happy_path(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync("task")).RequireTask();
        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal("result", Assert.Single(task.Artifacts!).Name);
        Assert.Equal("hi there", (await client.SendMessageAsync("hello")).RequireMessage().GetText());
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task SendStreamingMessage_happy_path(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var events = new List<StreamResponse>();
        await foreach (var e in host.Client(binding).SendStreamingMessageAsync(Message.FromUser("task")).WithCancellation(new CancellationTokenSource(Wait).Token)) events.Add(e);
        Assert.IsType<TaskEvent>(events[0]);
        Assert.Equal(TaskState.Completed, Assert.IsType<StatusUpdateEvent>(events[^1]).StatusUpdate.Status.State);
        Assert.Equal(5, events.Count);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task GetTask_happy_path(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var sent = (await client.SendMessageAsync("task")).RequireTask();
        var got = await client.GetTaskAsync(sent.Id);
        Assert.Equal(sent.Id, got.Id);
        Assert.Equal(TaskState.Completed, got.Status.State);
        Assert.Single(got.Artifacts!);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task ListTasks_happy_path(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var sent = (await client.SendMessageAsync("task")).RequireTask();
        var list = await client.ListTasksAsync(new ListTasksRequest { ContextId = sent.ContextId });
        Assert.Equal(sent.Id, Assert.Single(list.Tasks).Id);
        Assert.Equal(1, list.TotalSize);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task CancelTask_happy_path(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();
        Assert.Equal(TaskState.Canceled, (await client.CancelTaskAsync(task.Id)).Status.State);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task SubscribeToTask_happy_path(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync("input")).RequireTask();
        using var cts = new CancellationTokenSource(Wait);
        await using var e = client.SubscribeToTaskAsync(task.Id, null, cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await e.MoveNextAsync());
        Assert.Equal(task.Id, Assert.IsType<TaskEvent>(e.Current).Task.Id);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task CreateTaskPushNotificationConfig_happy_path(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync("input")).RequireTask();
        var created = await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = task.Id, Url = "http://hooks.test/a" });
        Assert.False(string.IsNullOrEmpty(created.Id));
        Assert.Equal(task.Id, created.TaskId);
        Assert.Equal("http://hooks.test/a", created.Url);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task GetTaskPushNotificationConfig_happy_path(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var (task, configId) = await SeedAsync(client);
        var got = await client.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = task.Id, Id = configId });
        Assert.Equal(configId, got.Id);
        Assert.Equal("http://hooks.test/seed", got.Url);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task ListTaskPushNotificationConfigs_happy_path(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var (task, configId) = await SeedAsync(client);
        var second = await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = task.Id, Url = "http://hooks.test/second" });
        var list = await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = task.Id });
        Assert.Equal(new[] { configId, second.Id! }.Order(), list.Configs.Select(c => c.Id!).Order());
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task DeleteTaskPushNotificationConfig_happy_path(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var (task, configId) = await SeedAsync(client);
        await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = task.Id, Id = configId });
        Assert.Empty((await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = task.Id })).Configs);
        var gone = await Assert.ThrowsAsync<A2AException>(() => client.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = task.Id, Id = configId }));
        Assert.Equal(A2AErrorCode.TaskNotFound, gone.Code);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task GetExtendedAgentCard_happy_path(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var card = await host.Client(binding).GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest());
        Assert.Equal("Extended card for alice", card.Description);
    }

    // ================================================================== metadata round-trip

    private static Dictionary<string, JsonElement> Meta(string value) =>
        new() { ["trace"] = JsonSerializer.SerializeToElement(value), ["n"] = JsonSerializer.SerializeToElement(7) };

    private static void AssertMeta(IReadOnlyDictionary<string, JsonElement>? seen, string value)
    {
        Assert.NotNull(seen);
        Assert.Equal(value, seen["trace"].GetString());
        Assert.Equal(7, seen["n"].GetInt32());
    }

    private static Task<TestAgentHost> StartRecordingAsync(MetadataRecorder recorder) => TestAgentHost.StartAsync(services: s =>
    {
        s.AddSingleton(recorder);
        s.AddScoped<IAgentExecutor, MetadataAgent>();
    });

    [Theory, MemberData(nameof(Bindings))]
    public async Task SendMessage_metadata(string binding)
    {
        var recorder = new MetadataRecorder();
        await using var host = await StartRecordingAsync(recorder);
        await host.Client(binding).SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("hello"), Metadata = Meta("send") });
        AssertMeta(await recorder.Execute, "send");
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task SendStreamingMessage_metadata(string binding)
    {
        var recorder = new MetadataRecorder();
        await using var host = await StartRecordingAsync(recorder);
        await foreach (var _ in host.Client(binding).SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("task"), Metadata = Meta("stream") })
            .WithCancellation(new CancellationTokenSource(Wait).Token)) { }
        AssertMeta(await recorder.Execute, "stream");
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task CancelTask_metadata(string binding)
    {
        var recorder = new MetadataRecorder();
        await using var host = await StartRecordingAsync(recorder);
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();
        await client.CancelTaskAsync(new CancelTaskRequest { Id = task.Id, Metadata = Meta("cancel") });
        AssertMeta(await recorder.Cancel, "cancel");
    }

    // ================================================================== tenant in route vs body

    /// <summary>The raw request for <paramref name="op"/>, with the tenant in the route and (when given) in the body.</summary>
    private static HttpRequestMessage Raw(string binding, Op op, string routeTenant, string? bodyTenant, string taskId, string configId)
    {
        string T(string json) => bodyTenant is null ? json : json.Length == 0 ? $"\"tenant\":\"{bodyTenant}\"" : $"\"tenant\":\"{bodyTenant}\",{json}";
        var message = "\"message\":{\"messageId\":\"m-raw\",\"role\":\"ROLE_USER\",\"parts\":[{\"text\":\"hello\"}]}";
        var push = $"\"taskId\":\"{taskId}\",\"url\":\"http://hooks.test/raw\"";
        var rpc = binding == ProtocolBindings.JsonRpc;
        HttpRequestMessage req;
        if (rpc)
        {
            var (method, args) = op switch
            {
                Op.SendMessage => (A2AProtocol.Methods.SendMessage, T(message)),
                Op.SendStreamingMessage => (A2AProtocol.Methods.SendStreamingMessage, T(message)),
                Op.GetTask => (A2AProtocol.Methods.GetTask, T($"\"id\":\"{taskId}\"")),
                Op.ListTasks => (A2AProtocol.Methods.ListTasks, T("\"pageSize\":10")),
                Op.CancelTask => (A2AProtocol.Methods.CancelTask, T($"\"id\":\"{taskId}\"")),
                Op.SubscribeToTask => (A2AProtocol.Methods.SubscribeToTask, T($"\"id\":\"{taskId}\"")),
                Op.CreatePush => (A2AProtocol.Methods.CreateTaskPushNotificationConfig, T(push)),
                Op.GetPush => (A2AProtocol.Methods.GetTaskPushNotificationConfig, T($"\"taskId\":\"{taskId}\",\"id\":\"{configId}\"")),
                Op.ListPush => (A2AProtocol.Methods.ListTaskPushNotificationConfigs, T($"\"taskId\":\"{taskId}\"")),
                Op.DeletePush => (A2AProtocol.Methods.DeleteTaskPushNotificationConfig, T($"\"taskId\":\"{taskId}\",\"id\":\"{configId}\"")),
                Op.GetExtendedAgentCard => (A2AProtocol.Methods.GetExtendedAgentCard, T("")),
                _ => throw new ArgumentOutOfRangeException(nameof(op)),
            };
            req = new HttpRequestMessage(HttpMethod.Post, $"/a2a/{routeTenant}")
            {
                Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + method + "\",\"params\":{" + args + "}}", Encoding.UTF8, "application/json"),
            };
        }
        else
        {
            var prefix = $"/a2a/{routeTenant}";
            (HttpMethod verb, string path, string? body) = op switch
            {
                Op.SendMessage => (HttpMethod.Post, "/message:send", "{" + T(message) + "}"),
                Op.SendStreamingMessage => (HttpMethod.Post, "/message:stream", "{" + T(message) + "}"),
                Op.GetTask => (HttpMethod.Get, $"/tasks/{taskId}", null),
                Op.ListTasks => (HttpMethod.Get, "/tasks", null),
                Op.CancelTask => (HttpMethod.Post, $"/tasks/{taskId}:cancel", "{" + T("\"metadata\":{}") + "}"),
                Op.SubscribeToTask => (HttpMethod.Get, $"/tasks/{taskId}:subscribe", null),
                Op.CreatePush => (HttpMethod.Post, $"/tasks/{taskId}/pushNotificationConfigs", "{" + T("\"url\":\"http://hooks.test/raw\"") + "}"),
                Op.GetPush => (HttpMethod.Get, $"/tasks/{taskId}/pushNotificationConfigs/{configId}", null),
                Op.ListPush => (HttpMethod.Get, $"/tasks/{taskId}/pushNotificationConfigs", null),
                Op.DeletePush => (HttpMethod.Delete, $"/tasks/{taskId}/pushNotificationConfigs/{configId}", null),
                Op.GetExtendedAgentCard => (HttpMethod.Get, "/extendedAgentCard", null),
                _ => throw new ArgumentOutOfRangeException(nameof(op)),
            };
            req = new HttpRequestMessage(verb, prefix + path);
            if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        return req;
    }

    private static async Task TenantAsync(Op op, string binding, string bodyTenant, bool expectOk)
    {
        await using var host = await TestAgentHost.StartAsync();
        // Everything the method needs lives in tenant "acme".
        var acme = host.Client(binding, options: new A2AClientOptions { Tenant = "acme" });
        var (task, configId) = await SeedAsync(acme);

        using var http = host.Http("alice");
        using var cts = new CancellationTokenSource(Wait);
        using var req = Raw(binding, op, "acme", bodyTenant, task.Id, configId);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var streaming = resp.Content.Headers.ContentType?.MediaType == "text/event-stream";

        if (expectOk)
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            if (!streaming)
            {
                var text = await resp.Content.ReadAsStringAsync(cts.Token);
                Assert.DoesNotContain("\"error\"", text);
            }
            else
            {
                Assert.True(op is Op.SendStreamingMessage or Op.SubscribeToTask);
                // The first frame is a result for the expected task, not an error.
                using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));
                string? line;
                do { line = await reader.ReadLineAsync(cts.Token); } while (line is not null && !line.StartsWith("data:", StringComparison.Ordinal));
                Assert.NotNull(line);
                using var frame = JsonDocument.Parse(line["data:".Length..]);
                var payload = frame.RootElement;
                Assert.False(payload.TryGetProperty("error", out _), line);
                if (binding == ProtocolBindings.JsonRpc) payload = payload.GetProperty("result");
                if (op is Op.SubscribeToTask) Assert.Equal(task.Id, payload.GetProperty("task").GetProperty("id").GetString());
                else Assert.Equal("hi there", payload.GetProperty("message").GetProperty("parts")[0].GetProperty("text").GetString()); // "hello" is a direct reply
            }
        }
        else
        {
            Assert.False(streaming);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(text);
            if (binding == ProtocolBindings.JsonRpc)
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.Equal((int)A2AErrorCode.InvalidParams, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }
            else
            {
                Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
                Assert.Equal(400, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }
        }
    }

    private static Task Match(Op op, string binding) => TenantAsync(op, binding, "acme", expectOk: true);

    private static Task Mismatch(Op op, string binding) => TenantAsync(op, binding, "other", expectOk: false);

    [Theory, MemberData(nameof(Bindings))] public Task SendMessage_tenant_match(string b) => Match(Op.SendMessage, b);
    [Theory, MemberData(nameof(Bindings))] public Task SendStreamingMessage_tenant_match(string b) => Match(Op.SendStreamingMessage, b);
    [Theory, MemberData(nameof(Bindings))] public Task GetTask_tenant_match(string b) => Match(Op.GetTask, b);
    [Theory, MemberData(nameof(Bindings))] public Task ListTasks_tenant_match(string b) => Match(Op.ListTasks, b);
    [Theory, MemberData(nameof(Bindings))] public Task CancelTask_tenant_match(string b) => Match(Op.CancelTask, b);
    [Theory, MemberData(nameof(Bindings))] public Task SubscribeToTask_tenant_match(string b) => Match(Op.SubscribeToTask, b);
    [Theory, MemberData(nameof(Bindings))] public Task CreateTaskPushNotificationConfig_tenant_match(string b) => Match(Op.CreatePush, b);
    [Theory, MemberData(nameof(Bindings))] public Task GetTaskPushNotificationConfig_tenant_match(string b) => Match(Op.GetPush, b);
    [Theory, MemberData(nameof(Bindings))] public Task ListTaskPushNotificationConfigs_tenant_match(string b) => Match(Op.ListPush, b);
    [Theory, MemberData(nameof(Bindings))] public Task DeleteTaskPushNotificationConfig_tenant_match(string b) => Match(Op.DeletePush, b);
    [Theory, MemberData(nameof(Bindings))] public Task GetExtendedAgentCard_tenant_match(string b) => Match(Op.GetExtendedAgentCard, b);

    [Theory, MemberData(nameof(Bindings))] public Task SendMessage_tenant_mismatch(string b) => Mismatch(Op.SendMessage, b);
    [Theory, MemberData(nameof(Bindings))] public Task SendStreamingMessage_tenant_mismatch(string b) => Mismatch(Op.SendStreamingMessage, b);
    [Theory, MemberData(nameof(JsonRpcOnly))] public Task GetTask_tenant_mismatch(string b) => Mismatch(Op.GetTask, b);
    [Theory, MemberData(nameof(JsonRpcOnly))] public Task ListTasks_tenant_mismatch(string b) => Mismatch(Op.ListTasks, b);
    [Theory, MemberData(nameof(Bindings))] public Task CancelTask_tenant_mismatch(string b) => Mismatch(Op.CancelTask, b);
    [Theory, MemberData(nameof(JsonRpcOnly))] public Task SubscribeToTask_tenant_mismatch(string b) => Mismatch(Op.SubscribeToTask, b);
    [Theory, MemberData(nameof(Bindings))] public Task CreateTaskPushNotificationConfig_tenant_mismatch(string b) => Mismatch(Op.CreatePush, b);
    [Theory, MemberData(nameof(JsonRpcOnly))] public Task GetTaskPushNotificationConfig_tenant_mismatch(string b) => Mismatch(Op.GetPush, b);
    [Theory, MemberData(nameof(JsonRpcOnly))] public Task ListTaskPushNotificationConfigs_tenant_mismatch(string b) => Mismatch(Op.ListPush, b);
    [Theory, MemberData(nameof(JsonRpcOnly))] public Task DeleteTaskPushNotificationConfig_tenant_mismatch(string b) => Mismatch(Op.DeletePush, b);
    [Theory, MemberData(nameof(JsonRpcOnly))] public Task GetExtendedAgentCard_tenant_mismatch(string b) => Mismatch(Op.GetExtendedAgentCard, b);

    // ================================================================== historyLength

    [Theory, MemberData(nameof(Bindings))]
    public async Task SendMessage_historyLength(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        async Task<AgentTask> Send(int? length) => (await client.SendMessageAsync(Message.FromUser("task"), new SendMessageConfiguration { HistoryLength = length })).RequireTask();
        Assert.Null((await Send(0)).History);
        Assert.Single((await Send(1)).History!);
        Assert.Equal(3, (await Send(null)).History!.Count); // user, working, done
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task GetTask_historyLength(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync("task")).RequireTask();
        Assert.Null((await client.GetTaskAsync(task.Id, 0)).History);
        Assert.Single((await client.GetTaskAsync(task.Id, 1)).History!);
        Assert.Equal(3, (await client.GetTaskAsync(task.Id)).History!.Count);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task ListTasks_historyLength(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        await client.SendMessageAsync("task");
        Assert.Null(Assert.Single((await client.ListTasksAsync(new ListTasksRequest { HistoryLength = 0 })).Tasks).History);
        Assert.Single(Assert.Single((await client.ListTasksAsync(new ListTasksRequest { HistoryLength = 1 })).Tasks).History!);
        Assert.Equal(3, Assert.Single((await client.ListTasksAsync(new ListTasksRequest())).Tasks).History!.Count);
    }

    // ================================================================== foreign owner

    private static async Task ForeignAsync(Op op, string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var (task, configId) = await SeedAsync(host.Client(binding, "alice"));
        var bob = host.Client(binding, "bob");
        var ex = await Assert.ThrowsAsync<A2AException>(() => InvokeAsync(bob, op, task.Id, configId));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
        // alice still has it: the answer for bob was the same as for a missing task, not a denial of something that exists
        var missing = await Assert.ThrowsAsync<A2AException>(() => InvokeAsync(bob, op, "no-such-task", configId));
        Assert.Equal(ex.Code, missing.Code);
        Assert.Equal(ex.Message, missing.Message.Replace("no-such-task", task.Id));
    }

    [Theory, MemberData(nameof(Bindings))] public Task SendMessage_foreign_owner(string b) => ForeignAsync(Op.SendMessage, b);
    [Theory, MemberData(nameof(Bindings))] public Task SendStreamingMessage_foreign_owner(string b) => ForeignAsync(Op.SendStreamingMessage, b);
    [Theory, MemberData(nameof(Bindings))] public Task GetTask_foreign_owner(string b) => ForeignAsync(Op.GetTask, b);
    [Theory, MemberData(nameof(Bindings))] public Task CancelTask_foreign_owner(string b) => ForeignAsync(Op.CancelTask, b);
    [Theory, MemberData(nameof(Bindings))] public Task SubscribeToTask_foreign_owner(string b) => ForeignAsync(Op.SubscribeToTask, b);
    [Theory, MemberData(nameof(Bindings))] public Task CreateTaskPushNotificationConfig_foreign_owner(string b) => ForeignAsync(Op.CreatePush, b);
    [Theory, MemberData(nameof(Bindings))] public Task GetTaskPushNotificationConfig_foreign_owner(string b) => ForeignAsync(Op.GetPush, b);
    [Theory, MemberData(nameof(Bindings))] public Task ListTaskPushNotificationConfigs_foreign_owner(string b) => ForeignAsync(Op.ListPush, b);
    [Theory, MemberData(nameof(Bindings))] public Task DeleteTaskPushNotificationConfig_foreign_owner(string b) => ForeignAsync(Op.DeletePush, b);

    [Theory, MemberData(nameof(Bindings))]
    public async Task ListTasks_foreign_owner(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var task = (await host.Client(binding, "alice").SendMessageAsync("task")).RequireTask();
        var bob = await host.Client(binding, "bob").ListTasksAsync(new ListTasksRequest { ContextId = task.ContextId });
        Assert.Empty(bob.Tasks);
        Assert.Equal(0, bob.TotalSize);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task GetExtendedAgentCard_foreign_owner(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var bob = await host.Client(binding, "bob").GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest());
        Assert.Equal("Extended card for bob", bob.Description); // never alice's card
    }

    // ================================================================== handler denial

    private static async Task DeniedAsync(Op op, string binding, A2AErrorCode expected)
    {
        var auth = new Switchable();
        await using var host = await TestAgentHost.StartAsync(services: s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler>(auth)));
        var client = host.Client(binding);
        var (task, configId) = await SeedAsync(client);

        auth.Allow = false;
        var ex = await Assert.ThrowsAsync<A2AException>(() => InvokeAsync(client, op, op is Op.ListTasks or Op.GetExtendedAgentCard ? null : task.Id, configId));
        Assert.Equal(expected, ex.Code);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task SendMessage_handler_denial(string binding)
    {
        // A follow-up names a task: denied like a missing one. A new conversation has no task to hide: permission denied.
        await DeniedAsync(Op.SendMessage, binding, A2AErrorCode.TaskNotFound);
        var auth = new Switchable { Allow = false };
        await using var host = await TestAgentHost.StartAsync(services: s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler>(auth)));
        var ex = await Assert.ThrowsAsync<A2AException>(() => InvokeAsync(host.Client(binding), Op.SendMessage, null));
        Assert.Equal(A2AErrorCode.PermissionDenied, ex.Code);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task SendStreamingMessage_handler_denial(string binding)
    {
        await DeniedAsync(Op.SendStreamingMessage, binding, A2AErrorCode.TaskNotFound);
        var auth = new Switchable { Allow = false };
        await using var host = await TestAgentHost.StartAsync(services: s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler>(auth)));
        var ex = await Assert.ThrowsAsync<A2AException>(() => InvokeAsync(host.Client(binding), Op.SendStreamingMessage, null));
        Assert.Equal(A2AErrorCode.PermissionDenied, ex.Code);
    }

    [Theory, MemberData(nameof(Bindings))] public Task GetTask_handler_denial(string b) => DeniedAsync(Op.GetTask, b, A2AErrorCode.TaskNotFound);
    [Theory, MemberData(nameof(Bindings))] public Task ListTasks_handler_denial(string b) => DeniedAsync(Op.ListTasks, b, A2AErrorCode.PermissionDenied);
    [Theory, MemberData(nameof(Bindings))] public Task CancelTask_handler_denial(string b) => DeniedAsync(Op.CancelTask, b, A2AErrorCode.TaskNotFound);
    [Theory, MemberData(nameof(Bindings))] public Task SubscribeToTask_handler_denial(string b) => DeniedAsync(Op.SubscribeToTask, b, A2AErrorCode.TaskNotFound);
    [Theory, MemberData(nameof(Bindings))] public Task CreateTaskPushNotificationConfig_handler_denial(string b) => DeniedAsync(Op.CreatePush, b, A2AErrorCode.TaskNotFound);
    [Theory, MemberData(nameof(Bindings))] public Task GetTaskPushNotificationConfig_handler_denial(string b) => DeniedAsync(Op.GetPush, b, A2AErrorCode.TaskNotFound);
    [Theory, MemberData(nameof(Bindings))] public Task ListTaskPushNotificationConfigs_handler_denial(string b) => DeniedAsync(Op.ListPush, b, A2AErrorCode.TaskNotFound);
    [Theory, MemberData(nameof(Bindings))] public Task DeleteTaskPushNotificationConfig_handler_denial(string b) => DeniedAsync(Op.DeletePush, b, A2AErrorCode.TaskNotFound);
    [Theory, MemberData(nameof(Bindings))] public Task GetExtendedAgentCard_handler_denial(string b) => DeniedAsync(Op.GetExtendedAgentCard, b, A2AErrorCode.PermissionDenied);
}
