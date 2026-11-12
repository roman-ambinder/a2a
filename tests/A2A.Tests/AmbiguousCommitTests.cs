using System.Globalization;
using A2A.Server;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>
/// D2 (ADR-002): a commit that the store applied, but whose caller saw an error. This happens when a database commits the
/// transaction and the connection then times out or resets before the answer arrives, or when a store retries a write
/// internally and the retry meets its own first attempt. With <see cref="ITaskCommitLookup"/>, the engine finds the
/// stored commit by its <see cref="TaskCommit.MutationId"/>, so the task and its event log hold each event once.
/// </summary>
public class AmbiguousCommitTests
{
    /// <summary>How the store fails after it applied the commit.</summary>
    public enum Failure
    {
        /// <summary>The answer is lost: the caller sees a timeout.</summary>
        Timeout,

        /// <summary>The store's own retry met the first attempt's write: the caller sees a version conflict.</summary>
        Conflict,
    }

    private static Exception Make(Failure failure, TaskKey key, long expected) => failure == Failure.Timeout
        ? new TimeoutException("The commit was applied, but its answer was lost.")
        : new ConcurrencyException(key, expected);

    /// <summary>
    /// Wraps the in-memory store. The <paramref name="failingArtifactCommit"/>th single commit of an event that
    /// <paramref name="counted"/> picks (default: artifact events) is applied, and then the call fails as
    /// <paramref name="failure"/> says. Every other call goes straight through.
    /// </summary>
    public class AmbiguousTaskStore(InMemoryTaskStore inner, Failure failure, int failingArtifactCommit,
        Func<StreamResponse, bool>? counted = null) : TaskStore
    {
        private readonly Func<StreamResponse, bool> _counted = counted ?? IsArtifact;
        private int _artifactCommits;
        protected InMemoryTaskStore Inner => inner;
        protected Failure Mode => failure;

        public override async ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default)
        {
            var result = await inner.CommitAsync(commit, ct);
            if (_counted(commit.Event.Event) && Interlocked.Increment(ref _artifactCommits) == failingArtifactCommit)
                throw Make(failure, commit.Record.Key, commit.ExpectedVersion);
            return result;
        }

        public override ValueTask<TaskRecord?> GetAsync(TaskKey key, CancellationToken ct = default) => inner.GetAsync(key, ct);
        public override ValueTask<TaskRecord> CreateAsync(TaskRecord record, CancellationToken ct = default) => inner.CreateAsync(record, ct);
        public override ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion, CancellationToken ct = default) => inner.UpdateAsync(record, expectedVersion, ct);
        public override ValueTask<TaskPage> ListAsync(TaskQuery query, CancellationToken ct = default) => inner.ListAsync(query, ct);
        public override ValueTask<ContextRecord?> GetContextAsync(string tenant, string contextId, CancellationToken ct = default) => inner.GetContextAsync(tenant, contextId, ct);
        public override ValueTask<ContextRecord> GetOrCreateContextAsync(ContextRecord record, CancellationToken ct = default) => inner.GetOrCreateContextAsync(record, ct);
        public override ValueTask<bool> DeleteAsync(TaskKey key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
        public override ValueTask<IReadOnlyList<TaskKey>> ListExpiredAsync(DateTimeOffset terminalBefore, int max, CancellationToken ct = default) => inner.ListExpiredAsync(terminalBefore, max, ct);
        public override ValueTask<IReadOnlyList<TaskKey>> ListInterruptedBeforeAsync(DateTimeOffset before, int max, CancellationToken ct = default) => inner.ListInterruptedBeforeAsync(before, max, ct);
        public override ValueTask<bool> DeleteContextAsync(string tenant, string contextId, CancellationToken ct = default) => inner.DeleteContextAsync(tenant, contextId, ct);
        public override ValueTask<IReadOnlyList<ContextRecord>> ListOrphanContextsAsync(DateTimeOffset createdBefore, int max, CancellationToken ct = default) => inner.ListOrphanContextsAsync(createdBefore, max, ct);
        public override ValueTask<TaskRecord?> GetForMaintenanceAsync(string tenant, string taskId, CancellationToken ct = default) => inner.GetForMaintenanceAsync(tenant, taskId, ct);
    }

    private static bool IsArtifact(StreamResponse e) => e is ArtifactUpdateEvent;
    private static bool IsStatus(StreamResponse e) => e is StatusUpdateEvent;

    /// <summary>The same store, able to tell whether a commit was stored (it asks the in-memory store).</summary>
    public sealed class LookupStore(InMemoryTaskStore inner, Failure failure, int failingArtifactCommit)
        : AmbiguousTaskStore(inner, failure, failingArtifactCommit), ITaskCommitLookup
    {
        public ValueTask<long?> FindCommitAsync(TaskKey task, string mutationId, CancellationToken ct = default) =>
            Inner.FindCommitAsync(task, mutationId, ct);
    }

    /// <summary>
    /// A store with batch commits. Holds the first artifact commit for a moment, so the chunks behind it queue up and are
    /// committed as a batch; the first batch is applied, and then the call fails.
    /// </summary>
    public sealed class BatchStore(InMemoryTaskStore inner, Failure failure)
        : AmbiguousTaskStore(inner, failure, failingArtifactCommit: 0), ITaskCommitLookup, IBatchTaskCommit
    {
        private int _held;
        private int _batches;
        public int Batches => _batches;

        public override async ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default)
        {
            if (commit.Event.Event is ArtifactUpdateEvent && Interlocked.Exchange(ref _held, 1) == 0)
                await Task.Delay(300, ct);
            return await base.CommitAsync(commit, ct);
        }

        public async ValueTask<BatchCommitResult> CommitBatchAsync(TaskBatchCommit commit, CancellationToken ct = default)
        {
            var result = await Inner.CommitBatchAsync(commit, ct);
            if (Interlocked.Increment(ref _batches) == 1) throw Make(Mode, commit.Record.Key, commit.ExpectedVersion);
            return result;
        }

        public ValueTask<long?> FindCommitAsync(TaskKey task, string mutationId, CancellationToken ct = default) =>
            Inner.FindCommitAsync(task, mutationId, ct);
    }

    /// <summary>
    /// A store with batch commits but no <see cref="ITaskCommitLookup"/>. The first batch either is applied and then
    /// reported as a version conflict (<paramref name="ownWrite"/>: the store's own retry met its first attempt), or meets
    /// a real conflict: another writer changes the task's status first, and the batch is not applied.
    /// </summary>
    public sealed class NoLookupBatchStore(InMemoryTaskStore inner, bool ownWrite)
        : AmbiguousTaskStore(inner, Failure.Conflict, failingArtifactCommit: 0), IBatchTaskCommit
    {
        private int _held;
        private int _batches;
        public int Batches => _batches;

        public override async ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default)
        {
            if (commit.Event.Event is ArtifactUpdateEvent && Interlocked.Exchange(ref _held, 1) == 0)
                await Task.Delay(300, ct);
            return await base.CommitAsync(commit, ct);
        }

        public async ValueTask<BatchCommitResult> CommitBatchAsync(TaskBatchCommit commit, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _batches) != 1) return await Inner.CommitBatchAsync(commit, ct);
            if (ownWrite)
            {
                await Inner.CommitBatchAsync(commit, ct);
            }
            else
            {
                // Another writer: a status message on the same task, committed first.
                var current = (await Inner.GetAsync(commit.Record.Key, ct))!;
                var task = current.Task.CopyForUpdate();
                task.Status = new AgentTaskStatus { State = TaskState.Working, Message = Message.FromAgent("other writer") };
                await Inner.CommitAsync(new TaskCommit(current with { Task = task }, current.Version,
                    StreamResponse.Of(new TaskStatusUpdateEvent { TaskId = task.Id, ContextId = task.ContextId, Status = task.Status }), []), ct);
            }

            throw new ConcurrencyException(commit.Record.Key, commit.ExpectedVersion);
        }
    }

    private static async Task<(AgentTask Task, List<SequencedEvent> Log)> RunAsync(Func<InMemoryTaskStore, TaskStore> store, string scenario)
    {
        await using var host = await TestAgentHost.StartAsync(services: s =>
            s.AddSingleton<TaskStore>(sp => store(A2AServerServiceCollectionExtensions.InMemoryTaskStore(sp))));
        var sent = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync(scenario)).RequireTask();
        await host.WaitIdleAsync();

        var key = new TaskKey("", "alice", sent.Id);
        var stored = (await host.App.Services.GetRequiredService<TaskStore>().GetAsync(key))!.Task;
        var log = new List<SequencedEvent>();
        await foreach (var e in host.App.Services.GetRequiredService<EventStore>().ReadAfterAsync(key, 0)) log.Add(e);
        return (stored, log);
    }

    private static List<string?> Parts(AgentTask task) =>
        task.Artifacts?.SelectMany(a => a.Parts).OfType<TextPart>().Select(p => (string?)p.Text).ToList() ?? [];

    private static string Describe(AgentTask task, List<SequencedEvent> log) =>
        $"state {task.Status.State}; parts [{string.Join(", ", Parts(task))}]; " +
        $"log: {string.Join(" | ", log.Select(e => $"{e.Sequence}:{e.Event.GetType().Name}"))}";

    [Theory]
    [InlineData(Failure.Timeout, 1)]
    [InlineData(Failure.Conflict, 1)]
    [InlineData(Failure.Timeout, 2)]
    [InlineData(Failure.Conflict, 2)]
    public async Task An_applied_commit_whose_answer_is_lost_stores_each_event_once(Failure failure, int failingArtifactCommit)
    {
        // "task": working, artifact chunk "part1", chunk "part2" (last), completed.
        var (task, log) = await RunAsync(inner => new LookupStore(inner, failure, failingArtifactCommit), "task");

        var summary = Describe(task, log);
        Assert.True(Parts(task).SequenceEqual(["part1", "part2"]), summary);
        Assert.True(log.Count(e => e.Event is ArtifactUpdateEvent) == 2, summary);
        Assert.True(task.Status.State == TaskState.Completed, summary);
    }

    private static readonly IEnumerable<string?> ManyChunkParts =
        Enumerable.Range(0, 21).Select(i => (string?)i.ToString(CultureInfo.InvariantCulture)).ToList();

    [Theory]
    [InlineData(Failure.Timeout)]
    [InlineData(Failure.Conflict)]
    public async Task An_applied_middle_append_whose_answer_is_lost_stores_the_chunk_once(Failure failure)
    {
        // "many-chunks": chunk "0" creates the artifact, chunks "1" to "20" append, an empty chunk ends it. Commit 2 is
        // chunk "1": an append that is not the last chunk, so the artifact is still open and would take the chunk again.
        var (task, log) = await RunAsync(inner => new LookupStore(inner, failure, failingArtifactCommit: 2), "many-chunks");

        var summary = Describe(task, log);
        Assert.True(Parts(task).SequenceEqual(ManyChunkParts), summary);
        Assert.True(log.Count(e => e.Event is ArtifactUpdateEvent) == 22, summary);
        Assert.True(task.Status.State == TaskState.Completed, summary);
    }

    [Fact]
    public async Task Without_the_lookup_a_conflict_on_a_middle_append_stores_the_chunk_once()
    {
        // The store applied chunk "1" and then reported a conflict for its own write. Without ITaskCommitLookup the engine
        // sees the task one version past the one it wrote, holding exactly its artifact, and keeps the append as stored
        // instead of applying it again on the open artifact.
        var (task, log) = await RunAsync(inner => new AmbiguousTaskStore(inner, Failure.Conflict, failingArtifactCommit: 2), "many-chunks");

        var summary = Describe(task, log);
        Assert.True(Parts(task).SequenceEqual(ManyChunkParts), summary);
        Assert.True(log.Count(e => e.Event is ArtifactUpdateEvent) == 22, summary);
        Assert.True(log.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(1, log.Count).Select(i => (long)i)), summary);
        Assert.True(task.Status.State == TaskState.Completed, summary);
    }

    /// <summary>
    /// Without the lookup: at the <paramref name="failingArtifactCommit"/>th artifact commit, another writer changes the task
    /// first (it adds task metadata), so the commit meets a real version conflict and is not stored.
    /// </summary>
    public sealed class OtherWriterStore(InMemoryTaskStore inner, int failingArtifactCommit,
        Func<StreamResponse, bool>? counted = null)
        : AmbiguousTaskStore(inner, Failure.Conflict, failingArtifactCommit: 0)
    {
        private readonly Func<StreamResponse, bool> _counted = counted ?? IsArtifact;
        private int _artifactCommits;

        public override async ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default)
        {
            if (_counted(commit.Event.Event) && Interlocked.Increment(ref _artifactCommits) == failingArtifactCommit)
            {
                var current = (await Inner.GetAsync(commit.Record.Key, ct))!;
                var changed = current.Task.CopyForUpdate();
                changed.Metadata = new() { ["writer"] = System.Text.Json.JsonSerializer.SerializeToElement("other") };
                await Inner.UpdateAsync(current with { Task = changed }, current.Version, ct);
                throw new ConcurrencyException(commit.Record.Key, commit.ExpectedVersion);
            }

            return await Inner.CommitAsync(commit, ct);
        }
    }

    [Fact]
    public async Task Without_the_lookup_a_real_conflict_on_a_middle_append_still_applies_the_chunk()
    {
        // The stored task is one version past the one the engine wrote, but its artifact lacks chunk "1": the conflict is
        // another writer's, so the engine reloads and applies the chunk on top of that writer's change.
        var (task, log) = await RunAsync(inner => new OtherWriterStore(inner, failingArtifactCommit: 2), "many-chunks");

        var summary = Describe(task, log);
        Assert.True(Parts(task).SequenceEqual(ManyChunkParts), summary);
        Assert.True(log.Count(e => e.Event is ArtifactUpdateEvent) == 22, summary);
        Assert.True(task.Metadata?.ContainsKey("writer") == true, "the other writer's change was lost: " + summary);
        Assert.True(task.Status.State == TaskState.Completed, summary);
    }

    [Fact]
    public async Task Without_the_lookup_a_lost_answer_on_a_middle_append_fails_the_run()
    {
        // As for the first chunk: the engine cannot tell whether the commit was stored, so it fails the run. What was
        // stored stays: the chunk whose answer was lost is in the task once.
        var (task, log) = await RunAsync(inner => new AmbiguousTaskStore(inner, Failure.Timeout, failingArtifactCommit: 2), "many-chunks");

        var summary = Describe(task, log);
        Assert.True(task.Status.State == TaskState.Failed, summary);
        Assert.True(Parts(task).SequenceEqual(ManyChunkParts.Take(2)), summary);
    }

    private static string Kinds(List<SequencedEvent> log) => string.Join(",", log.Select(e => e.Event.GetType().Name));

    [Fact]
    public async Task Without_the_lookup_a_conflict_on_the_chunk_that_creates_the_artifact_stores_it_once()
    {
        // "task", commit 1 is chunk "part1" (append false): not an append, but its event was stored twice before.
        var (task, log) = await RunAsync(inner => new AmbiguousTaskStore(inner, Failure.Conflict, failingArtifactCommit: 1), "task");

        var summary = Describe(task, log);
        Assert.True(Parts(task).SequenceEqual(["part1", "part2"]), summary);
        Assert.True(Kinds(log) == "TaskEvent,StatusUpdateEvent,ArtifactUpdateEvent,ArtifactUpdateEvent,StatusUpdateEvent", summary);
        Assert.True(log.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(1, log.Count).Select(i => (long)i)), summary);
        Assert.True(task.Status.State == TaskState.Completed, summary);
    }

    [Theory]
    [InlineData(1)] // "working"
    [InlineData(2)] // "done": the terminal state
    public async Task Without_the_lookup_a_conflict_on_a_status_update_stores_it_once(int failingStatusCommit)
    {
        var (task, log) = await RunAsync(inner => new AmbiguousTaskStore(inner, Failure.Conflict, failingStatusCommit, IsStatus), "task");

        var summary = Describe(task, log);
        Assert.True(Kinds(log) == "TaskEvent,StatusUpdateEvent,ArtifactUpdateEvent,ArtifactUpdateEvent,StatusUpdateEvent", summary);
        Assert.True(log.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(1, log.Count).Select(i => (long)i)), summary);
        Assert.True(Parts(task).SequenceEqual(["part1", "part2"]), summary);
        Assert.True(task.Status.State == TaskState.Completed, summary);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Without_the_lookup_a_real_conflict_on_a_status_update_still_applies_it(int failingStatusCommit)
    {
        var (task, log) = await RunAsync(inner => new OtherWriterStore(inner, failingStatusCommit, IsStatus), "task");

        var summary = Describe(task, log);
        Assert.True(Kinds(log) == "TaskEvent,StatusUpdateEvent,ArtifactUpdateEvent,ArtifactUpdateEvent,StatusUpdateEvent", summary);
        Assert.True(task.Metadata?.ContainsKey("writer") == true, "the other writer's change was lost: " + summary);
        Assert.True(task.Status.State == TaskState.Completed, summary);
    }

    [Theory]
    [InlineData(Failure.Timeout)]
    [InlineData(Failure.Conflict)]
    public async Task An_applied_batch_whose_answer_is_lost_stores_each_chunk_once(Failure failure)
    {
        BatchStore? store = null;
        var (task, log) = await RunAsync(inner => store = new BatchStore(inner, failure), "many-chunks");

        var summary = Describe(task, log);
        Assert.True(store!.Batches >= 1, "no batch commit happened: " + summary);
        var expected = Enumerable.Range(0, 21).Select(i => (string?)i.ToString(CultureInfo.InvariantCulture));
        Assert.True(Parts(task).SequenceEqual(expected), summary);
        Assert.True(log.Count(e => e.Event is ArtifactUpdateEvent) == 22, summary);
        Assert.True(task.Status.State == TaskState.Completed, summary);
    }

    [Fact]
    public async Task Without_the_lookup_a_batch_conflict_caused_by_the_stores_own_write_stores_each_chunk_once()
    {
        NoLookupBatchStore? store = null;
        var (task, log) = await RunAsync(inner => store = new NoLookupBatchStore(inner, ownWrite: true), "many-chunks");

        var summary = Describe(task, log);
        Assert.True(store!.Batches >= 1, "no batch commit happened: " + summary);
        var expected = Enumerable.Range(0, 21).Select(i => (string?)i.ToString(CultureInfo.InvariantCulture));
        Assert.True(Parts(task).SequenceEqual(expected), summary);
        Assert.True(log.Count(e => e.Event is ArtifactUpdateEvent) == 22, summary);
        Assert.True(task.Status.State == TaskState.Completed, summary);
    }

    [Fact]
    public async Task Without_the_lookup_a_real_batch_conflict_still_stores_every_chunk()
    {
        NoLookupBatchStore? store = null;
        var (task, log) = await RunAsync(inner => store = new NoLookupBatchStore(inner, ownWrite: false), "many-chunks");

        var summary = Describe(task, log);
        Assert.True(store!.Batches >= 1, "no batch commit happened: " + summary);
        var expected = Enumerable.Range(0, 21).Select(i => (string?)i.ToString(CultureInfo.InvariantCulture));
        Assert.True(Parts(task).SequenceEqual(expected), summary);
        Assert.True(log.Count(e => e.Event is ArtifactUpdateEvent) == 22, summary);
        Assert.True(task.Status.State == TaskState.Completed, summary);
    }

    [Fact]
    public async Task Without_the_lookup_a_lost_answer_still_fails_the_run_as_before()
    {
        // A store that cannot tell whether the commit was stored: the engine does not guess, and fails the run.
        var (task, log) = await RunAsync(inner => new AmbiguousTaskStore(inner, Failure.Timeout, 1), "task");

        Assert.True(task.Status.State == TaskState.Failed, Describe(task, log));
    }

    [Fact]
    public async Task The_in_memory_store_finds_a_commit_by_its_mutation_id_and_forgets_it_with_the_task()
    {
        var events = new InMemoryEventStore(new StreamingOptions());
        var store = new InMemoryTaskStore(events);
        var key = new TaskKey("", "alice", "t1");
        var now = DateTimeOffset.UtcNow;
        var record = new TaskRecord("", "alice",
            new AgentTask { Id = "t1", ContextId = "c1", Status = new AgentTaskStatus { State = TaskState.Submitted } }, 0, now, now);

        var created = await store.CommitAsync(new TaskCommit(record, 0, StreamResponse.Of(record.Task), []) { MutationId = "m1" });
        var working = StreamResponse.Of(new TaskStatusUpdateEvent { TaskId = "t1", ContextId = "c1", Status = new AgentTaskStatus { State = TaskState.Working } });
        var updated = await store.CommitAsync(new TaskCommit(created.Record, created.Record.Version, working, []) { MutationId = "m2" });

        Assert.Equal(created.Sequence, await store.FindCommitAsync(key, "m1"));
        Assert.Equal(updated.Sequence, await store.FindCommitAsync(key, "m2"));
        Assert.Null(await store.FindCommitAsync(key, "unknown"));
        Assert.Null(await store.FindCommitAsync(key with { Owner = "mallory" }, "m1")); // another owner's task

        // Only the last RecentMutationCount ids are kept.
        var last = updated;
        for (var i = 0; i < InMemoryTaskStore.RecentMutationCount; i++)
            last = await store.CommitAsync(new TaskCommit(last.Record, last.Record.Version, working, []) { MutationId = $"n{i}" });
        Assert.Null(await store.FindCommitAsync(key, "m2"));
        Assert.Equal(last.Sequence, await store.FindCommitAsync(key, $"n{InMemoryTaskStore.RecentMutationCount - 1}"));

        Assert.True(await store.DeleteAsync(key));
        Assert.Null(await store.FindCommitAsync(key, $"n{InMemoryTaskStore.RecentMutationCount - 1}"));
    }
}
