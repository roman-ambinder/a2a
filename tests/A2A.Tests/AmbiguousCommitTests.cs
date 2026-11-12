using A2A.Server;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>
/// D2 (ADR-002): a commit that the store applied, but whose caller saw an error. This happens when a database commits the
/// transaction and the connection then times out or resets before the answer arrives, or when a store retries a write
/// internally and the retry meets its own first attempt. The task and its event log must still hold each event once.
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

    /// <summary>
    /// Wraps the in-memory store. The first commit of an artifact event is applied, and then the call fails as
    /// <paramref name="mode"/> says. Every other call goes straight through.
    /// </summary>
    /// <param name="failingArtifactCommit">Which artifact commit fails: 1 creates the artifact ("part1"), 2 appends the last chunk ("part2").</param>
    public sealed class AmbiguousTaskStore(InMemoryTaskStore inner, Failure mode, int failingArtifactCommit) : TaskStore
    {
        private int _artifactCommits;

        public override async ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default)
        {
            var result = await inner.CommitAsync(commit, ct);
            if (commit.Event.Event is ArtifactUpdateEvent && Interlocked.Increment(ref _artifactCommits) == failingArtifactCommit)
                throw mode == Failure.Timeout
                    ? new TimeoutException("The commit was applied, but its answer was lost.")
                    : new ConcurrencyException(commit.Record.Key, commit.ExpectedVersion);
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

    [Theory]
    [InlineData(Failure.Timeout, 1)]
    [InlineData(Failure.Conflict, 1)]
    [InlineData(Failure.Timeout, 2)]
    [InlineData(Failure.Conflict, 2)]
    public async Task An_applied_commit_whose_answer_is_lost_stores_each_event_once(Failure failure, int failingArtifactCommit)
    {
        await using var host = await TestAgentHost.StartAsync(services: s =>
            s.AddSingleton<TaskStore>(sp => new AmbiguousTaskStore(A2AServerServiceCollectionExtensions.InMemoryTaskStore(sp), failure, failingArtifactCommit)));
        var client = host.Client(ProtocolBindings.JsonRpc);

        // "task": working, artifact chunk "part1", chunk "part2" (last), completed.
        var sent = (await client.SendMessageAsync("task")).RequireTask();
        await host.WaitIdleAsync();

        var key = new TaskKey("", "alice", sent.Id);
        var stored = (await host.App.Services.GetRequiredService<TaskStore>().GetAsync(key))!.Task;
        var log = new List<SequencedEvent>();
        await foreach (var e in host.App.Services.GetRequiredService<EventStore>().ReadAfterAsync(key, 0)) log.Add(e);

        var artifactEvents = log.Count(e => e.Event is ArtifactUpdateEvent);
        var parts = stored.Artifacts?.SelectMany(a => a.Parts).OfType<TextPart>().Select(p => p.Text).ToList() ?? [];
        var summary = $"state {stored.Status.State}; artifact events in log {artifactEvents}; parts [{string.Join(", ", parts)}]; " +
                      $"log: {string.Join(" | ", log.Select(e => $"{e.Sequence}:{e.Event.GetType().Name}"))}";

        Assert.True(parts.SequenceEqual(["part1", "part2"]), summary);
        Assert.True(artifactEvents == 2, summary);
        Assert.True(stored.Status.State == TaskState.Completed, summary);
    }
}
