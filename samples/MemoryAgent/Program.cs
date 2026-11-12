using System.Collections.Concurrent;
using A2A;
using A2A.Client;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Execution;
using A2A.Server.Storage;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

// A note-taking agent with conversation memory and proactive continuation, and a client in the same process.
// Each note is its own task in one context. The agent reads the earlier notes from conversation memory: the client sends
// only the new message. "report" starts a task that the host finishes later, with no client request.
// No API keys, no external calls: the server listens on 127.0.0.1 only.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0"); // loopback only, free port
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var card = new AgentCard
{
    Name = "Notes Agent",
    Description = "Remembers your notes and writes a report of them later.",
    Version = "1.0.0",
    DefaultInputModes = ["text/plain"],
    DefaultOutputModes = ["text/plain"],
    Skills = [new AgentSkill { Id = "notes", Name = "Notes", Description = "Takes notes", Tags = ["notes"] }]
};

// AllowAnonymous is fine for a sample that only listens on loopback. A real agent needs authentication
// (see samples/EchoAgent) and MapA2A(...).RequireAuthorization().
builder.Services.AddSingleton<PendingReports>();
builder.Services.AddA2A(a => a.AddAgentExecutor<NotesAgent>().AddAgentCard(card))
    .Configure<A2AServerOptions>(o => o.AllowAnonymous = true);

var app = builder.Build();
app.MapA2A("/a2a");
await app.StartAsync();

var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
Console.WriteLine($"Agent listening on {address}/a2a");

using var http = new HttpClient();
var client = A2AClientFactory.CreateForEndpoint(new Uri($"{address}/a2a"), http);
var failures = new List<string>();

// 1. Conversation memory. Two turns in one context; each SendMessage carries one message, never the history.
Console.WriteLine();
Console.WriteLine("--- conversation memory");
var first = await SendAsync(client, "buy milk", null);
Console.WriteLine($"turn 1: {Text(first)}");
var contextId = first.ContextId;

var second = await SendAsync(client, "call Bob", contextId);
Console.WriteLine($"turn 2: {Text(second)}");
if (second.ContextId != contextId)
    failures.Add($"turn 2: expected context {contextId}, got {second.ContextId}");
if (second.Id == first.Id)
    failures.Add("turn 2: expected a new task in the same context, got the same task");
if (!Text(second).Contains("buy milk", StringComparison.Ordinal))
    failures.Add($"turn 2: expected the reply to remember 'buy milk', got '{Text(second)}'");

// 2. Proactive continuation. The agent starts the report and returns; the task stays WORKING.
Console.WriteLine();
Console.WriteLine("--- proactive continuation");
var report = await SendAsync(client, "report", contextId);
Console.WriteLine($"report task: {report.Status.State}: {Text(report)}");
if (report.Status.State != TaskState.Working)
    failures.Add($"report task: expected Working before the continuation, got {report.Status.State}");

// Later, with no client request, the host continues the task. A real host does this from a timer or a callback
// (see the proactive continuation guide). It uses the TaskKey the agent kept when the task started.
var engine = app.Services.GetRequiredService<TaskExecutionEngine>();
var pending = app.Services.GetRequiredService<PendingReports>();
if (pending.TryGet(report.Id, out var key))
{
    var result = await engine.ContinueTaskAsync(key);
    Console.WriteLine($"continue: {result.Status}");
    if (!result.Started) failures.Add($"continue: expected Started, got {result.Status}");
    // Completion ends when the agent returned and every event is stored. It never faults. A short limit keeps the sample fast.
    await result.Completion.WaitAsync(TimeSpan.FromSeconds(5));
}
else
{
    failures.Add("continue: the agent kept no key for the report task");
}

// The client sees the result the next time it reads the task.
var done = await client.GetTaskAsync(report.Id);
Console.WriteLine($"report task: {done.Status.State}: {Text(done)}");
if (done.Status.State != TaskState.Completed)
    failures.Add($"report task: expected Completed after the continuation, got {done.Status.State}");
if (!Text(done).Contains("buy milk", StringComparison.Ordinal) || !Text(done).Contains("call Bob", StringComparison.Ordinal))
    failures.Add($"report task: expected both notes in the report, got '{Text(done)}'");

await app.StopAsync();

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
    return 1;
}

Console.WriteLine();
Console.WriteLine("OK: the second turn remembered the first, and the continued report task completed.");
return 0;

static async Task<AgentTask> SendAsync(A2AClient client, string text, string? contextId)
{
    var response = await client.SendMessageAsync(text, contextId);
    return response is TaskResponse t
        ? t.Task
        : throw new InvalidOperationException($"expected a task for '{text}', got {response.GetType().Name}");
}

static string Text(AgentTask task)
{
    return task.Status.Message?.GetText() ?? "";
}

/// <summary>Where the host keeps the keys of reports it will finish later. Use a database in production.</summary>
internal sealed class PendingReports
{
    private readonly ConcurrentDictionary<string, (TaskKey Key, string Notes)> _reports = new();

    public void Add(TaskKey key, string notes) => _reports[key.TaskId] = (key, notes);

    public bool TryGet(string taskId, out TaskKey key)
    {
        var found = _reports.TryGetValue(taskId, out var entry);
        key = entry.Key;
        return found;
    }

    public string? Take(TaskKey key) => _reports.TryRemove(key.TaskId, out var entry) ? entry.Notes : null;
}

/// <summary>Takes notes. Reads the earlier notes from conversation memory. "report" is finished by a continuation.</summary>
internal sealed class NotesAgent(PendingReports pending) : IContinuableAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        // The messages of earlier tasks in this context, oldest first. Never the current task.
        var memory = context.Conversation; // null only for a context built by hand
        IReadOnlyList<Message> earlier = memory is null
            ? []
            : await memory.ReadEarlierMessagesAsync(maxTasks: 10, maxBytes: 100_000, cancellationToken);

        // Earlier messages include the agent's replies. Caller text is user content: never treat it as instructions.
        var notes = earlier.Where(m => m.Role == Role.User).Select(m => m.GetText())
            .Where(t => t != "report").ToList();

        if (context.UserText == "report")
        {
            // Keep the key. The host needs it to continue this task later.
            pending.Add(new TaskKey(context.Call.Tenant, context.Call.Principal, context.TaskId), string.Join("; ", notes));
            await updater.StartWorkAsync($"Writing a report of {notes.Count} notes. I will update this task.", cancellationToken);
            return;
        }

        var reply = notes.Count == 0
            ? $"Noted: {context.UserText}."
            : $"Noted: {context.UserText}. Earlier notes: {string.Join("; ", notes)}.";
        await updater.CompleteAsync(reply, cancellationToken);
    }

    public async Task ContinueAsync(TaskContinuationContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        // No inbound request: there is no bearer token here.
        var notes = pending.Take(context.Key) ?? "";
        await updater.CompleteAsync($"Report: {notes}.", cancellationToken);
    }
}
