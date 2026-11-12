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
    /// Wraps the in-memory store. The <paramref name="failingArtifactCommit"/>th single commit of an artifact event is
    /// applied, and then the call fails as <paramref name="failure"/> says. Every other call goes straight through.
    /// </summary>
    public class AmbiguousTaskStore(InMemoryTaskStore inner, Failure failure, int failingArtifactCommit) : TaskStore
    {
        private int _artifactCommits;
        protected InMemoryTaskStore Inner => inner;
        protected Failure Mode => failure;

        public override async ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default)
        {
            var result = await inner.CommitAsync(commit, ct);
            if (commit.Event.Event is ArtifactUpdateEvent && Interlocked.Increment(ref _artifactCommits) == failingArtifactCommit)
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
