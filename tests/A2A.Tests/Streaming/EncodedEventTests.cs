using System.Text;
using A2A.Json;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Execution;
using A2A.Server.Idempotency;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using A2A.Tests.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests.Streaming;

/// <summary>
/// Each event is encoded once (spec D9): the engine encodes it, the size limit uses those bytes, and the event store, SSE
/// writers and push sender carry and write the same bytes.
/// </summary>
[Collection("PushGuard")]
public sealed class EncodedEventTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class DelegateFactory(Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute) : IAgentExecutorFactory
    {
        public ValueTask<AgentExecutorLease> RentAsync(string tenant, CancellationToken ct) =>
            ValueTask.FromResult(new AgentExecutorLease(new DelegateAgentExecutor(execute), scope: null));
    }


    private static TaskExecutionEngine Engine(TaskStore tasks, EventStore events, Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute,
        A2AServerOptions options)
    {
        if (string.IsNullOrEmpty(options.AgentCard.Name)) options.AgentCard.Name = "Encoded Event Test Agent";
        return new(tasks, events, new DelegateFactory(execute), new TaskEventBus(), new InMemoryPushOutbox(),
            new IdempotencyCoordinator(new InMemoryIdempotencyStore(options.Idempotency), tasks, options),
            new PushConfigService(new InMemoryPushConfigStore(), new EphemeralPushSecretProtector(), options),
            options, new AgentCardProvider(options, []));
    }

    private static AgentRequestContext NewTask() => new()
    {
        Message = Message.FromUser("go"),
        TaskId = TaskIds.New(),
        ContextId = TaskIds.New(),
        Call = ServerCallContext.Anonymous(),
    };

    private static TaskKey Key(AgentRequestContext context) => new(context.Call.Tenant, context.Call.Principal, context.TaskId);

    private static Artifact TextArtifact(int length) => new() { ArtifactId = "a1", Parts = [Part.FromText(new string('x', length))] };

    /// <summary>The event <see cref="TaskUpdater.AddArtifactAsync(Artifact, bool, bool, CancellationToken)"/> writes for <paramref name="artifact"/>.</summary>
    private static StreamResponse ArtifactEvent(AgentRequestContext context, Artifact artifact) =>
        StreamResponse.Of(new TaskArtifactUpdateEvent { TaskId = context.TaskId, ContextId = context.ContextId, Artifact = artifact });

    private static async Task<AgentTask> RunArtifactAsync(A2AServerOptions options, AgentRequestContext context, Artifact artifact)
    {
        var events = new InMemoryEventStore(options.Streaming);
        var tasks = new InMemoryTaskStore(events);
        await using var engine = Engine(tasks, events, async (_, updater, ct) =>
        {
            await updater.AddArtifactAsync(artifact, cancellationToken: ct);
            await updater.CompleteAsync(cancellationToken: ct);
        }, options);
        var exec = (await engine.StartForTestAsync(context));
        await foreach (var _ in exec.Outgoing.Reader.ReadAllAsync().WithCancellation(new CancellationTokenSource(Wait).Token)) { }
        await exec.Completion.WaitAsync(Wait);
        await engine.WhenIdleAsync().WaitAsync(Wait);
        return (await tasks.GetAsync(Key(context)))!.Task;
    }

    [Fact]
    public void SequencedEvent_event_and_json_have_no_init_accessor()
    {
        // A `with { Event = ... }` would keep stale bytes, so only Sequence stays settable through `with`.
        foreach (var name in new[] { nameof(SequencedEvent.Event), nameof(SequencedEvent.Json) })
            Assert.Null(typeof(SequencedEvent).GetProperty(name)!.SetMethod);
        Assert.NotNull(typeof(SequencedEvent).GetProperty(nameof(SequencedEvent.Sequence))!.SetMethod);
    }

    [Fact]
    public async Task Event_at_limit_is_accepted_one_over_fails()
    {
        var atLimit = NewTask();
        var limit = EventEncoder.Encode(0, ArtifactEvent(atLimit, TextArtifact(2000))).Json.Length;
        Assert.Equal(limit, EventEncoder.MeasureUtf8(ArtifactEvent(atLimit, TextArtifact(2000))));
        var options = new A2AServerOptions();
        options.Limits.MaxEventBytes = limit;

        var accepted = await RunArtifactAsync(options, atLimit, TextArtifact(2000));
        Assert.Equal(TaskState.Completed, accepted.Status.State);
        Assert.Equal(2000, Assert.Single(accepted.Artifacts!).Parts[0] is TextPart { Text: var text } ? text.Length : -1);

        // The same event with one more byte of text (the ids have the same length).
        var over = NewTask();
        Assert.Equal(limit + 1, EventEncoder.MeasureUtf8(ArtifactEvent(over, TextArtifact(2001))));
        var failed = await RunArtifactAsync(options, over, TextArtifact(2001));
        Assert.Equal(TaskState.Failed, failed.Status.State);
        Assert.Equal(TaskExecutionEngine.OversizedEventText, failed.Status.Message!.GetText());
        Assert.Null(failed.Artifacts);
    }

    public static TheoryData<string> Events() => ["task", "status", "artifact", "message"];

    private static StreamResponse Sample(string kind) => kind switch
    {
        "task" => StreamResponse.Of(new AgentTask { Id = "t1", ContextId = "c1", Status = new AgentTaskStatus { State = TaskState.Working } }),
        "status" => StreamResponse.Of(new TaskStatusUpdateEvent
        {
            TaskId = "t1", ContextId = "c1",
            Status = new AgentTaskStatus { State = TaskState.Completed, Timestamp = DateTimeOffset.UnixEpoch, Message = Message.FromUser("ü \"quoted\" <b>") },
        }),
        "artifact" => StreamResponse.Of(new TaskArtifactUpdateEvent
        {
            TaskId = "t1", ContextId = "c1", Append = true, LastChunk = true,
            Artifact = new Artifact { ArtifactId = "a", Parts = [Part.FromText("x"), Part.FromBytes(new byte[] { 1, 2, 3 }, "application/octet-stream")] },
        }),
        _ => StreamResponse.Of(Message.FromUser("hello")),
    };

    [Theory]
    [MemberData(nameof(Events))]
    public void Encoded_json_equals_fresh_serialization(string kind)
    {
        var evt = Sample(kind);
        var fresh = A2AJson.SerializeToUtf8Bytes(evt);

        var encoded = EventEncoder.Encode(5, evt);
        Assert.Equal(5, encoded.Sequence);
        Assert.Same(evt, encoded.Event);
        Assert.Equal(fresh, encoded.Json.ToArray());
        Assert.Equal(fresh.Length, EventEncoder.MeasureUtf8(evt));

        // Not encoded yet (the 2-argument constructor): encoded on demand. Already encoded: the same bytes, not encoded again.
        var plain = new SequencedEvent(3, evt);
        Assert.True(plain.Json.IsEmpty);
        var ensured = EventEncoder.EnsureEncoded(plain);
        Assert.Equal(3, ensured.Sequence);
        Assert.Equal(fresh, ensured.Json.ToArray());
        Assert.True(EventEncoder.EnsureEncoded(encoded).Json.Equals(encoded.Json));
    }

    [Fact]
    public async Task Engine_stores_and_publishes_the_same_bytes()
    {
        var options = new A2AServerOptions();
        var events = new InMemoryEventStore(options.Streaming);
        var tasks = new InMemoryTaskStore(events);
        await using var engine = Engine(tasks, events, async (_, updater, ct) =>
        {
            await updater.StartWorkAsync("working", ct);
            await updater.AddArtifactAsync([Part.FromText("part")], cancellationToken: ct);
            await updater.CompleteAsync("done", ct);
        }, options);
        var context = NewTask();
        var exec = (await engine.StartForTestAsync(context));
        var published = new List<SequencedEvent>();
        await foreach (var e in exec.Outgoing.Reader.ReadAllAsync().WithCancellation(new CancellationTokenSource(Wait).Token)) published.Add(e);
        await engine.WhenIdleAsync().WaitAsync(Wait);

        var stored = new List<SequencedEvent>();
        await foreach (var e in events.ReadAfterAsync(Key(context), 0)) stored.Add(e);
        Assert.Equal(4, published.Count);
        Assert.Equal(published.Select(e => e.Sequence), stored.Select(e => e.Sequence));
        for (var i = 0; i < published.Count; i++)
        {
            Assert.False(published[i].Json.IsEmpty);
            Assert.True(published[i].Json.Equals(stored[i].Json), "the stored event carries the published bytes");
            Assert.Equal(A2AJson.SerializeToUtf8Bytes(stored[i].Event), stored[i].Json.ToArray());
        }
    }

    /// <summary>Counts the async writes and flushes on the response body (the base async writes call the sync ones).</summary>
    private sealed class CountingBody : MemoryStream
    {
        public int Writes { get; private set; }
        public int Flushes { get; private set; }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Writes++;
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushes++;
            return base.FlushAsync(cancellationToken);
        }
    }

    [Theory]
    [InlineData("t1:3")]
    [InlineData("t-ü:12")]
    [InlineData(null)]
    public async Task Sse_frame_bytes_equal_previous_format(string? id)
    {
        var json = EventEncoder.Encode(3, Sample("status")).Json;
        var body = new CountingBody();
        var http = new DefaultHttpContext();
        http.Response.Body = body;

        await HttpPlumbing.WriteEventAsync(http, id, json);

        var expected = (id is null ? "" : $"id: {id}\n") + "data: " + Encoding.UTF8.GetString(json.Span) + "\n\n";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), body.ToArray());
        Assert.Equal(1, body.Writes);
        Assert.Equal(1, body.Flushes);
    }

    /// <summary>Records each webhook body.</summary>
    private sealed class BodyRecorder : HttpMessageHandler
    {
        public List<byte[]> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsByteArrayAsync(ct));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Push_body_equals_event_json()
    {
        var key = new TaskKey("", "alice", "t1");
        var configs = new InMemoryPushConfigStore();
        await configs.SaveAsync(new PushConfigRecord(key, new TaskPushNotificationConfig { Id = "c1", TaskId = key.TaskId, Url = "https://hooks.example/cb" }));
        var options = new A2AServerOptions();
        options.PushNotifications.AllowPrivateNetworks = true;
        var events = new InMemoryEventStore(options.Streaming);
        var outbox = new InMemoryPushOutbox();
        var tasks = new InMemoryTaskStore(events, outbox);
        var handler = new BodyRecorder();
        var service = new PushDeliveryService(outbox, configs, new EphemeralPushSecretProtector(), new HttpMessageInvoker(handler, disposeHandler: false), options);

        var evt = Sample("status");
        var encoded = EventEncoder.Encode(0, evt);
        // The outbox keeps the bytes the commit carries, never a new encoding: these differ from a fresh one by a trailing space.
        var marked = new SequencedEvent(0, evt, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(encoded.Json.Span) + " "));
        var now = DateTimeOffset.UtcNow;
        var record = new TaskRecord("", "alice", new AgentTask { Id = key.TaskId, ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working } }, 0, now, now);
        var stored = (await tasks.CommitAsync(new TaskCommit(record, 0, encoded, [new PushDelivery(key, "c1", 0, "d1")]))).Record;
        stored = (await tasks.CommitAsync(new TaskCommit(stored, stored.Version, marked, [new PushDelivery(key, "c1", 0, "d2")]))).Record;
        await tasks.CommitAsync(new TaskCommit(stored, stored.Version, new SequencedEvent(0, evt), [new PushDelivery(key, "c1", 0, "d3")])); // not encoded: encoded on demand
        for (var i = 0; i < 3; i++) Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None)); // in order, one at a time

        Assert.Equal(3, handler.Bodies.Count);
        Assert.Equal(encoded.Json.ToArray(), handler.Bodies[0]);
        Assert.Equal(marked.Json.ToArray(), handler.Bodies[1]);
        Assert.Equal(A2AJson.SerializeToUtf8Bytes(evt), handler.Bodies[2]);
    }

    /// <summary>
    /// Keeps a marked copy of each committed event's bytes (a trailing space, still the same JSON value), so a stream that
    /// writes stored bytes is told apart from one that encodes the event again.
    /// </summary>
    private sealed class MarkingTaskStore : DelegatingTaskStore
    {
        public override ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default) =>
            Inner.CommitAsync(commit with
            {
                Event = new SequencedEvent(commit.Event.Sequence, commit.Event.Event, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(commit.Event.Json.Span) + " ")),
            }, ct);
    }

    [Fact]
    public async Task Replay_writes_stored_bytes_without_reencoding()
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddTaskStore(new MarkingTaskStore()));
        var task = (await host.Client(ProtocolBindings.HttpJson).SendMessageAsync("task")).RequireTask();
        await host.WaitIdleAsync();
        var store = host.App.Services.GetRequiredService<EventStore>();
        var stored = new List<SequencedEvent>();
        await foreach (var e in store.ReadAfterAsync(new TaskKey("", "alice", task.Id), 1)) stored.Add(e);
        Assert.True(stored.Count >= 3, $"only {stored.Count} stored events");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/a2a/tasks/{task.Id}:subscribe");
        request.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        request.Headers.TryAddWithoutValidation("Last-Event-ID", StreamCursor.Format(task.Id, 1));
        using var response = await host.Http("alice").SendAsync(request).WaitAsync(Wait);
        var body = await response.Content.ReadAsByteArrayAsync();

        var expected = new List<byte>();
        foreach (var e in stored)
            expected.AddRange(Encoding.UTF8.GetBytes($"id: {StreamCursor.Format(task.Id, e.Sequence)}\ndata: {Encoding.UTF8.GetString(e.Json.Span)}\n\n"));
        Assert.Equal(Encoding.UTF8.GetString(expected.ToArray()), Encoding.UTF8.GetString(body));
    }
}
