using A2A.Server.Idempotency;
using A2A.Server.Postgres;
using A2A.Tests;

namespace A2A.Stores.Tests;

/// <summary>Behavior every <see cref="IdempotencyStore"/> must have.</summary>
public abstract class IdempotencyStoreContract
{
    protected abstract Task<IdempotencyStore> CreateStoreAsync(IdempotencyOptions options, TimeProvider? time = null);

    protected static readonly IdempotencyScope Scope = new("t", "alice", "key-1");
    protected static readonly byte[] Hash = [1, 2, 3, 4];
    protected static readonly byte[] OtherHash = [9, 9, 9, 9];

    [SkippableFact]
    public async Task First_claim_is_new_and_repeat_is_in_progress()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        Assert.IsType<IdempotencyResult.New>(await s.TryBeginAsync(Scope, Hash));
        var again = Assert.IsType<IdempotencyResult.InProgress>(await s.TryBeginAsync(Scope, Hash));
        Assert.False(again.Completion.IsCompleted);
        await s.AbandonAsync(Scope);
    }

    [SkippableFact]
    public async Task Different_hash_is_a_mismatch_while_running_and_after()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        await s.TryBeginAsync(Scope, Hash);
        Assert.IsType<IdempotencyResult.Mismatch>(await s.TryBeginAsync(Scope, OtherHash));
        await s.CompleteAsync(Scope, new IdempotencyOutcome.TaskCreated("task-1"));
        Assert.IsType<IdempotencyResult.Mismatch>(await s.TryBeginAsync(Scope, OtherHash));
    }

    [SkippableFact]
    public async Task Completed_request_returns_its_task_outcome()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        await s.TryBeginAsync(Scope, Hash);
        await s.CompleteAsync(Scope, new IdempotencyOutcome.TaskCreated("task-1", 7));
        var done = Assert.IsType<IdempotencyResult.Completed>(await s.TryBeginAsync(Scope, Hash));
        Assert.Equal(new IdempotencyOutcome.TaskCreated("task-1", 7), done.Outcome);
    }

    [SkippableFact]
    public async Task Completed_request_returns_its_direct_message()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        await s.TryBeginAsync(Scope, Hash);
        await s.CompleteAsync(Scope, new IdempotencyOutcome.DirectMessage(new Message
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
        await s.TryBeginAsync(Scope, Hash);
        await s.CompleteAsync(Scope, new IdempotencyOutcome.TaskCreated("first"));
        await s.CompleteAsync(Scope, new IdempotencyOutcome.TaskCreated("second"));
        var done = Assert.IsType<IdempotencyResult.Completed>(await s.TryBeginAsync(Scope, Hash));
        Assert.Equal("first", Assert.IsType<IdempotencyOutcome.TaskCreated>(done.Outcome).TaskId);
    }

    [SkippableFact]
    public async Task Waiting_request_gets_the_outcome_when_it_completes()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        await s.TryBeginAsync(Scope, Hash);
        var waiting = Assert.IsType<IdempotencyResult.InProgress>(await s.TryBeginAsync(Scope, Hash));
        await s.CompleteAsync(Scope, new IdempotencyOutcome.TaskCreated("task-1"));
        var outcome = await waiting.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("task-1", Assert.IsType<IdempotencyOutcome.TaskCreated>(outcome).TaskId);
    }

    [SkippableFact]
    public async Task Abandon_cancels_waiters_and_frees_the_key()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        await s.TryBeginAsync(Scope, Hash);
        var waiting = Assert.IsType<IdempotencyResult.InProgress>(await s.TryBeginAsync(Scope, Hash));
        await s.AbandonAsync(Scope);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(waiting.Completion.IsCanceled);
        Assert.IsType<IdempotencyResult.New>(await s.TryBeginAsync(Scope, OtherHash));
    }

    [SkippableFact]
    public async Task Abandon_after_complete_keeps_the_outcome()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        await s.TryBeginAsync(Scope, Hash);
        await s.CompleteAsync(Scope, new IdempotencyOutcome.TaskCreated("task-1"));
        await s.AbandonAsync(Scope);
        Assert.IsType<IdempotencyResult.Completed>(await s.TryBeginAsync(Scope, Hash));
    }

    [SkippableFact]
    public async Task Keys_are_scoped_by_tenant_and_principal()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        await s.TryBeginAsync(Scope, Hash);
        Assert.IsType<IdempotencyResult.New>(await s.TryBeginAsync(Scope with { Principal = "bob" }, OtherHash));
        Assert.IsType<IdempotencyResult.New>(await s.TryBeginAsync(Scope with { Tenant = "u" }, OtherHash));
    }

    [SkippableFact]
    public async Task Finished_entry_expires_after_retention()
    {
        var clock = new FakeTimeProvider();
        var s = await CreateStoreAsync(new IdempotencyOptions { Retention = TimeSpan.FromHours(1) }, clock);
        await s.TryBeginAsync(Scope, Hash);
        await s.CompleteAsync(Scope, new IdempotencyOutcome.TaskCreated("task-1"));

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
        await s.AbandonAsync(Scope);
    }

    [SkippableFact]
    public async Task Renew_keeps_a_running_claim_running()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        await s.TryBeginAsync(Scope, Hash);
        await s.RenewAsync(Scope);
        Assert.IsType<IdempotencyResult.InProgress>(await s.TryBeginAsync(Scope, Hash));
        await s.AbandonAsync(Scope);
    }

    [SkippableFact]
    public async Task Renew_of_a_missing_scope_does_nothing()
    {
        var s = await CreateStoreAsync(new IdempotencyOptions());
        await s.RenewAsync(Scope);
        Assert.IsType<IdempotencyResult.New>(await s.TryBeginAsync(Scope, Hash));
    }

    [SkippableFact]
    public async Task Renew_of_a_completed_scope_does_not_change_it()
    {
        var clock = new FakeTimeProvider();
        var s = await CreateStoreAsync(new IdempotencyOptions { Retention = TimeSpan.FromHours(1) }, clock);
        await s.TryBeginAsync(Scope, Hash);
        await s.CompleteAsync(Scope, new IdempotencyOutcome.TaskCreated("task-1"));
        clock.Advance(TimeSpan.FromMinutes(40));
        await s.RenewAsync(Scope);
        Assert.IsType<IdempotencyResult.Completed>(await s.TryBeginAsync(Scope, Hash));
        clock.Advance(TimeSpan.FromMinutes(30)); // still ends 1 h after completion, not after the renew
        Assert.IsType<IdempotencyResult.New>(await s.TryBeginAsync(Scope, Hash));
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
        var schema = await db.NewSchemaAsync();
        schema.IdempotencyPollInterval = TimeSpan.FromMilliseconds(20);
        return new PostgresIdempotencyStore(db.DataSource, options, schema, time);
    }

    [SkippableFact]
    public async Task Stale_claim_of_a_crashed_instance_can_be_taken_over()
    {
        var clock = new FakeTimeProvider();
        var schema = await db.NewSchemaAsync();
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

        await a.TryBeginAsync(Scope, Hash);
        var waiting = Assert.IsType<IdempotencyResult.InProgress>(await b.TryBeginAsync(Scope, Hash));
        await a.CompleteAsync(Scope, new IdempotencyOutcome.TaskCreated("task-1"));
        Assert.Equal("task-1", Assert.IsType<IdempotencyOutcome.TaskCreated>(await waiting.Completion.WaitAsync(TimeSpan.FromSeconds(10))).TaskId);
    }

    [SkippableFact]
    public async Task Renew_pushes_the_claim_timeout_out_from_now()
    {
        var clock = new FakeTimeProvider();
        var schema = await db.NewSchemaAsync();
        schema.IdempotencyClaimTimeout = TimeSpan.FromMinutes(5);
        var owner = new PostgresIdempotencyStore(db.DataSource, new IdempotencyOptions(), schema, clock);
        var other = new PostgresIdempotencyStore(db.DataSource, new IdempotencyOptions(), schema, clock);

        await owner.TryBeginAsync(Scope, Hash);
        clock.Advance(TimeSpan.FromMinutes(4));
        await owner.RenewAsync(Scope);
        clock.Advance(TimeSpan.FromMinutes(4)); // 8 min after the claim, 4 after the renew
        Assert.IsType<IdempotencyResult.InProgress>(await other.TryBeginAsync(Scope, Hash));
        clock.Advance(TimeSpan.FromMinutes(2)); // 6 min after the renew
        Assert.IsType<IdempotencyResult.New>(await other.TryBeginAsync(Scope, Hash));
    }

    [SkippableFact]
    public void Claim_timeout_defaults_to_two_minutes() =>
        Assert.Equal(TimeSpan.FromMinutes(2), new PostgresStoreOptions().IdempotencyClaimTimeout);
}
