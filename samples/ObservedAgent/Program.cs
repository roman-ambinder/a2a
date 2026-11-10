using System.Diagnostics;
using A2A;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Diagnostics;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// An agent with OpenTelemetry tracing and metrics, and a token budget per task. A client calls it twice: once within
// the budget, once past it. The sample reads its own spans and counters in-process to check what happened.
// No collector, no API keys, no external calls: the server listens on 127.0.0.1 only.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0"); // loopback only, free port
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// The SDK traces with ActivitySource and counts with Meter. OpenTelemetry listens to them by name.
// A real host exports with .AddOtlpExporter() to a collector. This sample keeps spans and metrics in memory to check them.
// Client and server run in one process here, so one setup listens to both sources.
var spans = new List<Activity>();
var metrics = new List<Metric>();
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource(A2ATelemetry.ServerSourceName, A2ATelemetry.ClientSourceName)
        .AddAspNetCoreInstrumentation()
        .AddInMemoryExporter(spans))
    .WithMetrics(m => m
        .AddMeter(A2ATelemetry.ServerMeterName, "A2A")
        .AddInMemoryExporter(metrics));

var card = new AgentCard
{
    Name = "Observed Agent",
    Description = "Answers with a pretend model. Each answer costs tokens.",
    Version = "1.0.0",
    DefaultInputModes = ["text/plain"],
    DefaultOutputModes = ["text/plain"],
    Skills = [new AgentSkill { Id = "answer", Name = "Answer", Description = "Answers a question", Tags = ["demo"] }]
};

// The budget per task: past it, the task fails. A real agent sets limits that fit its model and prices.
// AllowAnonymous is fine for a sample that only listens on loopback. A real agent needs authentication
// (see samples/EchoAgent) and MapA2A(...).RequireAuthorization().
builder.Services.AddA2A(a => a.AddAgentExecutor<ModelAgent>().AddAgentCard(card))
    .Configure<A2AServerOptions>(o =>
    {
        o.Limits.MaxTaskTokens = 2_000;
        o.Limits.MaxTaskCostUnits = 0.50m;
        o.AllowAnonymous = true;
    });

var app = builder.Build();
app.UseA2A();
app.MapA2A("/a2a");
await app.StartAsync();

var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
Console.WriteLine($"Agent listening on {address}/a2a (token budget per task: 2000)");

var options = new A2AClientOptions();
using var http = new HttpClient();
var client = new A2AClient(new JsonRpcTransport(http, new Uri($"{address}/a2a"), options), options, card);
var failures = new List<string>();

// 1. Within the budget: the pretend model uses 400 tokens per word, so two words cost 800 tokens.
Console.WriteLine();
Console.WriteLine("--- within the budget");
var small = await client.SendMessageAsync("Say hello");
var smallTask = (small as TaskResponse)?.Task;
Console.WriteLine($"task {smallTask?.Status.State}: {smallTask?.Status.Message?.GetText()}");
if (smallTask?.Status.State != TaskState.Completed)
    failures.Add($"small task: expected Completed, got {smallTask?.Status.State}");

// 2. Past the budget: six words cost 2400 tokens. The guide says the task moves to FAILED with "Task exceeded its budget".
Console.WriteLine();
Console.WriteLine("--- past the budget");
var big = await client.SendMessageAsync("Write a long essay about tracing");
var bigTask = (big as TaskResponse)?.Task;
Console.WriteLine($"task {bigTask?.Status.State}: {bigTask?.Status.Message?.GetText()}");
if (bigTask?.Status.State != TaskState.Failed)
    failures.Add($"big task: expected Failed, got {bigTask?.Status.State}");
if (bigTask?.Status.Message?.GetText() != "Task exceeded its budget")
    failures.Add($"big task: expected the message 'Task exceeded its budget', got '{bigTask?.Status.Message?.GetText()}'");

// A span is exported when it ends, and the server may end its spans just after it answers. Stopping the host lets
// every request and run finish. Metrics are exported when the reader collects: flush both before reading.
await app.StopAsync();
app.Services.GetRequiredService<TracerProvider>().ForceFlush();
app.Services.GetRequiredService<MeterProvider>().ForceFlush();

// 3. The trace of the call past the budget: the client span, ASP.NET Core's request span, the SDK's operation span and
//    the agent run. The client sent traceparent, so all of them share one trace.
Console.WriteLine();
Console.WriteLine("--- trace of the call past the budget");
var clientSpan = spans.LastOrDefault(s =>
    s.Source.Name == A2ATelemetry.ClientSourceName && s.OperationName == "SendMessage");
var trace = clientSpan is null
    ? []
    : spans.Where(s => s.TraceId == clientSpan.TraceId).OrderBy(s => s.StartTimeUtc).ToList();
foreach (var span in trace)
{
    var depth = 0;
    for (var parent = span.ParentSpanId; trace.FirstOrDefault(p => p.SpanId == parent) is { } p; parent = p.ParentSpanId)
        depth++;
    var state = span.GetTagItem(A2ATelemetry.Tags.TaskState);
    var error = span.GetTagItem(A2ATelemetry.Tags.ErrorType);
    Console.WriteLine($"{new string(' ', depth * 2)}{span.DisplayName} [{span.Source.Name}, {span.Kind}]" +
                      (state is null ? "" : $" {A2ATelemetry.Tags.TaskState}={state}") +
                      (error is null ? "" : $" {A2ATelemetry.Tags.ErrorType}={error}"));
}

var serverSpan = trace.FirstOrDefault(s =>
    s.Source.Name == A2ATelemetry.ServerSourceName && s.OperationName == "SendMessage");
var runSpan = trace.FirstOrDefault(s => s.OperationName == A2ATelemetry.SpanNames.AgentExecution);
if (clientSpan is null) failures.Add("no A2A.Client SendMessage span was recorded");
if (serverSpan is null) failures.Add("no A2A.Server SendMessage span was recorded in the client's trace");
if (runSpan is null) failures.Add($"no {A2ATelemetry.SpanNames.AgentExecution} span was recorded in the client's trace");
else if (runSpan.GetTagItem(A2ATelemetry.Tags.TaskState) as string != "TASK_STATE_FAILED")
    failures.Add($"run span: expected {A2ATelemetry.Tags.TaskState}=TASK_STATE_FAILED, got {runSpan.GetTagItem(A2ATelemetry.Tags.TaskState)}");

// 4. The counters. a2a.server.tasks.budget_exceeded counts tasks failed for going past their budget.
Console.WriteLine();
Console.WriteLine("--- counters");
var requests = Sum("a2a.server.requests");
var budgetExceeded = Sum("a2a.server.tasks.budget_exceeded");
Console.WriteLine($"a2a.server.requests: {requests}");
Console.WriteLine($"a2a.server.tasks.budget_exceeded: {budgetExceeded}");
if (requests < 2) failures.Add($"a2a.server.requests: expected at least 2, got {requests}");
if (budgetExceeded != 1) failures.Add($"a2a.server.tasks.budget_exceeded: expected 1, got {budgetExceeded}");

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
    return 1;
}

Console.WriteLine();
Console.WriteLine("OK: the call was traced from client to agent run, and the task past its budget ended FAILED.");
return 0;

// The in-memory exporter adds one Metric per export; the last one holds the running total of a counter.
long Sum(string name)
{
    var metric = metrics.LastOrDefault(m => m.Name == name);
    if (metric is null) return 0;
    long total = 0;
    foreach (ref readonly var point in metric.GetMetricPoints()) total += point.GetSumLong();
    return total;
}

/// <summary>
///     Calls a pretend model and reports what it spent. The server adds the usage up per task and fails the task when it
///     goes past the budget. The report that goes over throws OperationCanceledException: let it go up.
/// </summary>
internal sealed class ModelAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        #region Report
        var result = await CallModelAsync(context.UserText, cancellationToken);
        await updater.ReportUsageAsync(result.TotalTokens, result.Cost, cancellationToken);
        await updater.CompleteAsync(result.Text, cancellationToken);
        #endregion
    }

    // Stands in for an LLM call: 400 tokens per word of the prompt, 0.0001 cost units per token.
    private static Task<LlmResult> CallModelAsync(string? prompt, CancellationToken cancellationToken)
    {
        var words = (prompt ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        long tokens = 400L * words;
        return Task.FromResult(new LlmResult($"Here is my answer to '{prompt}'.", tokens, tokens * 0.0001m));
    }

    private sealed record LlmResult(string Text, long TotalTokens, decimal Cost);
}
