using A2A.Json;
using A2A.Server;
using A2A.Server.Coordination;
using A2A.Server.Postgres;
using A2A.Server.Push;
using A2A.Server.Retention;
using A2A.Server.Storage;
using A2A.Tests;

namespace A2A.Stores.Tests;

/// <summary>The purge that the retention sweep and <see cref="A2ATaskAdmin"/> share, on PostgreSQL stores.</summary>
[Collection(ContainersCollection.Name)]
public sealed class RetentionPostgresTests(PostgresFixture db)
{
    private static readonly TaskKey Alice = new("t", "alice", "task1");
    private static readonly TaskKey Other = new("t", "alice", "task2");

    [SkippableFact]
    public async Task Purge_removes_the_task_with_its_events_push_configs_outbox_and_dead_letters()
    {
        var schema = await db.NewSchemaAsync();
        var clock = new FakeTimeProvider();
        var streaming = new StreamingOptions();
        var tasks = new PostgresTaskStore(db.DataSource, streaming, schema, clock);
        var events = new PostgresEventStore(db.DataSource, streaming, schema, clock);
        var configs = new PostgresPushConfigStore(db.DataSource, schema);
        var outbox = new PostgresPushOutbox(db.DataSource, schema, clock);
        var admin = new A2ATaskAdmin(tasks, events, configs, outbox, new InMemoryExecutionCoordinator(timeProvider: clock));

        await CompletedTaskAsync(tasks, configs, Alice, clock);
        await CompletedTaskAsync(tasks, configs, Other, clock);
        // Alice: one dead letter (the first entry of config c1) and pending entries; Other: pending entries only.
        var leased = await outbox.LeaseDueAsync(100, TimeSpan.FromMinutes(1));
        Assert.True(await outbox.DeadLetterAsync(leased.First(e => e.Delivery.Task == Alice && e.Delivery.ConfigId == "c1"), "gone"));
        clock.Advance(TimeSpan.FromMinutes(2)); // the other leases end
        Assert.Contains(Alice, await tasks.ListExpiredAsync(clock.GetUtcNow() + TimeSpan.FromDays(1), 10));

        Assert.False(await admin.DeleteTaskAsync(Alice with { Owner = "bob" })); // a foreign key deletes nothing
        Assert.True(await admin.DeleteTaskAsync(Alice));

        Assert.Null(await tasks.GetAsync(Alice));
        Assert.DoesNotContain(Alice, await tasks.ListExpiredAsync(clock.GetUtcNow() + TimeSpan.FromDays(1), 10));
        Assert.Empty(await TaskStoreContract.ReadAllAsync(events, Alice));
        Assert.Equal(0, await events.LastSequenceAsync(Alice));
        Assert.Empty(await configs.ListAsync(Alice));
        Assert.Empty(await outbox.ListDeadLettersAsync("t", 10));
        Assert.All(await outbox.LeaseDueAsync(100, TimeSpan.FromMinutes(1)), e => Assert.Equal(Other, e.Delivery.Task));

        // The other task is untouched; deleting again is a no-op.
        Assert.NotNull(await tasks.GetAsync(Other));
        Assert.NotEmpty(await TaskStoreContract.ReadAllAsync(events, Other));
        Assert.Equal(2, (await configs.ListAsync(Other)).Count);
        Assert.False(await admin.DeleteTaskAsync(Alice));
    }

    /// <summary>Creates a task, then completes it; both commits carry a delivery to push configs c1 and c2.</summary>
    private static async Task CompletedTaskAsync(TaskStore tasks, PushConfigStore configs, TaskKey key, TimeProvider clock)
    {
        foreach (var id in new[] { "c1", "c2" })
            await configs.SaveAsync(new PushConfigRecord(key, new TaskPushNotificationConfig { Id = id, TaskId = key.TaskId, Url = $"https://hooks.test/{id}" }));
        PushDelivery[] Deliveries() => [new(key, "c1", 0, Guid.NewGuid().ToString("N")), new(key, "c2", 0, Guid.NewGuid().ToString("N"))];
        var created = TaskStoreContract.NewRecord(key.Tenant, key.Owner, key.TaskId, clock.GetUtcNow());
        var first = await tasks.CommitAsync(new TaskCommit(created, 0, StreamResponse.Of(created.Task.DeepCopy()), Deliveries()));
        await tasks.CommitAsync(new TaskCommit(TaskStoreContract.WithState(first.Record, TaskState.Completed), first.Record.Version,
            TaskStoreContract.StatusEvent(key.TaskId, TaskState.Completed), Deliveries()));
    }
}
