using A2A.Server;
using A2A.Server.Coordination;
using A2A.Server.Postgres;
using A2A.Server.Redis;
using A2A.Server.Retention;
using A2A.Server.Storage;
using A2A.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace A2A.Stores.Tests;

/// <summary>
/// What the Postgres coordinator is for: a lease that lives in the database outlives a crash of its instance, expires, and
/// is found by the retention sweep. Also two instances racing for one task, and the registration rules.
/// </summary>
[Collection(ContainersCollection.Name)]
public sealed class PostgresCoordinatorTests(PostgresFixture db)
{
    private static readonly TaskKey Running = new("", "alice", "crashed-task");

    private static AgentCard Card() => new()
    {
        Name = "Test Agent",
        Description = "Test agent",
        Version = "1.0.0",
        SupportedInterfaces = [new AgentInterface { Url = "http://localhost/a2a", ProtocolBinding = ProtocolBindings.JsonRpc }],
        Capabilities = new AgentCapabilities(),
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "s", Name = "S", Description = "S", Tags = ["test"] }],
    };

    private static ServiceProvider Host(NpgsqlDataSource dataSource, PostgresStoreOptions schema, FakeTimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddA2AServer(o => o.AgentCard = Card());
        services.AddA2APostgresStores(dataSource, o =>
        {
            o.Schema = schema.Schema;
            o.CreateSchemaOnStartup = false;
            o.UseDatabaseClock = false; // leases and task times follow the test's clock
        });
        services.AddA2ARetention();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The process dies while it runs a task: its lease is never unregistered, and nothing renews it. A new process (a new
    /// coordinator on the same database) sees the lease expire, and its retention sweep marks the task FAILED. With the
    /// in-memory coordinator the lease would have vanished with the process and the task stayed WORKING for good.
    /// </summary>
    [SkippableFact]
    public async Task Task_of_a_crashed_process_is_failed_by_the_sweep_once_its_lease_expires()
    {
        var clock = new FakeTimeProvider();
        var schema = await db.NewSchemaAsync(clock);
        var ttl = new CoordinationOptions().LeaseTtl;

        // The first process: the task is WORKING and its execution holds a lease. Then it crashes (no unregister).
        await using (var first = Host(db.DataSource, schema, clock))
        {
            var tasks = first.GetRequiredService<TaskStore>();
            var created = TaskStoreContract.NewRecord(Running.Tenant, Running.Owner, Running.TaskId, clock.GetUtcNow());
            var submitted = await tasks.CommitAsync(new TaskCommit(created, 0, TaskStoreContract.StatusEvent(Running.TaskId, TaskState.Submitted), []));
            await tasks.CommitAsync(new TaskCommit(TaskStoreContract.WithState(submitted.Record, TaskState.Working), submitted.Record.Version,
                TaskStoreContract.StatusEvent(Running.TaskId, TaskState.Working), []));
            await first.GetRequiredService<ExecutionCoordinator>().RegisterAsync(Running, "execution-1");
        }

        // The restarted process.
        await using var second = Host(db.DataSource, schema, clock);
        var coordinator = Assert.IsType<PostgresExecutionCoordinator>(second.GetRequiredService<ExecutionCoordinator>());
        var store = second.GetRequiredService<TaskStore>();
        var sweeper = ActivatorUtilities.CreateInstance<A2ARetentionService>(second, Options.Create(new A2ARetentionOptions()));

        Assert.True(await coordinator.IsRunningAsync(Running));
        await sweeper.RunOnceAsync(CancellationToken.None);
        Assert.Equal(TaskState.Working, (await store.GetAsync(Running))!.Task.Status.State); // the lease has not expired

        clock.Advance(ttl + TimeSpan.FromSeconds(1));
        Assert.False(await coordinator.IsRunningAsync(Running));
        Assert.Equal([(Running, "execution-1")], await coordinator.ListExpiredAsync(10));
        // The task changed within the last lease TTL (the crash may have been right after a commit): left alone for now.
        clock.Advance(ttl);
        await sweeper.RunOnceAsync(CancellationToken.None);

        var failed = (await store.GetAsync(Running))!.Task.Status;
        Assert.Equal(TaskState.Failed, failed.State);
        Assert.Equal("The agent stopped unexpectedly.", failed.Message!.GetText());
        Assert.Empty(await coordinator.ListExpiredAsync(10)); // the swept lease is gone
    }

    /// <summary>
    /// Two instances race to start an execution of the same tasks. Per task exactly one wins; the other's heartbeat and
    /// unregister never touch the winner's lease, and the winner keeps it.
    /// </summary>
    [SkippableFact]
    public async Task Racing_instances_leave_one_lease_owner_and_fencing_holds()
    {
        var clock = new FakeTimeProvider();
        var schema = await db.NewSchemaAsync(clock);
        await using var a = new PostgresExecutionCoordinator(db.DataSource, schema, time: clock);
        await using var b = new PostgresExecutionCoordinator(db.DataSource, schema, time: clock);
        var keys = Enumerable.Range(0, 25).Select(i => new TaskKey("t", "alice", $"task-{i}")).ToArray();

        var races = await Task.WhenAll(keys.Select(key => Task.Run(async () =>
        {
            var wonA = Task.Run(async () => await a.TryRegisterExclusiveAsync(key, "from-a"));
            var wonB = Task.Run(async () => await b.TryRegisterExclusiveAsync(key, "from-b"));
            return (Key: key, A: await wonA, B: await wonB);
        })));

        foreach (var (key, wonA, wonB) in races)
        {
            Assert.True(wonA ^ wonB, $"{key.TaskId}: exactly one instance may win");
            var (winner, winnerId, loser, loserId) = wonA ? (a, "from-a", b, "from-b") : (b, "from-b", a, "from-a");
            Assert.False(await loser.HeartbeatAsync(key, loserId)); // the loser holds no lease
            await loser.UnregisterAsync(key, loserId);               // and cannot end the winner's
            Assert.True(await loser.IsRunningAsync(key));
            Assert.True(await winner.HeartbeatAsync(key, winnerId));
            Assert.False(await winner.HeartbeatAsync(key, loserId)); // an id is bound to its own lease
        }
        Assert.Empty(await a.ListExpiredAsync(100));
    }

    private static ServiceProvider Registered(params Action<IServiceCollection>[] steps)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        foreach (var step in steps) step(services);
        return services.BuildServiceProvider();
    }

    private static readonly Action<IServiceCollection> Server = s => s.AddA2AServer();

    // Never connects: the resolved coordinators are only inspected.
    private static readonly Action<IServiceCollection> Redis = s => s.AddA2ARedisExecutionCoordinator("localhost:1,abortConnect=false");

    private static readonly Action<IServiceCollection> Stores = s => s.AddA2APostgresStores("Host=localhost;Database=unused");

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Redis_coordinator_is_kept_whatever_the_registration_order(string order, int[] steps)
    {
        Action<IServiceCollection>[] all = [Server, Redis, Stores];
        await using var sp = Registered([.. steps.Select(i => all[i])]);
        Assert.True(sp.GetRequiredService<ExecutionCoordinator>() is RedisExecutionCoordinator, order);
    }

    public static TheoryData<string, int[]> Orders => new()
    {
        { "server, redis, postgres", [0, 1, 2] },
        { "server, postgres, redis", [0, 2, 1] },
        { "redis, server, postgres", [1, 0, 2] },
        { "redis, postgres, server", [1, 2, 0] },
        { "postgres, server, redis", [2, 0, 1] },
        { "postgres, redis, server", [2, 1, 0] },
    };

    [Fact]
    public async Task Postgres_stores_register_the_postgres_coordinator_by_default()
    {
        await using var before = Registered(Stores, Server);
        Assert.IsType<PostgresExecutionCoordinator>(before.GetRequiredService<ExecutionCoordinator>());
        await using var after = Registered(Server, Stores);
        Assert.IsType<PostgresExecutionCoordinator>(after.GetRequiredService<ExecutionCoordinator>());
        Assert.Same(after.GetRequiredService<ExecutionCoordinator>(), after.GetRequiredService<PostgresExecutionCoordinator>());
    }

    [Fact]
    public async Task Register_coordinator_false_keeps_the_in_memory_one()
    {
        await using var sp = Registered(Server, s => s.AddA2APostgresStores("Host=localhost;Database=unused", o => o.RegisterCoordinator = false));
        Assert.IsType<InMemoryExecutionCoordinator>(sp.GetRequiredService<ExecutionCoordinator>());
    }

    [Fact]
    public async Task A_coordinator_the_host_registered_itself_is_kept()
    {
        var own = new InMemoryExecutionCoordinator();
        await using var sp = Registered(s => s.AddSingleton<ExecutionCoordinator>(own), Server, Stores);
        Assert.Same(own, sp.GetRequiredService<ExecutionCoordinator>());
    }

    [Fact]
    public async Task Explicit_registration_replaces_any_earlier_coordinator()
    {
        await using var sp = Registered(Server, Redis, s => s.AddA2APostgresExecutionCoordinator("Host=localhost;Database=unused"));
        Assert.IsType<PostgresExecutionCoordinator>(sp.GetRequiredService<ExecutionCoordinator>());
    }
}
