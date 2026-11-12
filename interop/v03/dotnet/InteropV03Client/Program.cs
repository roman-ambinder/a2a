// Drives any A2A v0.3 agent that implements the interop scenarios, using the .NET v0.3 compat client.
// Usage: dotnet run -- <agentBaseUrl> <JSONRPC|HTTP+JSON|GRPC>
// Exit code: number of failed checks. No ListTasks: it is not in v0.3 JSON-RPC or gRPC, and the 0.3 Python SDK does not
// implement the REST route.
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using A2A;
using A2A.Client;
using A2A.Client.Grpc.V0_3Compat;
using A2A.Client.V0_3Compat;
using Grpc.Net.Client;

var agentUrl = new Uri(args.Length > 0 ? args[0] : "http://127.0.0.1:5400/");
var binding = args.Length > 1 ? args[1] : ProtocolBindings.JsonRpc;
var failures = 0;

async Task Check(string name, Func<Task> body)
{
    try
    {
        await body().WaitAsync(TimeSpan.FromSeconds(20));
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
    }
}

void Expect(bool condition, string what)
{
    if (!condition) throw new Exception("expected " + what);
}

// Records what goes on the wire, so the run can prove it really spoke v0.3.
var wire = new List<string>();
using var http = new HttpClient(new RecordingHandler(wire) { InnerHandler = new HttpClientHandler() });
var clientOptions = new A2AClientOptions { SupportedBindings = [binding] };
// v0.3 gRPC: the same recorder sees the gRPC calls (their paths are /a2a.v1.A2AService/<method>).
if (binding == ProtocolBindings.Grpc)
    clientOptions.UseGrpcV03(new GrpcChannelOptions { HttpHandler = new RecordingHandler(wire) { InnerHandler = new SocketsHttpHandler() } });
var client = await V03CompatClientFactory.CreateAsync(agentUrl, http, clientOptions);
Console.WriteLine($"# .NET v0.3 compat client -> {client.AgentCard!.Name} via {binding}");

await Check("direct message reply", async () =>
{
    var r = await client.SendMessageAsync("hello");
    Expect(r is MessageResponse, "a message, no task");
    var message = ((MessageResponse)r).Message;
    Expect(message.GetText() == "hi there", "text 'hi there'");
    Expect(message.Role == Role.Agent, "agent role");
});

AgentTask? completed = null;
await Check("blocking send -> completed task with appended artifact", async () =>
{
    completed = (await client.SendMessageAsync("task") as TaskResponse)?.Task;
    Expect(completed is not null, "a task");
    Expect(completed!.Status.State == TaskState.Completed, $"COMPLETED, got {completed.Status.State}");
    var art = completed.Artifacts?.SingleOrDefault();
    Expect(art is not null, "one artifact");
    var texts = string.Join(",", art!.Parts.Select(p => (p as TextPart)?.Text));
    Expect(texts == "part1,part2", $"parts part1,part2, got {texts}");
});

await Check("streaming send -> task first, ordered events, terminal last", async () =>
{
    var events = new List<StreamResponse>();
    await foreach (var e in client.SendStreamingMessageAsync(Message.FromUser("task"))) events.Add(e);
    Expect(events.Count >= 4, $">= 4 events, got {events.Count}");
    Expect(events[0] is TaskEvent, "first event to be a Task");
    Expect(events.Any(e => e is ArtifactUpdateEvent { ArtifactUpdate.Append: true }), "an append artifact chunk");
    var state = events.Last(e => e is StatusUpdateEvent or TaskEvent) switch
    {
        StatusUpdateEvent s => s.StatusUpdate.Status.State,
        TaskEvent t => t.Task.Status.State,
        _ => TaskState.Unspecified,
    };
    Expect(state == TaskState.Completed, $"last state COMPLETED, got {state}");
});

await Check("streaming direct message", async () =>
{
    var events = new List<StreamResponse>();
    await foreach (var e in client.SendStreamingMessageAsync(Message.FromUser("hello"))) events.Add(e);
    Expect(events.Count == 1 && (events[0] as MessageEvent)?.Message.GetText() == "hi there", "exactly one message event");
});

await Check("multi-turn input-required", async () =>
{
    var first = ((TaskResponse)await client.SendMessageAsync("input")).Task;
    Expect(first.Status.State == TaskState.InputRequired, $"INPUT_REQUIRED, got {first.Status.State}");
    var second = ((TaskResponse)await client.SendMessageAsync("more", first.ContextId, first.Id)).Task;
    Expect(second.Id == first.Id, "same task id");
    Expect(second.Status.State == TaskState.Completed, $"COMPLETED, got {second.Status.State}");
    Expect(second.Status.Message?.GetText() == "got: more", $"'got: more', got '{second.Status.Message?.GetText()}'");
});

await Check("get task with historyLength", async () =>
{
    Expect(completed is not null, "a task from an earlier check");
    var full = await client.GetTaskAsync(completed!.Id);
    Expect((full.History?.Count ?? 0) > 1, $"more than 1 history entry without a limit, got {full.History?.Count}");
    var t = await client.GetTaskAsync(completed.Id, historyLength: 1);
    Expect(t.Id == completed.Id, "same id");
    Expect(t.History?.Count == 1, $"exactly 1 history entry, got {t.History?.Count}");
});

await Check("get unknown task -> TaskNotFound", async () =>
{
    try
    {
        await client.GetTaskAsync("does-not-exist-" + Guid.NewGuid());
        throw new Exception("expected an error");
    }
    catch (A2AException ex)
    {
        Expect(ex.Code == A2AErrorCode.TaskNotFound, $"TaskNotFound, got {ex.Code}");
    }
});

await Check("return immediately, resubscribe, cancel", async () =>
{
    var task = ((TaskResponse)await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).Task;
    Expect(!task.Status.State.IsTerminal(), $"non-terminal, got {task.Status.State}");

    var states = new List<TaskState>();
    var sub = Task.Run(async () =>
    {
        await foreach (var e in client.SubscribeToTaskAsync(task.Id))
        {
            if (e is StatusUpdateEvent su) states.Add(su.StatusUpdate.Status.State);
            if (e is TaskEvent { Task: var t }) states.Add(t.Status.State);
        }
    });
    // v0.3 does not promise a snapshot first (the 0.3 Python server sends none), so give the subscription time to attach.
    await Task.Delay(1000);
    Expect(!sub.IsCompleted, "the subscription to stay open while the task runs");

    var canceled = await client.CancelTaskAsync(task.Id);
    Expect(canceled.Status.State == TaskState.Canceled, $"CANCELED, got {canceled.Status.State}");
    await sub.WaitAsync(TimeSpan.FromSeconds(10));
    Expect(states.Contains(TaskState.Canceled), $"subscription to see CANCELED, saw [{string.Join(",", states)}]");
});

await Check("extended card", async () =>
{
    Expect(client.AgentCard!.Capabilities.ExtendedAgentCard == true, "the card to offer an extended card");
    var card = await client.RefreshWithExtendedAgentCardAsync();
    Expect(card.Name.EndsWith("(extended)", StringComparison.Ordinal), $"the extended card, got '{card.Name}'");
});

await Check("push notification: config, then a v0.3 Task body at the webhook", async () =>
{
    Expect(client.AgentCard!.Capabilities.PushNotifications == true, "the agent to offer push notifications");
    using var listener = new HttpListener();
    var port = FreePort();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    var first = ((TaskResponse)await client.SendMessageAsync("input")).Task;
    var created = await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig
        { TaskId = first.Id, Url = $"http://127.0.0.1:{port}/hook", Token = "tok-net" });
    Expect(created.Url.EndsWith("/hook", StringComparison.Ordinal), "the webhook url back");
    // The 0.3 Python agent implements push config get on every binding, list and delete only on JSON-RPC.
    if (created.Id is { Length: > 0 } createdId)
    {
        var got = await client.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = first.Id, Id = createdId });
        Expect(got.Url == created.Url, "the same config back");
    }

    if (binding == ProtocolBindings.JsonRpc)
    {
        var configs = await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = first.Id });
        Expect(configs.Configs.Count >= 1, $"the config listed, got {configs.Configs.Count}");
    }
    await client.SendMessageAsync("more", first.ContextId, first.Id);

    JsonNode? body;
    string? token;
    do
    {
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using (var reader = new StreamReader(context.Request.InputStream)) body = JsonNode.Parse(await reader.ReadToEndAsync());
        token = context.Request.Headers["X-A2A-Notification-Token"];
        context.Response.StatusCode = 200;
        context.Response.Close();
    } while ((string?)body?["status"]?["state"] != "completed");
    // The v0.3 body: the task as a v0.3 JSON-RPC Task, what the 0.3 Python server sends.
    Expect((string?)body!["kind"] == "task" && (string?)body["id"] == first.Id, $"a v0.3 Task body, got {body.ToJsonString()[..Math.Min(200, body.ToJsonString().Length)]}");
    Expect(token == "tok-net", $"the token header, got '{token}'");
    if (binding == ProtocolBindings.JsonRpc && created.Id is { Length: > 0 } id)
        await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = first.Id, Id = id });
});

static int FreePort()
{
    using var socket = new TcpListener(IPAddress.Loopback, 0);
    socket.Start();
    return ((IPEndPoint)socket.LocalEndpoint).Port;
}

// The proof that this run was v0.3: the card read as v0.3, and every call used a v0.3 method or route.
Check2();
void Check2()
{
    var itf = client.AgentCard!.SupportedInterfaces.Single(i => i.ProtocolBinding == binding);
    var v03Card = itf.ProtocolVersion.StartsWith("0.3", StringComparison.Ordinal);
    string[] rpcMethods = ["message/send", "message/stream", "tasks/get", "tasks/cancel", "tasks/resubscribe", "agent/getAuthenticatedExtendedCard"];
    var calls = wire.Where(w => !w.Contains("agent-card.json", StringComparison.Ordinal)).ToList();
    string[] pushMethods = ["tasks/pushNotificationConfig/set", "tasks/pushNotificationConfig/get", "tasks/pushNotificationConfig/list", "tasks/pushNotificationConfig/delete"];
    var allV03 = calls.Count > 0 && calls.All(w => binding switch
    {
        ProtocolBindings.JsonRpc => rpcMethods.Contains(w.Split(' ')[0]) || pushMethods.Contains(w.Split(' ')[0]),
        // The v0.3 gRPC service; the v1.0 one is /lf.a2a.v1.A2AService/.
        ProtocolBindings.Grpc => w.Split(' ')[1].StartsWith("/a2a.v1.A2AService/", StringComparison.Ordinal),
        _ => w.Split(' ')[1].StartsWith("/v1/", StringComparison.Ordinal),
    });
    var ok = v03Card && allV03;
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  used v0.3 on the wire (card {itf.ProtocolVersion}, {calls.Count} calls){(ok ? "" : ": " + string.Join(" | ", calls.Take(5)))}");
}

Console.WriteLine(failures == 0 ? "# all checks passed" : $"# {failures} check(s) failed");
return failures;

// Records "<rpc method or HTTP verb> <path>" for each request.
sealed class RecordingHandler(List<string> wire) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string label;
        if (request.Method == HttpMethod.Post && request.Content is not null && request.RequestUri!.AbsolutePath is "/" or "")
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            label = $"{JsonNode.Parse(body)?["method"]} {request.RequestUri!.AbsolutePath}";
        }
        else label = $"{request.Method} {request.RequestUri!.AbsolutePath}";
        lock (wire) wire.Add(label);
        return await base.SendAsync(request, cancellationToken);
    }
}
