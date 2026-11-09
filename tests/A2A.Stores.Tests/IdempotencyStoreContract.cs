using A2A.Server;
using A2A.Server.Idempotency;
using A2A.Server.Postgres;
using A2A.Server.Storage;
using A2A.Tests;

namespace A2A.Stores.Tests;

/// <summary>Behavior every <see cref="IdempotencyStore"/> must have.</summary>
public abstract class IdempotencyStoreContract
{
    protected abstract Task<IdempotencyStore> CreateStoreAsync(IdempotencyOptions options, TimeProvider? time = null);

    protected static readonly IdempotencyScope Scope = new("t", "alice", "key-1");
    protected static readonly byte[] Hash = [1, 2, 3, 4];
    protected static readonly byte[] OtherHash = [9, 9, 9, 9];

    /// <summary>Claims <paramref name="scope"/> (default <see cref="Scope"/>), asserts it is new and returns its claim id.</summary>
    protected static async Task<Guid> BeginAsync(IdempotencyStore s, IdempotencyScope? scope = null, byte[]? hash = null) =>
        Assert.IsType<IdempotencyResult.New>(await s.TryBeginAsync(scope ?? Scope, hash ?? Hash)).ClaimId;

    [SkippableFact]
    public async Task First_claim_is_new_and_repeat_is_in_progress()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        var claim = await BeginAsync(s);
        var again = Assert.IsType<IdempotencyResult.InProgress>(await s.TryBeginAsync(Scope, Hash));
        Assert.False(again.Completion.IsCompleted);
        await s.AbandonAsync(Scope, claim);
    }

    [SkippableFact]
    public async Task Different_hash_is_a_mismatch_while_running_and_after()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        var claim = await BeginAsync(s);
        Assert.IsType<IdempotencyResult.Mismatch>(await s.TryBeginAsync(Scope, OtherHash));
        await s.CompleteAsync(Scope, claim, new IdempotencyOutcome.TaskCreated("task-1"));
        Assert.IsType<IdempotencyResult.Mismatch>(await s.TryBeginAsync(Scope, OtherHash));
    }

    [SkippableFact]
    public async Task Completed_request_returns_its_task_outcome()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        var claim = await BeginAsync(s);
        await s.CompleteAsync(Scope, claim, new IdempotencyOutcome.TaskCreated("task-1", 7));
        var done = Assert.IsType<IdempotencyResult.Completed>(await s.TryBeginAsync(Scope, Hash));
        Assert.Equal(new IdempotencyOutcome.TaskCreated("task-1", 7), done.Outcome);
    }

    [SkippableFact]
    public async Task Completed_follow_up_keeps_its_accepted_status_timestamp()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        var claim = await BeginAsync(s);
        var at = new DateTimeOffset(2026, 11, 8, 10, 30, 15, 123, TimeSpan.Zero);
        await s.CompleteAsync(Scope, claim, new IdempotencyOutcome.TaskCreated("task-1", 7, at));
        var done = Assert.IsType<IdempotencyResult.Completed>(await s.TryBeginAsync(Scope, Hash));
        Assert.Equal(new IdempotencyOutcome.TaskCreated("task-1", 7, at), done.Outcome);
    }

    [SkippableFact]
    public async Task Completed_request_returns_its_direct_message()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        var claim = await BeginAsync(s);
        await s.CompleteAsync(Scope, claim, new IdempotencyOutcome.DirectMessage(new Message
        {
            Role = Role.Agent, MessageId = "m1", Parts = [Part.FromText("hi there")],
        }));
        var done = Assert.IsType<IdempotencyResult.Completed>(await s.TryBeginAsync(Scope, Hash));
        var message = Assert.IsType<IdempotencyOutcome.DirectMessage>(done.Outcome).Message;
        Assert.Equal("m1", message.MessageId);
        Assert.Equal("hi there", Assert.IsType<TextPart>(Assert.Single(message.Parts)).Text);
    }

    [SkippableFact]
    public async Task Second_complete_does_not_change_the_outcome()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        var claim = await BeginAsync(s);
        await s.CompleteAsync(Scope, claim, new IdempotencyOutcome.TaskCreated("first"));
        await s.CompleteAsync(Scope, claim, new IdempotencyOutcome.TaskCreated("second"));
        var done = Assert.IsType<IdempotencyResult.Completed>(await s.TryBeginAsync(Scope, Hash));
        Assert.Equal("first", Assert.IsType<IdempotencyOutcome.TaskCreated>(done.Outcome).TaskId);
    }

    [SkippableFact]
    public async Task Waiting_request_gets_the_outcome_when_it_completes()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        var claim = await BeginAsync(s);
        var waiting = Assert.IsType<IdempotencyResult.InProgress>(await s.TryBeginAsync(Scope, Hash));
        await s.CompleteAsync(Scope, claim, new IdempotencyOutcome.TaskCreated("task-1"));
        var outcome = await waiting.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("task-1", Assert.IsType<IdempotencyOutcome.TaskCreated>(outcome).TaskId);
    }

    [SkippableFact]
    public async Task Abandon_cancels_waiters_and_frees_the_key()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        var claim = await BeginAsync(s);
        var waiting = Assert.IsType<IdempotencyResult.InProgress>(await s.TryBeginAsync(Scope, Hash));
        await s.AbandonAsync(Scope, claim);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(waiting.Completion.IsCanceled);
        Assert.IsType<IdempotencyResult.New>(await s.TryBeginAsync(Scope, OtherHash));
    }

    [SkippableFact]
    public async Task Abandon_after_complete_keeps_the_outcome()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        var claim = await BeginAsync(s);
        await s.CompleteAsync(Scope, claim, new IdempotencyOutcome.TaskCreated("task-1"));
        await s.AbandonAsync(Scope, claim);
        Assert.IsType<IdempotencyResult.Completed>(await s.TryBeginAsync(Scope, Hash));
    }

    [SkippableFact]
    public async Task Keys_are_scoped_by_tenant_and_principal()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        var claim = await BeginAsync(s);
        Assert.IsType<IdempotencyResult.New>(await s.TryBeginAsync(Scope with { Principal = "bob" }, OtherHash));
        Assert.IsType<IdempotencyResult.New>(await s.TryBeginAsync(Scope with { Tenant = "u" }, OtherHash));
    }

    [SkippableFact]
    public async Task Finished_entry_expires_after_retention()
    {
        var clock = new FakeTimeProvider();
        var s = await CreateStoreAsync(new IdempotencyOptions { Retention = TimeSpan.FromHours(1) }, clock);
        var claim = await BeginAsync(s);
        await s.CompleteAsync(Scope, claim, new IdempotencyOutcome.TaskCreated("task-1"));

        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.IsType<IdempotencyResult.Completed>(await s.TryBeginAsync(Scope, Hash));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.IsType<IdempotencyResult.New>(await s.TryBeginAsync(Scope, OtherHash));
    }

    [SkippableFact]
    public async Task Concurrent_claims_have_exactly_one_new()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => s.TryBeginAsync(Scope, Hash).AsTask())));
        Assert.Equal(1, results.Count(r => r is IdempotencyResult.New));
        Assert.Equal(15, results.Count(r => r is IdempotencyResult.InProgress));
        await s.AbandonAsync(Scope, results.OfType<IdempotencyResult.New>().Single().ClaimId);
    }

    [SkippableFact]
    public async Task Renew_keeps_a_running_claim_running()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        var claim = await BeginAsync(s);
        await s.RenewAsync(Scope, claim);
        Assert.IsType<IdempotencyResult.InProgress>(await s.TryBeginAsync(Scope, Hash));
        await s.AbandonAsync(Scope, claim);
    }

    [SkippableFact]
    public async Task Renew_of_a_missing_scope_does_nothing()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        await s.RenewAsync(Scope, Guid.NewGuid());
        Assert.IsType<IdempotencyResult.New>(await s.TryBeginAsync(Scope, Hash));
    }

    [SkippableFact]
    public async Task Renew_of_a_completed_scope_does_not_change_it()
    {
        var clock = new FakeTimeProvider();
        var s = await CreateStoreAsync(new IdempotencyOptions { Retention = TimeSpan.FromHours(1) }, clock);
        var claim = await BeginAsync(s);
        await s.CompleteAsync(Scope, claim, new IdempotencyOutcome.TaskCreated("task-1"));
        clock.Advance(TimeSpan.FromMinutes(40));
        await s.RenewAsync(Scope, claim);
        Assert.IsType<IdempotencyResult.Completed>(await s.TryBeginAsync(Scope, Hash));
        clock.Advance(TimeSpan.FromMinutes(30)); // still ends 1 h after completion, not after the renew
        Assert.IsType<IdempotencyResult.New>(await s.TryBeginAsync(Scope, Hash));
    }

    [SkippableFact]
    public async Task Claims_have_different_ids()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        var first = await BeginAsync(s);
        await s.AbandonAsync(Scope, first);
        Assert.NotEqual(first, await BeginAsync(s));
    }

    [SkippableFact]
    public async Task Old_claim_cannot_complete_the_entry_of_a_newer_claim()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        var old = await BeginAsync(s);
        await s.AbandonAsync(Scope, old);
        var current = await BeginAsync(s);

        await s.CompleteAsync(Scope, old, new IdempotencyOutcome.TaskCreated("stale"));
        var waiting = Assert.IsType<IdempotencyResult.InProgress>(await s.TryBeginAsync(Scope, Hash));
        Assert.False(waiting.Completion.IsCompleted);

        await s.CompleteAsync(Scope, current, new IdempotencyOutcome.TaskCreated("current"));
        var done = Assert.IsType<IdempotencyResult.Completed>(await s.TryBeginAsync(Scope, Hash));
        Assert.Equal("current", Assert.IsType<IdempotencyOutcome.TaskCreated>(done.Outcome).TaskId);
    }

    [SkippableFact]
    public async Task Old_claim_cannot_abandon_the_entry_of_a_newer_claim()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        var old = await BeginAsync(s);
        await s.AbandonAsync(Scope, old);
        var current = await BeginAsync(s);

        await s.AbandonAsync(Scope, old);
        var waiting = Assert.IsType<IdempotencyResult.InProgress>(await s.TryBeginAsync(Scope, Hash));
        Assert.False(waiting.Completion.IsCompleted);
        await s.AbandonAsync(Scope, current);
    }

    [SkippableFact]
    public async Task Old_claim_cannot_renew_the_entry_of_a_newer_claim()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        var old = await BeginAsync(s);
        await s.AbandonAsync(Scope, old);
        var current = await BeginAsync(s);

        await s.RenewAsync(Scope, old); // must not throw or change anything
        Assert.IsType<IdempotencyResult.InProgress>(await s.TryBeginAsync(Scope, Hash));
        await s.AbandonAsync(Scope, current);
    }

    [SkippableFact]
    public async Task Old_claim_cannot_change_a_completed_entry_of_a_newer_claim()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        var old = await BeginAsync(s);
        await s.AbandonAsync(Scope, old);
        var current = await BeginAsync(s);
        await s.CompleteAsync(Scope, current, new IdempotencyOutcome.TaskCreated("current"));

        await s.CompleteAsync(Scope, old, new IdempotencyOutcome.TaskCreated("stale"));
        await s.AbandonAsync(Scope, old);
        await s.RenewAsync(Scope, old);
        var done = Assert.IsType<IdempotencyResult.Completed>(await s.TryBeginAsync(Scope, Hash));
        Assert.Equal("current", Assert.IsType<IdempotencyOutcome.TaskCreated>(done.Outcome).TaskId);
    }

    [SkippableFact]
    public async Task Unknown_claim_id_changes_nothing()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        var claim = await BeginAsync(s);
        await s.CompleteAsync(Scope, Guid.NewGuid(), new IdempotencyOutcome.TaskCreated("x"));
        await s.AbandonAsync(Scope, Guid.NewGuid());
        Assert.IsType<IdempotencyResult.InProgress>(await s.TryBeginAsync(Scope, Hash));
        await s.AbandonAsync(Scope, claim);
    }
}

public sealed class InMemoryIdempotencyStoreTests : IdempotencyStoreContract
{
    protected override Task<IdempotencyStore> CreateStoreAsync(IdempotencyOptions options, TimeProvider? time = null) =>
        Task.FromResult<IdempotencyStore>(new InMemoryIdempotencyStore(options, time));
}

[Collection(ContainersCollection.Name)]
public sealed class PostgresIdempotencyStoreTests(PostgresFixture db) : IdempotencyStoreContract
{
    protected override async Task<IdempotencyStore> CreateStoreAsync(IdempotencyOptions options, TimeProvider? time = null)
    {
        var schema = await db.NewSchemaAsync(time);
        schema.IdempotencyPollInterval = TimeSpan.FromMilliseconds(20);
        return new PostgresIdempotencyStore(db.DataSource, options, schema, time);
    }

    [SkippableFact]
    public async Task Stale_claim_of_a_crashed_instance_can_be_taken_over()
    {
        var clock = new FakeTimeProvider();
        var schema = await db.NewSchemaAsync(clock);
        schema.IdempotencyClaimTimeout = TimeSpan.FromMinutes(5);
        var crashed = new PostgresIdempotencyStore(db.DataSource, new IdempotencyOptions(), schema, clock);
        var other = new PostgresIdempotencyStore(db.DataSource, new IdempotencyOptions(), schema, clock);

        Assert.IsType<IdempotencyResult.New>(await crashed.TryBeginAsync(Scope, Hash));
        Assert.IsType<IdempotencyResult.InProgress>(await other.TryBeginAsync(Scope, Hash));
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.IsType<IdempotencyResult.New>(await other.TryBeginAsync(Scope, Hash));
    }

    [SkippableFact]
    public async Task Waiter_on_another_instance_sees_the_outcome()
    {
        var schema = await db.NewSchemaAsync();
        schema.IdempotencyPollInterval = TimeSpan.FromMilliseconds(20);
        var a = new PostgresIdempotencyStore(db.DataSource, new IdempotencyOptions(), schema);
        var b = new PostgresIdempotencyStore(db.DataSource, new IdempotencyOptions(), schema);

        var claim = await BeginAsync(a);
        var waiting = Assert.IsType<IdempotencyResult.InProgress>(await b.TryBeginAsync(Scope, Hash));
        await a.CompleteAsync(Scope, claim, new IdempotencyOutcome.TaskCreated("task-1"));
        Assert.Equal("task-1", Assert.IsType<IdempotencyOutcome.TaskCreated>(await waiting.Completion.WaitAsync(TimeSpan.FromSeconds(10))).TaskId);
    }

    [SkippableFact]
    public async Task Renew_pushes_the_claim_timeout_out_from_now()
    {
        var clock = new FakeTimeProvider();
        var schema = await db.NewSchemaAsync(clock);
        schema.IdempotencyClaimTimeout = TimeSpan.FromMinutes(5);
        var owner = new PostgresIdempotencyStore(db.DataSource, new IdempotencyOptions(), schema, clock);
        var other = new PostgresIdempotencyStore(db.DataSource, new IdempotencyOptions(), schema, clock);

        var claim = await BeginAsync(owner);
        clock.Advance(TimeSpan.FromMinutes(4));
        await owner.RenewAsync(Scope, claim);
        clock.Advance(TimeSpan.FromMinutes(4)); // 8 min after the claim, 4 after the renew
        Assert.IsType<IdempotencyResult.InProgress>(await other.TryBeginAsync(Scope, Hash));
        clock.Advance(TimeSpan.FromMinutes(2)); // 6 min after the renew
        Assert.IsType<IdempotencyResult.New>(await other.TryBeginAsync(Scope, Hash));
    }

    [SkippableFact]
    public async Task Renew_by_another_principal_or_tenant_does_not_extend_the_claim()
    {
        var clock = new FakeTimeProvider();
        var schema = await db.NewSchemaAsync(clock);
        schema.IdempotencyClaimTimeout = TimeSpan.FromMinutes(5);
        var s = new PostgresIdempotencyStore(db.DataSource, new IdempotencyOptions(), schema, clock);

        var claim = await BeginAsync(s);
        clock.Advance(TimeSpan.FromMinutes(4));
        await s.RenewAsync(Scope with { Principal = "bob" }, claim);
        await s.RenewAsync(Scope with { Tenant = "u" }, claim);
        await s.RenewAsync(Scope with { Key = "other-key" }, claim);
        clock.Advance(TimeSpan.FromMinutes(2)); // 6 min after the real claim, which nobody renewed
        Assert.IsType<IdempotencyResult.New>(await s.TryBeginAsync(Scope, Hash));
    }

    [SkippableFact]
    public async Task Crashed_owner_cannot_complete_abandon_or_renew_after_a_takeover()
    {
        var clock = new FakeTimeProvider();
        var schema = await db.NewSchemaAsync(clock);
        schema.IdempotencyClaimTimeout = TimeSpan.FromMinutes(5);
        var crashed = new PostgresIdempotencyStore(db.DataSource, new IdempotencyOptions(), schema, clock);
        var other = new PostgresIdempotencyStore(db.DataSource, new IdempotencyOptions(), schema, clock);

        var old = await BeginAsync(crashed);
        clock.Advance(TimeSpan.FromMinutes(6));
        var current = await BeginAsync(other); // took over the stale claim
        Assert.NotEqual(old, current);

        // The crashed owner wakes up: none of this may touch the new owner's entry.
        await crashed.RenewAsync(Scope, old);
        await crashed.AbandonAsync(Scope, old);
        await crashed.CompleteAsync(Scope, old, new IdempotencyOutcome.TaskCreated("stale"));
        Assert.IsType<IdempotencyResult.InProgress>(await other.TryBeginAsync(Scope, Hash));

        // The renew did not extend the claim either: the new owner's clock still runs from its own start.
        clock.Advance(TimeSpan.FromMinutes(4));
        await crashed.RenewAsync(Scope, old);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.IsType<IdempotencyResult.New>(await other.TryBeginAsync(Scope, Hash));
        await other.AbandonAsync(Scope, current);
    }

    [SkippableFact]
    public async Task Commit_with_an_idempotency_outcome_completes_the_entry_without_a_complete_call()
    {
        var clock = new FakeTimeProvider();
        var (tasks, idem) = await NewStoresAsync(clock, TimeSpan.FromHours(1));
        var claim = await BeginAsync(idem);

        // The crash simulation: the task commit carries the outcome, and CompleteAsync is never called.
        await tasks.CommitAsync(CreateCommit("task-1") with { Idempotency = new IdempotencyCommit(Scope, claim, new IdempotencyOutcome.TaskCreated("task-1"), TimeSpan.FromHours(1)) });

        var done = Assert.IsType<IdempotencyResult.Completed>(await idem.TryBeginAsync(Scope, Hash));
        Assert.Equal(new IdempotencyOutcome.TaskCreated("task-1"), done.Outcome);
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.IsType<IdempotencyResult.Completed>(await idem.TryBeginAsync(Scope, Hash));
        clock.Advance(TimeSpan.FromMinutes(2)); // retention counts from the commit
        Assert.IsType<IdempotencyResult.New>(await idem.TryBeginAsync(Scope, OtherHash));
    }

    [SkippableFact]
    public async Task Rolled_back_commit_leaves_the_outcome_empty()
    {
        var (tasks, idem) = await NewStoresAsync(new FakeTimeProvider(), TimeSpan.FromHours(1));
        await tasks.CommitAsync(CreateCommit("task-1")); // the task id is taken
        var claim = await BeginAsync(idem);

        await Assert.ThrowsAsync<DuplicateTaskException>(() => tasks.CommitAsync(
            CreateCommit("task-1") with { Idempotency = new IdempotencyCommit(Scope, claim, new IdempotencyOutcome.TaskCreated("task-1"), TimeSpan.FromHours(1)) }).AsTask());

        Assert.IsType<IdempotencyResult.InProgress>(await idem.TryBeginAsync(Scope, Hash));
        await idem.AbandonAsync(Scope, claim);
    }

    [SkippableFact]
    public async Task Commit_with_another_claim_id_commits_the_task_and_records_no_outcome()
    {
        var (tasks, idem) = await NewStoresAsync(new FakeTimeProvider(), TimeSpan.FromHours(1));
        var claim = await BeginAsync(idem);

        var result = await tasks.CommitAsync(
            CreateCommit("task-1") with { Idempotency = new IdempotencyCommit(Scope, Guid.NewGuid(), new IdempotencyOutcome.TaskCreated("task-1"), TimeSpan.FromHours(1)) });

        Assert.Equal(1, result.Sequence);
        Assert.NotNull(await tasks.GetAsync(new TaskKey("t", "alice", "task-1")));
        Assert.IsType<IdempotencyResult.InProgress>(await idem.TryBeginAsync(Scope, Hash));
        await idem.AbandonAsync(Scope, claim);
    }

    [SkippableFact]
    public async Task Commit_does_not_overwrite_a_completed_entry()
    {
        var (tasks, idem) = await NewStoresAsync(new FakeTimeProvider(), TimeSpan.FromHours(1));
        var claim = await BeginAsync(idem);
        await idem.CompleteAsync(Scope, claim, new IdempotencyOutcome.TaskCreated("first"));

        await tasks.CommitAsync(CreateCommit("task-2") with { Idempotency = new IdempotencyCommit(Scope, claim, new IdempotencyOutcome.TaskCreated("second"), TimeSpan.FromHours(1)) });

        var done = Assert.IsType<IdempotencyResult.Completed>(await idem.TryBeginAsync(Scope, Hash));
        Assert.Equal("first", Assert.IsType<IdempotencyOutcome.TaskCreated>(done.Outcome).TaskId);
    }

    private async Task<(PostgresTaskStore Tasks, PostgresIdempotencyStore Idem)> NewStoresAsync(FakeTimeProvider clock, TimeSpan retention)
    {
        var schema = await db.NewSchemaAsync(clock);
        return (new PostgresTaskStore(db.DataSource, new StreamingOptions(), schema, clock),
            new PostgresIdempotencyStore(db.DataSource, new IdempotencyOptions { Retention = retention }, schema, clock));
    }

    private static TaskCommit CreateCommit(string taskId)
    {
        var record = TaskStoreContract.NewRecord("t", "alice", taskId, DateTimeOffset.UtcNow);
        return new TaskCommit(record, 0, StreamResponse.Of(record.Task.DeepCopy()), []);
    }

    [SkippableFact]
    public void Claim_timeout_defaults_to_two_minutes() =>
        Assert.Equal(TimeSpan.FromMinutes(2), new PostgresStoreOptions().IdempotencyClaimTimeout);
}
