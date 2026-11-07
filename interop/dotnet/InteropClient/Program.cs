// Drives any A2A v1.0 agent that implements the interop scenarios, using the .NET client.
// Usage: dotnet run -- <agentBaseUrl> <JSONRPC|HTTP+JSON|GRPC>
// Exit code: number of failed checks.
using A2A;
using A2A.Client;
using A2A.Client.Grpc;

var agentUrl = new Uri(args.Length > 0 ? args[0] : "http://127.0.0.1:5200/");
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

using var http = new HttpClient();
var client = binding == ProtocolBindings.Grpc
    ? A2AGrpcClientFactory.Create(await new A2ACardResolver(http).GetAgentCardAsync(agentUrl))
    : await A2AClientFactory.CreateAsync(agentUrl, http, new A2AClientOptions { SupportedBindings = [binding] });
Console.WriteLine($"# .NET client -> {client.AgentCard!.Name} via {binding}");

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
    var t = await client.GetTaskAsync(completed!.Id, historyLength: 1);
    Expect(t.Id == completed.Id, "same id");
    Expect((t.History?.Count ?? 0) <= 1, $"<= 1 history entries, got {t.History?.Count}");
});

await Check("list tasks", async () =>
{
    var page = await client.ListTasksAsync(new ListTasksRequest { PageSize = 50 });
    Expect(page.Tasks.Count > 0, "some tasks");
    Expect(completed is null || page.Tasks.Any(t => t.Id == completed.Id), "the completed task in the list");
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

await Check("return immediately, subscribe, cancel", async () =>
{
    var task = ((TaskResponse)await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).Task;
    Expect(!task.Status.State.IsTerminal(), $"non-terminal, got {task.Status.State}");

    var first = new TaskCompletionSource<StreamResponse>();
    var states = new List<TaskState>();
    var sub = Task.Run(async () =>
    {
        await foreach (var e in client.SubscribeToTaskAsync(task.Id))
        {
            first.TrySetResult(e);
            if (e is StatusUpdateEvent su) states.Add(su.StatusUpdate.Status.State);
            if (e is TaskEvent { Task: var t } && t.Status.State.IsTerminal()) states.Add(t.Status.State);
        }
    });
    var firstEvent = await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Expect((firstEvent as TaskEvent)?.Task.Id == task.Id, "subscription to start with the task snapshot");

    var canceled = await client.CancelTaskAsync(task.Id);
    Expect(canceled.Status.State == TaskState.Canceled, $"CANCELED, got {canceled.Status.State}");
    await sub.WaitAsync(TimeSpan.FromSeconds(10));
    Expect(states.Contains(TaskState.Canceled), $"subscription to see CANCELED, saw [{string.Join(",", states)}]");
});

Console.WriteLine(failures == 0 ? "# all checks passed" : $"# {failures} check(s) failed");
return failures;
