using A2A.Server;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>
/// The commit that creates a task (ADR-002, the case D2 left open): the store writes it, and then the call fails. The
/// answer was lost (a timeout), or the store retried on its own and met its first attempt, which it reports as a
/// duplicate task. With <see cref="ITaskCommitLookup"/>, the engine finds the stored create by its mutation id and
/// carries on: the run is not failed, and the inline push config the request registered is kept.
/// </summary>
public class CreateCommitLostAnswerTests
{
    public enum Failure
    {
        /// <summary>The answer is lost: the caller sees a timeout.</summary>
        Timeout,

        /// <summary>The store's own retry met the first attempt: the caller sees a duplicate task.</summary>
        Duplicate,
    }

    /// <summary>Wraps the in-memory store: the create commit is written, then the call fails as <paramref name="failure"/> says.</summary>
    public class CreateFailsStore(InMemoryTaskStore inner, Failure failure) : TaskStore
    {
        private int _failed;
        protected InMemoryTaskStore Inner => inner;

        public override async ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default)
        {
            var result = await inner.CommitAsync(commit, ct);
            if (commit.IsCreate && Interlocked.Exchange(ref _failed, 1) == 0)
                throw failure == Failure.Timeout
                    ? new TimeoutException("The create was written, but its answer was lost.")
                    : new DuplicateTaskException(commit.Record.Key);
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

    /// <summary>The same, able to tell whether a commit was stored.</summary>
    public sealed class CreateFailsLookupStore(InMemoryTaskStore inner, Failure failure)
        : CreateFailsStore(inner, failure), ITaskCommitLookup
    {
        public ValueTask<long?> FindCommitAsync(TaskKey task, string mutationId, CancellationToken ct = default) =>
            Inner.FindCommitAsync(task, mutationId, ct);
    }

    private sealed record Outcome(SendMessageResponse? Response, Exception? Error, AgentTask? Stored, List<SequencedEvent> Log,
        IReadOnlyList<PushConfigRecord> Configs, int Webhooks, IReadOnlyList<AgentTask> AllTasks);

    private static async Task<Outcome> RunAsync(Func<InMemoryTaskStore, TaskStore> store)
    {
        await using var host = await TestAgentHost.StartAsync(services: s =>
            s.AddSingleton<TaskStore>(sp => store(A2AServerServiceCollectionExtensions.InMemoryTaskStore(sp))));
        var message = Message.FromUser("task");
        message.TaskId = null;
        var config = new SendMessageConfiguration
        {
            TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = "http://hooks.test/create", Token = "t" },
        };
        SendMessageResponse? response = null;
        Exception? error = null;
        try
        {
            response = await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync(message, config);
        }
        catch (A2AException ex)
        {
            error = ex;
        }

        await host.WaitIdleAsync();
        var tasks = host.App.Services.GetRequiredService<TaskStore>();
        var page = await tasks.ListAsync(new TaskQuery("", "alice", null, null, null, 10, null));
        var all = page.Records.Select(r => r.Task).ToList();
        var stored = all.Count == 1 ? all[0] : null;
        var log = new List<SequencedEvent>();
        IReadOnlyList<PushConfigRecord> configs = [];
        if (stored is not null)
        {
            var key = new TaskKey("", "alice", stored.Id);
            await foreach (var e in host.App.Services.GetRequiredService<EventStore>().ReadAfterAsync(key, 0)) log.Add(e);
            configs = await host.App.Services.GetRequiredService<PushConfigStore>().ListAsync(key);
        }

        var webhooks = host.Webhooks.Calls.Count(c => c.Request.RequestUri!.AbsolutePath == "/create");
        return new Outcome(response, error, stored, log, configs, webhooks, all);
    }

    private static string Describe(Outcome o) =>
        $"response {o.Response?.GetType().Name ?? "none"}; error {o.Error?.Message ?? "none"}; state {o.Stored?.Status.State.ToString() ?? "no task"}; " +
        $"tasks [{string.Join(", ", o.AllTasks.Select(t => t.Status.State))}]; configs {o.Configs.Count}; webhooks {o.Webhooks}; log: {string.Join(" | ", o.Log.Select(e => $"{e.Sequence}:{e.Event.GetType().Name}"))}";

    [Theory]
    [InlineData(Failure.Timeout)]
    [InlineData(Failure.Duplicate)]
    public async Task A_create_whose_answer_is_lost_carries_on_and_keeps_the_push_config(Failure failure)
    {
        var o = await RunAsync(inner => new CreateFailsLookupStore(inner, failure));

        var summary = Describe(o);
        Assert.True(o.Error is null, summary);
        Assert.True(o.Stored?.Status.State == TaskState.Completed, summary);
        Assert.True(o.Log.Count(e => e.Event is TaskEvent) == 1 && o.Log[0].Sequence == 1, summary);
        Assert.True(o.Configs.Count == 1, summary);
        // Every stored event was delivered to the webhook, the snapshot included.
        Assert.True(o.Webhooks == o.Log.Count, summary);
    }

    [Fact]
    public async Task Without_the_lookup_a_create_whose_answer_is_lost_is_treated_as_failed_as_before()
    {
        // A store that cannot tell whether the create was stored: the engine does not guess, so the stored task is failed.
        var o = await RunAsync(inner => new CreateFailsStore(inner, Failure.Timeout));

        Assert.True(o.AllTasks.Any(t => t.Status.State == TaskState.Failed), Describe(o));
    }
}
