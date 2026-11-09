using A2A.Server;
using A2A.Server.Execution;
using A2A.Server.Idempotency;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;

namespace A2A.Tests.Execution;

/// <summary>
/// Artifact chunks that queue up while the engine is busy are applied to one copy of the task and committed together
/// (<see cref="IBatchTaskCommit"/>). What is stored and sent must be the same as with one commit per event.
/// </summary>
public sealed class BatchCommitTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class Executor(Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute) : IAgentExecutor
    {
        public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct) => execute(context, updater, ct);
        public Task CancelAsync(AgentTask task, TaskUpdater updater, CancellationToken ct) => updater.CancelAsync(cancellationToken: ct).AsTask();
    }

    private sealed class Factory(IAgentExecutor executor) : IAgentExecutorFactory
    {
        public ValueTask<AgentExecutorLease> RentAsync(string tenant, CancellationToken ct) => ValueTask.FromResult(new AgentExecutorLease(executor, scope: null));
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServerCallContext _call = ServerCallContext.Anonymous();
        public InMemoryTaskStore Tasks { get; }
        public InMemoryEventStore Events { get; }
        public TaskExecutionEngine Engine { get; }

        public Rig(Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute, Action<A2AServerOptions>? configure = null)
        {
            var options = new A2AServerOptions();
            options.AgentCard.Name = "Batch Test Agent";
            configure?.Invoke(options);
            Events = new InMemoryEventStore(options.Streaming);
            Tasks = new InMemoryTaskStore(Events, new InMemoryPushOutbox());
            Engine = new TaskExecutionEngine(Tasks, Events, new Factory(new Executor(execute)), new TaskEventBus(), new InMemoryPushOutbox(),
                new IdempotencyCoordinator(new InMemoryIdempotencyStore(options.Idempotency), Tasks, options),
                new PushConfigService(new InMemoryPushConfigStore(), new EphemeralPushSecretProtector(), options),
                options, new AgentCardProvider(options, []));
        }

        public AgentRequestContext NewTask() => new() { Message = Message.FromUser("go"), TaskId = TaskIds.New(), ContextId = TaskIds.New(), Call = _call };

        public TaskKey Key(string taskId) => new(_call.Tenant, _call.Principal, taskId);

        public async Task<List<SequencedEvent>> LogAsync(string taskId)
        {
            var list = new List<SequencedEvent>();
            await foreach (var e in Events.ReadAfterAsync(Key(taskId), 0)) list.Add(e);
            return list;
        }

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    /// <summary>
    /// The agent creates an artifact, then appends <paramref name="chunks"/> chunks while the test holds the task lock, so they
    /// queue up behind the first one; then completes. Returns the rig, the task id and the stored record.
    /// </summary>
    private static async Task<(Rig Rig, string TaskId, TaskRecord Stored)> RunQueuedAsync(int chunks, Action<A2AServerOptions>? configure = null,
        bool sealLast = false)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new Rig(async (_, u, ct) =>
        {
            await u.StartWorkAsync(cancellationToken: ct);
            var id = await u.AddArtifactAsync([Part.FromText("p0")], name: "out", cancellationToken: ct);
            ready.TrySetResult();
            await go.Task.WaitAsync(ct);
            try
            {
                for (var i = 1; i <= chunks; i++)
                    await u.AddArtifactAsync([Part.FromText($"p{i}")], artifactId: id, append: true, lastChunk: sealLast && i == chunks, cancellationToken: ct);
                await u.CompleteAsync("done", ct);
            }
            finally
            {
                written.TrySetResult();
            }
        }, configure);
        var context = rig.NewTask();
        var exec = await rig.Engine.StartForTestAsync(context);
        await ready.Task.WaitAsync(Wait);
        // Wait until the artifact is stored, so the chunks below are appends to it.
        while ((await rig.Tasks.GetAsync(rig.Key(context.TaskId)))?.Task.Artifacts is not { Count: 1 }) await Task.Delay(5);
        using (await rig.Engine.LockAsync(rig.Key(context.TaskId), CancellationToken.None))
        {
            go.TrySetResult();
            await written.Task.WaitAsync(Wait);
        }
        await exec.Completion.WaitAsync(Wait);
        await rig.Engine.WhenIdleAsync().WaitAsync(Wait);
        return (rig, context.TaskId, (await rig.Tasks.GetAsync(rig.Key(context.TaskId)))!);
    }

    private static string[] Texts(Artifact a) => a.Parts.OfType<TextPart>().Select(p => p.Text).ToArray();

    [Fact]
    public async Task Queued_chunks_are_stored_in_order_with_one_event_each_and_fewer_task_writes()
    {
        const int chunks = 40;
        var (rig, taskId, stored) = await RunQueuedAsync(chunks);
        await using var _ = rig;

        Assert.Equal(TaskState.Completed, stored.Task.Status.State);
        Assert.Equal(Enumerable.Range(0, chunks + 1).Select(i => $"p{i}"), Texts(Assert.Single(stored.Task.Artifacts!)));

        var log = await rig.LogAsync(taskId);
        Assert.Equal(Enumerable.Range(1, log.Count).Select(i => (long)i), log.Select(e => e.Sequence)); // no gaps
        var appends = log.Select(e => e.Event).OfType<ArtifactUpdateEvent>().Where(e => e.ArtifactUpdate.Append).ToList();
        Assert.Equal(Enumerable.Range(1, chunks).Select(i => $"p{i}"), appends.Select(e => Assert.IsType<TextPart>(Assert.Single(e.ArtifactUpdate.Artifact.Parts)).Text));
        // Snapshot, WORKING, artifact, chunks, COMPLETED: one event each. The task was written far fewer times than that.
        Assert.Equal(chunks + 4, log.Count);
        Assert.True(stored.Version < chunks / 2, $"version {stored.Version}: the queued chunks were not batched");
    }

    [Fact]
    public async Task A_sealing_chunk_ends_the_batch_and_still_seals()
    {
        var (rig, taskId, stored) = await RunQueuedAsync(10, sealLast: true);
        await using var _ = rig;

        var artifact = Assert.Single(stored.Task.Artifacts!);
        Assert.Equal(Enumerable.Range(0, 11).Select(i => $"p{i}"), Texts(artifact));
        Assert.True(ArtifactSeal.IsSealed(artifact));
        Assert.Equal(TaskState.Completed, stored.Task.Status.State);
        Assert.Equal(14, (await rig.LogAsync(taskId)).Count);
    }

    [Fact]
    public async Task A_chunk_past_the_size_cap_fails_the_task_after_the_chunks_that_fit()
    {
        // Each chunk adds a few bytes; the cap lets a handful in, then the task fails, as with one commit per event.
        var (rig, taskId, stored) = await RunQueuedAsync(40, o => o.Limits.MaxTaskBytes = 40);
        await using var _ = rig;

        Assert.Equal(TaskState.Failed, stored.Task.Status.State);
        var texts = Texts(Assert.Single(stored.Task.Artifacts!));
        Assert.True(texts.Length < 41);
        Assert.Equal(Enumerable.Range(0, texts.Length).Select(i => $"p{i}"), texts); // a prefix, in order
        Assert.True(texts.Length > 2, "some chunks fit");
        var log = await rig.LogAsync(taskId);
        Assert.Equal(Enumerable.Range(1, log.Count).Select(i => (long)i), log.Select(e => e.Sequence));
        Assert.Equal(TaskState.Failed, Assert.IsType<StatusUpdateEvent>(log[^1].Event).StatusUpdate.Status.State);
    }

    [Fact]
    public async Task Batch_commit_queues_one_delivery_per_event_with_its_own_sequence_and_id()
    {
        var events = new InMemoryEventStore(new StreamingOptions());
        var outbox = new InMemoryPushOutbox();
        var store = new InMemoryTaskStore(events, outbox);
        var key = new TaskKey("t", "alice", "id1");
        var record = new TaskRecord("t", "alice",
            new AgentTask { Id = "id1", ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working } }, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var created = await store.CommitAsync(new TaskCommit(record, 0, StreamResponse.Of(record.Task), []));

        static SequencedEvent Evt(string text) => EventEncoder.Encode(0, StreamResponse.Of(new TaskArtifactUpdateEvent
        {
            TaskId = "id1", ContextId = "c", Append = true, Artifact = new Artifact { ArtifactId = "a", Parts = [Part.FromText(text)] },
        }));
        PushDelivery D(string id) => new(key, "cfg", 0, id);

        var result = await store.CommitBatchAsync(new TaskBatchCommit(created.Record, 1,
            [new BatchedEvent(Evt("x"), [D("d1")]), new BatchedEvent(Evt("y"), []), new BatchedEvent(Evt("z"), [D("d3")])]));

        Assert.Equal(2, result.FirstSequence);
        // The outbox hands out one delivery per config at a time, in sequence order.
        var seen = new List<(string, long)>();
        while ((await outbox.LeaseDueAsync(10, TimeSpan.FromMinutes(1))) is { Count: > 0 } due)
        {
            foreach (var e in due)
            {
                seen.Add((e.Delivery.DeliveryId, e.Delivery.Sequence));
                await outbox.CompleteAsync(e);
            }
        }
        Assert.Equal([("d1", 2L), ("d3", 4L)], seen);
    }
}
