using System.Net;
using System.Text;
using System.Text.Json;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Server;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests.Parity;

/// <summary>Records the metadata the agent receives for Execute and for Cancel.</summary>
public sealed class MetadataRecorder
{
    private readonly TaskCompletionSource<IReadOnlyDictionary<string, JsonElement>?> _execute = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<IReadOnlyDictionary<string, JsonElement>?> _cancel = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void RecordExecute(IReadOnlyDictionary<string, JsonElement>? metadata) => _execute.TrySetResult(metadata);

    public void RecordCancel(IReadOnlyDictionary<string, JsonElement>? metadata) => _cancel.TrySetResult(metadata);

    public Task<IReadOnlyDictionary<string, JsonElement>?> Execute => _execute.Task.WaitAsync(TimeSpan.FromSeconds(30));

    public Task<IReadOnlyDictionary<string, JsonElement>?> Cancel => _cancel.Task.WaitAsync(TimeSpan.FromSeconds(30));
}

/// <summary>The scenario agent, plus a record of the request metadata it was given.</summary>
public sealed class MetadataAgent(MetadataRecorder recorder) : IAgentExecutor
{
    private readonly ScenarioAgent _inner = new();

    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
    {
        recorder.RecordExecute(context.Metadata);
        return _inner.ExecuteAsync(context, updater, ct);
    }

    public Task CancelAsync(AgentTask task, TaskUpdater updater, CancellationToken ct)
    {
        recorder.RecordCancel(updater.RequestMetadata);
        return updater.CancelAsync(cancellationToken: ct).AsTask();
    }
}

/// <summary>A task store whose Nth update fails with a non-conflict error, like a database that went away.</summary>
public sealed class FailOnNthUpdateStore(int failOn) : TaskStore
{
    private readonly InMemoryTaskStore _inner = new();
    private int _updates;

    public override ValueTask<TaskRecord?> GetAsync(TaskKey key, CancellationToken ct = default) => _inner.GetAsync(key, ct);

    public override ValueTask<TaskRecord> CreateAsync(TaskRecord record, CancellationToken ct = default) => _inner.CreateAsync(record, ct);

    public override ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion, CancellationToken ct = default) =>
        Interlocked.Increment(ref _updates) == failOn
            ? throw new InvalidOperationException("the database went away")
            : _inner.UpdateAsync(record, expectedVersion, ct);

    public override ValueTask<TaskPage> ListAsync(TaskQuery query, CancellationToken ct = default) => _inner.ListAsync(query, ct);

    public override ValueTask<ContextRecord?> GetContextAsync(string tenant, string contextId, CancellationToken ct = default) =>
        _inner.GetContextAsync(tenant, contextId, ct);

    public override ValueTask<ContextRecord> GetOrCreateContextAsync(ContextRecord record, CancellationToken ct = default) =>
        _inner.GetOrCreateContextAsync(record, ct);
}

/// <summary>
/// One test per historical a2a-dotnet failure (design §7.10). Several overlap with tests of earlier tasks; each is kept
/// here as a short, explicit statement of the issue.
/// </summary>
public class RegressionTests
{
    public static TheoryData<string> Bindings => BindingTests.Bindings;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>#478: the agent fails after the response headers are out. The client gets a FAILED event and a clean end.</summary>
    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Issue478_error_after_stream_headers_becomes_error_event(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var events = new List<StreamResponse>();
        await foreach (var e in host.Client(binding).SendStreamingMessageAsync(Message.FromUser("stream-error")).WithCancellation(new CancellationTokenSource(Wait).Token))
            events.Add(e);

        Assert.IsType<TaskEvent>(events[0]);
        Assert.Equal(TaskState.Working, Assert.IsType<StatusUpdateEvent>(events[1]).StatusUpdate.Status.State);
        var last = Assert.IsType<StatusUpdateEvent>(events[^1]);
        Assert.Equal(TaskState.Failed, last.StatusUpdate.Status.State);
        Assert.DoesNotContain("boom", last.StatusUpdate.Status.Message?.GetText() ?? ""); // no exception text on the wire
    }

    /// <summary>#495: the store fails on the second update. The stream must end with an error, not as if it had finished.</summary>
    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Issue495_persistence_failure_mid_stream_is_not_silent_eof(string binding)
    {
        // Update 1 stores WORKING; update 2 (the first artifact) fails.
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddSingleton<TaskStore>(new FailOnNthUpdateStore(2)));
        var seen = new List<StreamResponse>();
        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var e in host.Client(binding).SendStreamingMessageAsync(Message.FromUser("task")).WithCancellation(new CancellationTokenSource(Wait).Token))
                seen.Add(e);
        });

        Assert.Equal(A2AErrorCode.Internal, ex.Code);
        Assert.DoesNotContain("went away", ex.Message);
        Assert.DoesNotContain(seen, e => e is StatusUpdateEvent { StatusUpdate.Status.State: TaskState.Completed });
    }

    /// <summary>#551: a JSON-RPC error object inside an SSE stream is thrown to the caller, not swallowed or parsed as an event.</summary>
    [Fact]
    public async Task Issue551_jsonrpc_error_inside_stream_surfaces_as_exception()
    {
        const string body =
            "data: {\"jsonrpc\":\"2.0\",\"id\":\"x\",\"result\":{\"statusUpdate\":{\"taskId\":\"t1\",\"contextId\":\"c1\",\"status\":{\"state\":\"TASK_STATE_WORKING\"}}}}\n\n" +
            "data: {\"jsonrpc\":\"2.0\",\"id\":\"x\",\"error\":{\"code\":-32001,\"message\":\"gone\"}}\n\n";
        using var http = new HttpClient(new StubHandler(_ =>
        {
            var content = new StringContent(body, Encoding.UTF8, "text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        var options = new A2AClientOptions();
        var client = new A2AClient(new JsonRpcTransport(http, new Uri("http://localhost/a2a"), options), options);

        var received = new List<StreamResponse>();
        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var e in client.SendStreamingMessageAsync(Message.FromUser("hi")).WithCancellation(new CancellationTokenSource(Wait).Token))
                received.Add(e);
        });

        Assert.Single(received);
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
    }

    /// <summary>#508: a follow-up to an INPUT_REQUIRED task over JSON-RPC returns the task, with the new turn applied.</summary>
    [Fact]
    public async Task Issue508_input_required_continuation_over_jsonrpc_returns_task()
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(ProtocolBindings.JsonRpc);
        var first = (await client.SendMessageAsync("input")).RequireTask();
        Assert.Equal(TaskState.InputRequired, first.Status.State);

        var second = (await client.SendMessageAsync("more", first.ContextId, first.Id)).RequireTask();

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(TaskState.Completed, second.Status.State);
        Assert.Equal("got: more", second.Status.Message!.GetText());
    }

    /// <summary>#593: the metadata in a REST CancelTask body reaches the agent's CancelAsync.</summary>
    [Fact]
    public async Task Issue593_rest_cancel_passes_metadata()
    {
        var recorder = new MetadataRecorder();
        await using var host = await TestAgentHost.StartAsync(services: s =>
        {
            s.AddSingleton(recorder);
            s.AddScoped<IAgentExecutor, MetadataAgent>();
        });
        var client = host.Client(ProtocolBindings.HttpJson);
        var task = (await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();

        await client.CancelTaskAsync(new CancelTaskRequest
        {
            Id = task.Id,
            Metadata = new() { ["reason"] = JsonSerializer.SerializeToElement("user-request") },
        });

        var seen = await recorder.Cancel;
        Assert.Equal("user-request", seen!["reason"].GetString());
    }

    /// <summary>#591: REST SubscribeToTask answers a GET, as the proto says.</summary>
    [Fact]
    public async Task Issue591_rest_get_subscribe_works()
    {
        await using var host = await TestAgentHost.StartAsync();
        var task = (await host.Client(ProtocolBindings.HttpJson).SendMessageAsync("input")).RequireTask();

        using var http = host.Http("alice");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/a2a/tasks/{task.Id}:subscribe");
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        using var cts = new CancellationTokenSource(Wait);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));
        string? line;
        do { line = await reader.ReadLineAsync(cts.Token); } while (line is not null && !line.StartsWith("data:", StringComparison.Ordinal));
        Assert.NotNull(line);
        Assert.Contains(task.Id, line);
    }

    /// <summary>#540: a JSON object with the same property twice is refused, not resolved by "last one wins".</summary>
    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Issue540_duplicate_json_properties_rejected(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        const string dup = """{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"a"}]},"message":{"messageId":"m2","role":"ROLE_USER","parts":[{"text":"b"}]}}""";
        var rpc = binding == ProtocolBindings.JsonRpc;
        using var req = new HttpRequestMessage(HttpMethod.Post, rpc ? "/a2a" : "/a2a/message:send")
        {
            Content = new StringContent(rpc ? $$"""{"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{{dup}}}""" : dup, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        using var http = host.Http("alice");
        using var resp = await http.SendAsync(req);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        if (rpc)
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal((int)A2AErrorCode.InvalidParams, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        Assert.Empty((await host.Client(binding).ListTasksAsync(new ListTasksRequest())).Tasks); // nothing was created
    }

    /// <summary>#350: with ReturnImmediately, an agent that throws in the background must not leave the task in WORKING or SUBMITTED.</summary>
    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Issue350_background_exception_does_not_leave_task_working(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync(Message.FromUser("fail"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();

        var deadline = DateTime.UtcNow + Wait;
        AgentTask current;
        do
        {
            current = await client.GetTaskAsync(task.Id);
            if (current.Status.State.IsTerminal()) break;
            await Task.Delay(20);
        } while (DateTime.UtcNow < deadline);

        Assert.Equal(TaskState.Failed, current.Status.State);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
