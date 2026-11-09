using A2A.Server;
using A2A.Server.Idempotency;
using A2A.Server.Postgres;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;

namespace A2A.Stores.Tests;

/// <summary>
/// Several instances share one database but not one clock. Deadlines (outbox due times and leases, idempotency claims and
/// retention, event log retention) must be decided by the database clock, so an instance whose clock is off does not take
/// another's lease, run a claimed request twice, or delay or drop data.
/// </summary>
[Collection(ContainersCollection.Name)]
public sealed class PostgresClockSkewTests(PostgresFixture db)
{
    private static readonly TimeSpan Skew = TimeSpan.FromHours(1);
    private static readonly IdempotencyScope Scope = new("t", "alice", "key-1");
    private static readonly byte[] Hash = [1, 2, 3, 4];

    /// <summary>The system clock moved by a fixed offset: an instance whose clock is wrong.</summary>
    private sealed class SkewedClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + offset;
    }

    private static readonly TimeProvider Ahead = new SkewedClock(Skew);
    private static readonly TimeProvider Behind = new SkewedClock(-Skew);

    private static async Task CommitWithDeliveryAsync(TaskStore tasks, TaskKey key)
    {
        var record = TaskStoreContract.NewRecord(key.Tenant, key.Owner, key.TaskId);
        await tasks.CommitAsync(new TaskCommit(record, 0, StreamResponse.Of(record.Task.DeepCopy()),
            [new PushDelivery(key, "c1", 0, Guid.NewGuid().ToString("N"))]));
    }

    [SkippableFact]
    public async Task Outbox_lease_holds_against_an_instance_whose_clock_runs_ahead()
    {
        var schema = await db.NewSchemaAsync();
        var tasks = new PostgresTaskStore(db.DataSource, new StreamingOptions(), schema);
        await CommitWithDeliveryAsync(tasks, new TaskKey("t", "alice", "task1"));

        var a = new PostgresPushOutbox(db.DataSource, schema);
        var b = new PostgresPushOutbox(db.DataSource, schema, Ahead);
        Assert.Single(await a.LeaseDueAsync(10, TimeSpan.FromMinutes(1)));
        Assert.Empty(await b.LeaseDueAsync(10, TimeSpan.FromMinutes(1)));
    }

    [SkippableFact]
    public async Task Delivery_age_is_measured_on_the_database_clock()
    {
        var schema = await db.NewSchemaAsync();
        var tasks = new PostgresTaskStore(db.DataSource, new StreamingOptions(), schema);
        await CommitWithDeliveryAsync(tasks, new TaskKey("t", "alice", "task1"));
        var late = new PostgresPushOutbox(db.DataSource, schema, Ahead);
        Assert.True(await late.EnqueueAsync(new PushDelivery(new TaskKey("t", "alice", "task1"), "c2", 1, "d2"), new byte[] { 1 }));

        // An instance an hour ahead (or behind) does not make a delivery queued just now look old (or negative).
        foreach (var outbox in new[] { late, new PostgresPushOutbox(db.DataSource, schema, Behind) })
        {
            var entries = await outbox.LeaseDueAsync(10, TimeSpan.Zero);
            Assert.Equal(2, entries.Count);
            Assert.All(entries, e => Assert.InRange(e.Age, TimeSpan.Zero, TimeSpan.FromMinutes(1)));
        }
    }

    [SkippableFact]
    public async Task Outbox_rows_from_before_the_queued_column_are_still_leased_and_aged_from_their_due_time()
    {
        var schema = await db.NewSchemaAsync();
        var key = new TaskKey("t", "alice", "task1");
        var table = $"\"{schema.Schema}\".a2a_push_outbox";
        await using (var cmd = db.DataSource.CreateCommand($"ALTER TABLE {table} DROP COLUMN queued_ticks"))
            await cmd.ExecuteNonQueryAsync();
        // The database now looks like one from an older build: the version row says 1 (the new check skips a current version).
        await using (var cmd = db.DataSource.CreateCommand($"DELETE FROM \"{schema.Schema}\".a2a_schema_version; INSERT INTO \"{schema.Schema}\".a2a_schema_version (version) VALUES (1)"))
            await cmd.ExecuteNonQueryAsync();
        await using (var cmd = db.DataSource.CreateCommand(
            $"INSERT INTO {table} (tenant, owner, task_id, config_id, seq, delivery_id, attempt, payload, due_ticks) VALUES ('t', 'alice', 'task1', 'c1', 1, 'old', 0, '\\x01', 1)"))
            await cmd.ExecuteNonQueryAsync();

        await PostgresSchema.EnsureSchemaAsync(db.DataSource, schema.Schema); // the upgrade: adds the column, keeps the row

        var entry = Assert.Single(await new PostgresPushOutbox(db.DataSource, schema).LeaseDueAsync(10, TimeSpan.FromMinutes(1)));
        Assert.Equal("old", entry.Delivery.DeliveryId);
        Assert.True(entry.Age > TimeSpan.FromDays(365)); // due_ticks 1 is the year 1: a NULL queued time reads as the due time
        await PostgresSchema.EnsureSchemaAsync(db.DataSource, schema.Schema); // and the script stays idempotent
    }

    [SkippableFact]
    public async Task Delivery_committed_by_an_instance_whose_clock_runs_ahead_is_due_at_once()
    {
        var schema = await db.NewSchemaAsync();
        var aheadTasks = new PostgresTaskStore(db.DataSource, new StreamingOptions(), schema, Ahead);
        await CommitWithDeliveryAsync(aheadTasks, new TaskKey("t", "alice", "task1"));

        Assert.Single(await new PostgresPushOutbox(db.DataSource, schema).LeaseDueAsync(10, TimeSpan.FromMinutes(1)));
    }

    [SkippableFact]
    public async Task Retry_scheduled_by_an_instance_whose_clock_runs_ahead_waits_only_its_backoff()
    {
        var schema = await db.NewSchemaAsync();
        var tasks = new PostgresTaskStore(db.DataSource, new StreamingOptions(), schema);
        await CommitWithDeliveryAsync(tasks, new TaskKey("t", "alice", "task1"));

        var ahead = new PostgresPushOutbox(db.DataSource, schema, Ahead);
        var leased = Assert.Single(await ahead.LeaseDueAsync(10, TimeSpan.FromMinutes(1)));
        // A zero backoff on the skewed instance's clock: due now, not an hour from now.
        Assert.True(await ahead.RescheduleAsync(leased, 1, Ahead.GetUtcNow(), "boom"));

        Assert.Single(await new PostgresPushOutbox(db.DataSource, schema).LeaseDueAsync(10, TimeSpan.FromMinutes(1)));
    }

    [SkippableFact]
    public async Task Idempotency_claim_holds_against_an_instance_whose_clock_runs_ahead()
    {
        var schema = await db.NewSchemaAsync();
        var a = new PostgresIdempotencyStore(db.DataSource, new IdempotencyOptions(), schema);
        var b = new PostgresIdempotencyStore(db.DataSource, new IdempotencyOptions(), schema, Ahead);

        var claim = Assert.IsType<IdempotencyResult.New>(await a.TryBeginAsync(Scope, Hash)).ClaimId;
        Assert.IsType<IdempotencyResult.InProgress>(await b.TryBeginAsync(Scope, Hash));
        await a.AbandonAsync(Scope, claim);
    }

    [SkippableFact]
    public async Task Idempotency_outcome_completed_by_an_instance_whose_clock_runs_behind_is_kept()
    {
        var schema = await db.NewSchemaAsync();
        var options = new IdempotencyOptions { Retention = TimeSpan.FromMinutes(30) };
        var behind = new PostgresIdempotencyStore(db.DataSource, options, schema, Behind);
        var other = new PostgresIdempotencyStore(db.DataSource, options, schema);

        var claim = Assert.IsType<IdempotencyResult.New>(await behind.TryBeginAsync(Scope, Hash)).ClaimId;
        await behind.CompleteAsync(Scope, claim, new IdempotencyOutcome.TaskCreated("task-1"));
        Assert.IsType<IdempotencyResult.Completed>(await other.TryBeginAsync(Scope, Hash));
    }

    [SkippableFact]
    public async Task Event_log_retention_set_by_an_instance_whose_clock_runs_behind_is_kept()
    {
        var schema = await db.NewSchemaAsync();
        var options = new StreamingOptions { TerminalRetention = TimeSpan.FromMinutes(30) };
        var key = new TaskKey("t", "alice", "task1");
        var behind = new PostgresEventStore(db.DataSource, options, schema, Behind);
        await behind.AppendAsync(key, EventEncoder.Encode(0, StreamResponse.Of(TaskStoreContract.NewRecord("t", "alice", "task1").Task)));
        await behind.MarkTerminalAsync(key);

        Assert.Equal(1, await new PostgresEventStore(db.DataSource, options, schema).LastSequenceAsync(key));
    }
}
