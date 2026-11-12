using A2A.Client;
using A2A.Client.Streaming;

namespace A2A.Tests;

/// <summary>
/// A2A 1.1 §3.2.7 and §3.5.2 on the client: a gap in the generations of a stream is reconciled with GetTask, events already
/// reflected are skipped, a server without generations is not trusted with them, and a reconnect without an event id
/// resumes after the last generation seen.
/// </summary>
public sealed class ClientGenerationTests
{
    private const string TaskId = "t1";

    private static AgentTask Task(long? generation, TaskState state = TaskState.Working) => new()
    {
        Id = TaskId, ContextId = "c1", Status = new AgentTaskStatus { State = state }, Generation = generation,
    };

    private static StreamResponse Status(long? generation, TaskState state = TaskState.Working) =>
        new StatusUpdateEvent(new TaskStatusUpdateEvent
        {
            TaskId = TaskId, ContextId = "c1", Status = new AgentTaskStatus { State = state }, Generation = generation,
        });

    private static StreamResponse Artifact(long? generation) =>
        new ArtifactUpdateEvent(new TaskArtifactUpdateEvent
        {
            TaskId = TaskId, ContextId = "c1", Generation = generation,
            Artifact = new Artifact { ArtifactId = "a1", Parts = [Part.FromText("x")] },
        });

    private static long? Gen(StreamResponse e) => e switch
    {
        TaskEvent t => t.Task.Generation,
        StatusUpdateEvent s => s.StatusUpdate.Generation,
        ArtifactUpdateEvent a => a.ArtifactUpdate.Generation,
        _ => null,
    };

    /// <summary>A transport whose subscriptions play scripts, one per call, and whose GetTask answers a fixed task.</summary>
    private sealed class ScriptedTransport(AgentTask current, params Func<IAsyncEnumerable<StreamResponse>>[] scripts) : IA2AClient
    {
        private int _next;
        public List<SubscribeToTaskRequest> Subscribes { get; } = [];
        public int GetTaskCalls { get; private set; }

        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Subscribes.Add(request);
            return scripts[Math.Min(_next++, scripts.Length - 1)]();
        }

        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            GetTaskCalls++;
            return System.Threading.Tasks.Task.FromResult(current);
        }

        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

#pragma warning disable CS1998 // scripts without awaits
    private static Func<IAsyncEnumerable<StreamResponse>> Script(params StreamResponse[] events) => () => Play(events, null);
    private static Func<IAsyncEnumerable<StreamResponse>> ScriptThenDrop(params StreamResponse[] events) => () => Play(events, new IOException("dropped"));

    private static async IAsyncEnumerable<StreamResponse> Play(StreamResponse[] events, Exception? then)
    {
        foreach (var e in events) yield return e;
        if (then is not null) throw then;
    }
#pragma warning restore CS1998

    private static A2AClient Client(ScriptedTransport transport, string version = "1.1", bool reconcile = true) =>
        new(transport, new A2AClientOptions { ProtocolVersion = version, ReconcileGenerationGaps = reconcile, MaxRetries = 0 });

    private static async Task<List<StreamResponse>> ObserveAsync(A2AClient client)
    {
        var events = new List<StreamResponse>();
        await foreach (var e in client.ObserveTaskAsync(TaskId).WithCancellation(new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token))
            events.Add(e);
        return events;
    }

    [Fact]
    public async Task A_gap_is_reconciled_with_GetTask_and_events_it_already_covers_are_skipped()
    {
        var transport = new ScriptedTransport(Task(6, TaskState.Working),
            Script(new TaskEvent(Task(1)), Status(2), Artifact(4), Status(5), Status(6), Status(7, TaskState.Completed)));
        var events = await ObserveAsync(Client(transport));

        Assert.Equal(1, transport.GetTaskCalls);
        // 4 jumped over 3: after it, the task as it is now (6). Then 5 and 6 are already in that snapshot.
        Assert.Equal([1, 2, 4, 6, 7], events.Select(Gen));
        Assert.IsType<TaskEvent>(events[3]);
        Assert.Equal(TaskState.Completed, Assert.IsType<StatusUpdateEvent>(events[^1]).StatusUpdate.Status.State);
    }

    [Fact]
    public async Task With_reconciliation_off_a_gap_is_passed_on_as_it_is()
    {
        var transport = new ScriptedTransport(Task(6),
            Script(new TaskEvent(Task(1)), Status(2), Artifact(4), Status(5, TaskState.Completed)));
        var events = await ObserveAsync(Client(transport, reconcile: false));

        Assert.Equal(0, transport.GetTaskCalls);
        Assert.Equal([1, 2, 4, 5], events.Select(Gen));
    }

    [Fact]
    public async Task A_server_that_repeats_a_generation_is_not_trusted_with_generations()
    {
        // Two update events with generation 2: the server does not implement it, so 2 then 9 is no gap and nothing is skipped.
        var transport = new ScriptedTransport(Task(9),
            Script(new TaskEvent(Task(1)), Status(2), Artifact(2), Status(9), Status(3, TaskState.Completed)));
        var events = await ObserveAsync(Client(transport));

        Assert.Equal(0, transport.GetTaskCalls);
        Assert.Equal([1, 2, 2, 9, 3], events.Select(Gen));
    }

    [Fact]
    public async Task Events_without_generations_change_nothing()
    {
        var transport = new ScriptedTransport(Task(null),
            Script(new TaskEvent(Task(null)), Status(null), Artifact(null), Status(null, TaskState.Completed)));
        var events = await ObserveAsync(Client(transport));

        Assert.Equal(0, transport.GetTaskCalls);
        Assert.Equal(4, events.Count);
    }

    [Fact]
    public async Task A_reconnect_without_event_ids_resumes_after_the_last_generation()
    {
        // This transport has no SSE event ids (like gRPC): the reconnect carries lastSeenGeneration instead.
        var transport = new ScriptedTransport(Task(3),
            ScriptThenDrop(new TaskEvent(Task(1)), Status(2)),
            Script(Status(3, TaskState.Completed)));
        var events = await ObserveAsync(Client(transport));

        Assert.Equal(2, transport.Subscribes.Count);
        Assert.Null(transport.Subscribes[0].LastSeenGeneration);
        Assert.Equal(2, transport.Subscribes[1].LastSeenGeneration);
        Assert.Equal([1, 2, 3], events.Select(Gen));
    }

    [Fact]
    public async Task A_1_0_client_never_sends_lastSeenGeneration()
    {
        var transport = new ScriptedTransport(Task(3),
            ScriptThenDrop(new TaskEvent(Task(1)), Status(2)),
            Script(Status(3, TaskState.Completed)));
        await ObserveAsync(Client(transport, version: "1.0"));

        Assert.All(transport.Subscribes, s => Assert.Null(s.LastSeenGeneration));
    }

    [Fact]
    public void The_tracker_follows_snapshots_and_updates()
    {
        var tracker = new GenerationTracker();
        Assert.Equal(GenerationVerdict.InSequence, tracker.Observe(new TaskEvent(Task(5))));
        Assert.Equal(5, tracker.Last);
        Assert.Equal(GenerationVerdict.AlreadyReflected, tracker.Observe(Status(5))); // in the snapshot
        Assert.Equal(GenerationVerdict.InSequence, tracker.Observe(Status(6)));
        Assert.Equal(GenerationVerdict.AlreadyReflected, tracker.Observe(Status(4))); // a replay
        Assert.Equal(GenerationVerdict.Gap, tracker.Observe(Artifact(9)));
        Assert.Equal(9, tracker.Last);
        Assert.Equal(GenerationVerdict.InSequence, tracker.Observe(Status(0))); // 0: not used
        Assert.Equal(9, tracker.Last);
        Assert.Equal(GenerationVerdict.InSequence, tracker.Observe(Artifact(9))); // a second update with 9: disabled
        Assert.Null(tracker.Last);
        Assert.Equal(GenerationVerdict.InSequence, tracker.Observe(Status(2)));
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    [InlineData(ProtocolBindings.Grpc)]
    public async Task This_SDKs_1_1_streams_are_contiguous_and_need_no_reconciliation(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.EnableProtocolV11(), grpc: binding == ProtocolBindings.Grpc);
        var client = host.Client(binding, options: new A2AClientOptions { ProtocolVersion = A2AProtocol.Version11 });

        var events = new List<StreamResponse>();
        await foreach (var e in client.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("task") }))
            events.Add(e);

        Assert.Single(events.OfType<TaskEvent>()); // only the first snapshot: no reconcile happened
        var generations = events.Select(Gen).ToList();
        Assert.All(generations, g => Assert.NotNull(g));
        Assert.Equal(Enumerable.Range((int)generations[0]!.Value, generations.Count).Select(g => (long?)g), generations);
    }
}
