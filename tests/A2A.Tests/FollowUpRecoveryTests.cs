using System.Runtime.CompilerServices;
using System.Security.Claims;
using A2A.Server;
using A2A.Server.Coordination;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests;

/// <summary>
/// A follow-up's message is committed before its execution starts. When the process dies (or the start and the failing of the
/// task both fail) in between, the client's retry of the same message id starts the agent, once, and only when it never ran.
/// </summary>
public sealed class FollowUpRecoveryTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static ServerCallContext Call() => new()
    {
        Principal = "alice",
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "Test")),
    };

    /// <summary>The system clock, whose timers fail while <see cref="Fail"/> is set: the execution cannot start.</summary>
    private sealed class FailingTimers : TimeProvider
    {
        public volatile bool Fail;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Fail ? throw new InvalidOperationException("timer unavailable") : System.CreateTimer(callback, state, dueTime, period);
    }

    /// <summary>
    /// A task store whose commits fail while <see cref="FailCommits"/> is set: the task cannot be failed. The commit of a
    /// follow-up's message (its message update event) still goes through: the crash comes after it.
    /// </summary>
    private sealed class FaultyTaskStore(TaskStore inner) : TaskStore
    {
        public volatile bool FailCommits;

        public override ValueTask<TaskRecord?> GetAsync(TaskKey key, CancellationToken ct = default) => inner.GetAsync(key, ct);
        public override ValueTask<TaskRecord> CreateAsync(TaskRecord record, CancellationToken ct = default) => inner.CreateAsync(record, ct);
        public override ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion, CancellationToken ct = default) => inner.UpdateAsync(record, expectedVersion, ct);
        public override ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default) =>
            FailCommits && commit.Event.Event is not MessageUpdateEvent ? throw new InvalidOperationException("store unavailable") : inner.CommitAsync(commit, ct);
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

    private sealed class Fixture : IAsyncDisposable
    {
        public required FailingTimers Clock { get; init; }
        public required FaultyTaskStore Store { get; init; }
        public int FollowUpsRun;
        public volatile string? SeenText;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public required TestAgentHost Host { get; init; }
        public ValueTask DisposeAsync() => Host.DisposeAsync();

        /// <param name="followUp">What the agent does for a follow-up.</param>
        /// <param name="coordinator">Wraps the coordinator, or null for the default one.</param>
        public static async Task<Fixture> StartAsync(Func<Fixture, TaskUpdater, CancellationToken, Task> followUp,
            Func<ExecutionCoordinator, ExecutionCoordinator>? coordinator = null)
        {
            Fixture? self = null;
            FaultyTaskStore? store = null;
            var clock = new FailingTimers();
            var host = await TestAgentHost.StartAsync(services: s =>
            {
                s.AddSingleton<TimeProvider>(clock);
                if (coordinator is not null)
                    s.AddSingleton(coordinator(new InMemoryExecutionCoordinator(null, clock)));
                s.Replace(ServiceDescriptor.Singleton<TaskStore>(sp => store = new FaultyTaskStore(A2AServerServiceCollectionExtensions.InMemoryTaskStore(sp))));
                s.AddSingleton<IAgentExecutor>(new DelegateAgentExecutor(async (context, u, ct) =>
                {
                    if (context.IsNewTask) { await u.RequireInputAsync("more?", ct); return; }
                    Interlocked.Increment(ref self!.FollowUpsRun);
                    self.SeenText = context.UserText;
                    await followUp(self, u, ct);
                }));
            });
            _ = host.App.Services.GetRequiredService<TaskStore>(); // built now, so the fixture holds it
            self = new Fixture { Host = host, Clock = clock, Store = store! };
            return self;
        }
    }

    /// <summary>
    /// Holds the unregister of the first execution registered until <see cref="Release"/>: that execution stays registered
    /// while it ends. Other executions are not held.
    /// </summary>
    private sealed class SlowFirstUnregister(ExecutionCoordinator inner) : ExecutionCoordinator
    {
        private string? _first;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask RegisterAsync(TaskKey task, string executionId, CancellationToken ct = default)
        {
            Interlocked.CompareExchange(ref _first, executionId, null);
            return inner.RegisterAsync(task, executionId, ct);
        }
        public override ValueTask<bool> HeartbeatAsync(TaskKey task, string executionId, CancellationToken ct = default) => inner.HeartbeatAsync(task, executionId, ct);
        public override async ValueTask UnregisterAsync(TaskKey task, string executionId, CancellationToken ct = default)
        {
            if (executionId == Volatile.Read(ref _first)) await Release.Task.WaitAsync(ct);
            await inner.UnregisterAsync(task, executionId, ct);
        }
        public override ValueTask<bool> IsRunningAsync(TaskKey task, CancellationToken ct = default) => inner.IsRunningAsync(task, ct);
        public override ValueTask RequestCancelAsync(TaskKey task, CancellationToken ct = default) => inner.RequestCancelAsync(task, ct);
        public override async IAsyncEnumerable<TaskKey> CancelRequestsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var key in inner.CancelRequestsAsync(ct)) yield return key;
        }
        public override ValueTask<IReadOnlyList<(TaskKey Task, string ExecutionId)>> ListExpiredAsync(int max, CancellationToken ct = default) => inner.ListExpiredAsync(max, ct);
        public override ValueTask<bool> TryAcquireSweepLeaseAsync(string name, TimeSpan duration, CancellationToken ct = default) => inner.TryAcquireSweepLeaseAsync(name, duration, ct);
    }

    private static Task<Fixture> StartCompletingAsync() => Fixture.StartAsync((_, u, ct) => u.CompleteAsync("done", ct).AsTask());

    private static async Task<AgentTask> NewTaskAsync(Fixture f) =>
        (await f.Host.Handler.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("go") }, Call()).WaitAsync(Wait)).RequireTask();

    private static Task<SendMessageResponse> SendAsync(Fixture f, Message message, bool returnImmediately = false) =>
        f.Host.Handler.SendMessageAsync(new SendMessageRequest
        {
            Message = message,
            Configuration = returnImmediately ? new SendMessageConfiguration { ReturnImmediately = true } : null,
        }, Call()).WaitAsync(Wait);

    private static Task<AgentTask> GetAsync(Fixture f, string id) => f.Host.Handler.GetTaskAsync(new GetTaskRequest { Id = id }, Call());

    /// <summary>Sends the follow-up while it cannot start and the task cannot be failed: the message is committed, nothing runs.</summary>
    private static async Task CrashAfterCommitAsync(Fixture f, Message followUp)
    {
        f.Clock.Fail = true;
        f.Store.FailCommits = true;
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(f, followUp));
        }
        finally
        {
            f.Clock.Fail = false;
            f.Store.FailCommits = false;
        }
    }

    [Fact]
    public async Task Retry_after_a_crash_between_commit_and_start_runs_the_agent_once()
    {
        await using var f = await StartCompletingAsync();
        var first = await NewTaskAsync(f);
        var followUp = Message.FromUser("more", taskId: first.Id);

        await CrashAfterCommitAsync(f, followUp);
        var stuck = await GetAsync(f, first.Id);
        Assert.Equal(TaskState.InputRequired, stuck.Status.State); // the message is accepted, nothing runs
        Assert.Contains(stuck.History!, m => m.MessageId == followUp.MessageId);
        Assert.Equal(0, Volatile.Read(ref f.FollowUpsRun));

        var retry = (await SendAsync(f, followUp)).RequireTask();

        Assert.Equal(TaskState.Completed, retry.Status.State);
        await f.Host.WaitIdleAsync();
        Assert.Equal(1, Volatile.Read(ref f.FollowUpsRun));
    }

    [Fact]
    public async Task Retry_while_the_earlier_run_is_still_ending_waits_for_it_and_runs_the_agent_once()
    {
        // The run that asked for input has returned, but has not ended yet (its lease is still registered, as under load).
        // It cannot take the committed message: the retry waits for it to end, then starts the message. It used to count it
        // as the recovered run, so the retry was a duplicate and the task stayed INPUT_REQUIRED with the message unanswered.
        SlowFirstUnregister? slow = null;
        await using var f = await Fixture.StartAsync((_, u, ct) => u.CompleteAsync("done", ct).AsTask(),
            inner => slow = new SlowFirstUnregister(inner));
        var first = await NewTaskAsync(f);
        var followUp = Message.FromUser("more", taskId: first.Id);
        await CrashAfterCommitAsync(f, followUp);
        Assert.True(f.Host.Engine.RunningCompletions(new TaskKey("", "alice", first.Id)).Count == 1); // still ending

        var retry = SendAsync(f, followUp);
        _ = await Task.WhenAny(retry, Task.Delay(300));
        slow!.Release.SetResult();

        Assert.Equal(TaskState.Completed, (await retry).RequireTask().Status.State);
        await f.Host.WaitIdleAsync();
        Assert.Equal(1, Volatile.Read(ref f.FollowUpsRun));
    }

    [Fact]
    public async Task Recovery_runs_the_committed_message_not_the_retrys_text()
    {
        await using var f = await StartCompletingAsync();
        var first = await NewTaskAsync(f);
        var followUp = Message.FromUser("original", taskId: first.Id);
        await CrashAfterCommitAsync(f, followUp);

        await SendAsync(f, new Message { MessageId = followUp.MessageId, Role = Role.User, Parts = [Part.FromText("changed")], TaskId = first.Id });

        await f.Host.WaitIdleAsync();
        Assert.Equal(1, Volatile.Read(ref f.FollowUpsRun));
        Assert.Equal("original", f.SeenText);
    }

    [Fact]
    public async Task Retry_after_the_agent_completed_does_not_rerun()
    {
        await using var f = await StartCompletingAsync();
        var first = await NewTaskAsync(f);
        var followUp = Message.FromUser("more", taskId: first.Id);
        await CrashAfterCommitAsync(f, followUp);
        await SendAsync(f, followUp);
        await f.Host.WaitIdleAsync();

        var again = (await SendAsync(f, followUp)).RequireTask();

        Assert.Equal(TaskState.Completed, again.Status.State);
        await f.Host.WaitIdleAsync();
        Assert.Equal(1, Volatile.Read(ref f.FollowUpsRun));
    }

    [Fact]
    public async Task Retry_after_the_agent_ran_and_only_added_an_artifact_does_not_rerun()
    {
        // The task stays INPUT_REQUIRED and the message stays last in the history: only the event store shows that the agent ran.
        await using var f = await Fixture.StartAsync((_, u, ct) => u.AddArtifactAsync([Part.FromText("partial")], name: "a", cancellationToken: ct).AsTask());
        var first = await NewTaskAsync(f);
        var followUp = Message.FromUser("more", taskId: first.Id);
        await SendAsync(f, followUp);
        await f.Host.WaitIdleAsync();
        Assert.Equal(1, Volatile.Read(ref f.FollowUpsRun));

        await SendAsync(f, followUp);

        await f.Host.WaitIdleAsync();
        Assert.Equal(1, Volatile.Read(ref f.FollowUpsRun));
    }

    [Fact]
    public async Task Retry_while_the_agent_runs_does_not_rerun()
    {
        await using var f = await Fixture.StartAsync(async (fx, u, ct) =>
        {
            await u.StartWorkAsync("working", ct);
            await fx.Release.Task.WaitAsync(ct);
            await u.CompleteAsync("done", CancellationToken.None);
        });
        var first = await NewTaskAsync(f);
        var followUp = Message.FromUser("more", taskId: first.Id);
        await SendAsync(f, followUp, returnImmediately: true);

        await SendAsync(f, followUp, returnImmediately: true);

        f.Release.SetResult();
        await f.Host.WaitIdleAsync();
        Assert.Equal(1, Volatile.Read(ref f.FollowUpsRun));
    }

    [Fact]
    public async Task Retry_while_the_recovered_run_is_going_does_not_rerun()
    {
        await using var f = await Fixture.StartAsync(async (fx, u, ct) =>
        {
            await u.StartWorkAsync("working", ct);
            await fx.Release.Task.WaitAsync(ct);
            await u.CompleteAsync("done", CancellationToken.None);
        });
        var first = await NewTaskAsync(f);
        var followUp = Message.FromUser("more", taskId: first.Id);
        await CrashAfterCommitAsync(f, followUp);
        await SendAsync(f, followUp, returnImmediately: true); // recovers: the agent runs

        await SendAsync(f, followUp, returnImmediately: true);

        f.Release.SetResult();
        await f.Host.WaitIdleAsync();
        Assert.Equal(1, Volatile.Read(ref f.FollowUpsRun));
    }

    [Fact]
    public async Task Two_concurrent_retries_run_the_agent_once()
    {
        await using var f = await Fixture.StartAsync(async (fx, u, ct) =>
        {
            await u.StartWorkAsync("working", ct);
            await fx.Release.Task.WaitAsync(ct);
            await u.CompleteAsync("done", CancellationToken.None);
        });
        var first = await NewTaskAsync(f);
        var followUp = Message.FromUser("more", taskId: first.Id);
        await CrashAfterCommitAsync(f, followUp);

        await Task.WhenAll(SendAsync(f, followUp, returnImmediately: true), SendAsync(f, followUp, returnImmediately: true));

        f.Release.SetResult();
        await f.Host.WaitIdleAsync();
        Assert.Equal(1, Volatile.Read(ref f.FollowUpsRun));
        Assert.Equal(TaskState.Completed, (await GetAsync(f, first.Id)).Status.State);
    }

    [Fact]
    public async Task A_follow_up_that_starts_normally_is_not_started_again_by_a_retry_after_it_stopped_with_events()
    {
        await using var f = await StartCompletingAsync();
        var first = await NewTaskAsync(f);
        var followUp = Message.FromUser("more", taskId: first.Id);

        var one = (await SendAsync(f, followUp)).RequireTask();
        var two = (await SendAsync(f, followUp)).RequireTask();

        Assert.Equal(TaskState.Completed, one.Status.State);
        Assert.Equal(TaskState.Completed, two.Status.State);
        Assert.Equal(1, Volatile.Read(ref f.FollowUpsRun));
    }
}
