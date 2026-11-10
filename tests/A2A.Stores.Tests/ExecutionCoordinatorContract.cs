using A2A.Server;
using A2A.Server.Coordination;
using A2A.Server.Postgres;
using A2A.Server.Redis;
using A2A.Server.Storage;
using A2A.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace A2A.Stores.Tests;

/// <summary>
/// Behavior every <see cref="ExecutionCoordinator"/> must have. A coordinator pair stands for two server instances; for the
/// process-local coordinator both are the same instance.
/// </summary>
public abstract class ExecutionCoordinatorContract
{
    protected static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    protected static TaskKey Key(string id = "task", string tenant = "t", string owner = "alice") => new(tenant, owner, id);

    /// <summary>The lease settings the coordinators are created with.</summary>
    protected abstract CoordinationOptions Options { get; }

    /// <summary>Returns two coordinators that share state (two instances), or the same one twice.</summary>
    protected abstract Task<(ExecutionCoordinator A, ExecutionCoordinator B)> CreatePairAsync();

    /// <summary>Lets <paramref name="time"/> pass on the coordinators' clock: the fake clock, or the real one.</summary>
    protected abstract Task PassAsync(TimeSpan time);

    /// <summary>Lets every lease registered so far expire.</summary>
    private Task PassLeaseTtlAsync() => PassAsync(Options.LeaseTtl + TimeSpan.FromMilliseconds(50));

    /// <summary>Polls <paramref name="condition"/> until it holds, failing after <see cref="Wait"/>.</summary>
    protected static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting until {what}.");
            await Task.Delay(20);
        }
    }

    private static async Task<bool> ExpiredContainsAsync(ExecutionCoordinator c, TaskKey task, string executionId) =>
        (await c.ListExpiredAsync(100)).Contains((task, executionId));

    [Fact]
    public async Task Registered_execution_runs_everywhere_until_unregistered()
    {
        var (a, b) = await CreatePairAsync();
        Assert.False(await b.IsRunningAsync(Key()));
        await a.RegisterAsync(Key(), "e1");
        Assert.True(await a.IsRunningAsync(Key()));
        Assert.True(await b.IsRunningAsync(Key()));
        await a.UnregisterAsync(Key(), "e1");
        Assert.False(await a.IsRunningAsync(Key()));
        Assert.False(await b.IsRunningAsync(Key()));
        Assert.Empty(await b.ListExpiredAsync(10));
    }

    [Fact]
    public async Task Running_is_scoped_to_tenant_owner_and_task()
    {
        var (a, b) = await CreatePairAsync();
        await a.RegisterAsync(Key("x"), "e1");
        Assert.True(await b.IsRunningAsync(Key("x")));
        Assert.False(await b.IsRunningAsync(Key("y")));
        Assert.False(await b.IsRunningAsync(Key("x", owner: "bob")));
        Assert.False(await b.IsRunningAsync(Key("x", tenant: "u")));
    }

    [Fact]
    public async Task Task_runs_while_any_of_its_executions_runs()
    {
        var (a, b) = await CreatePairAsync();
        await a.RegisterAsync(Key(), "e1");
        await b.RegisterAsync(Key(), "e2");
        await a.UnregisterAsync(Key(), "e1");
        Assert.True(await a.IsRunningAsync(Key()));
        await b.UnregisterAsync(Key(), "e2");
        Assert.False(await a.IsRunningAsync(Key()));
    }

    [Fact]
    public async Task Exclusive_register_succeeds_on_a_free_task_and_counts_as_running()
    {
        var (a, b) = await CreatePairAsync();
        Assert.True(await a.TryRegisterExclusiveAsync(Key(), "e1"));
        Assert.True(await a.IsRunningAsync(Key()));
        Assert.True(await b.IsRunningAsync(Key()));
    }

    [Fact]
    public async Task Exclusive_register_is_refused_while_another_lease_runs()
    {
        var (a, b) = await CreatePairAsync();
        await a.RegisterAsync(Key(), "e1");
        Assert.False(await b.TryRegisterExclusiveAsync(Key(), "e2"));
        Assert.False(await b.HeartbeatAsync(Key(), "e2")); // the refused call left no lease
        await a.UnregisterAsync(Key(), "e1");
        Assert.True(await b.TryRegisterExclusiveAsync(Key(), "e2"));
    }

    [Fact]
    public async Task Exclusive_register_ignores_an_expired_lease()
    {
        var (a, b) = await CreatePairAsync();
        await a.RegisterAsync(Key(), "e1");
        await PassLeaseTtlAsync();
        Assert.True(await b.TryRegisterExclusiveAsync(Key(), "e2"));
    }

    [Fact]
    public async Task Exclusive_register_is_scoped_to_tenant_owner_and_task()
    {
        var (a, b) = await CreatePairAsync();
        await a.RegisterAsync(Key("x"), "e1");
        Assert.True(await b.TryRegisterExclusiveAsync(Key("x", tenant: "u"), "e2"));
        Assert.True(await b.TryRegisterExclusiveAsync(Key("x", owner: "bob"), "e3"));
        Assert.True(await b.TryRegisterExclusiveAsync(Key("y"), "e4"));
        Assert.False(await b.TryRegisterExclusiveAsync(Key("x"), "e5"));
    }

    [Fact]
    public async Task Concurrent_exclusive_registers_have_one_winner()
    {
        var (a, b) = await CreatePairAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(async () => await (i % 2 == 0 ? a : b).TryRegisterExclusiveAsync(Key(), $"e{i}"))));
        Assert.Equal(1, results.Count(won => won));
    }

    [Fact]
    public async Task Stale_execution_id_never_touches_a_newer_lease()
    {
        var (a, b) = await CreatePairAsync();
        await a.RegisterAsync(Key(), "old");
        await a.UnregisterAsync(Key(), "old");
        await b.RegisterAsync(Key(), "new");

        Assert.False(await a.HeartbeatAsync(Key(), "old")); // its lease is gone
        await a.UnregisterAsync(Key(), "old");
        Assert.True(await a.IsRunningAsync(Key()));

        // The old heartbeat did not create a lease: once the new one ends, nothing runs.
        await b.UnregisterAsync(Key(), "new");
        Assert.False(await a.IsRunningAsync(Key()));
        Assert.Empty(await a.ListExpiredAsync(10));
    }

    [Fact]
    public async Task Lease_without_heartbeat_expires_and_is_listed()
    {
        var (a, b) = await CreatePairAsync();
        await a.RegisterAsync(Key(), "e1");
        Assert.Empty(await b.ListExpiredAsync(10));
        await PassLeaseTtlAsync();
        await EventuallyAsync(async () => !await b.IsRunningAsync(Key()), "the lease expired");
        Assert.Equal([(Key(), "e1")], await b.ListExpiredAsync(10));
    }

    [Fact]
    public async Task Heartbeats_keep_the_lease_alive()
    {
        var (a, b) = await CreatePairAsync();
        await a.RegisterAsync(Key(), "e1");
        // Well past the TTL in all, one heartbeat interval at a time.
        for (var i = 0; i < 6; i++)
        {
            await PassAsync(Options.Heartbeat);
            Assert.True(await a.HeartbeatAsync(Key(), "e1")); // the lease was held
        }
        Assert.True(Options.Heartbeat * 6 > Options.LeaseTtl);
        Assert.True(await b.IsRunningAsync(Key()));
        Assert.Empty(await b.ListExpiredAsync(10));
    }

    [Fact]
    public async Task Heartbeat_never_brings_back_an_expired_lease()
    {
        var (a, b) = await CreatePairAsync();
        await a.RegisterAsync(Key(), "e1");
        await PassLeaseTtlAsync();
        await EventuallyAsync(async () => !await b.IsRunningAsync(Key()), "the lease expired");

        Assert.False(await a.HeartbeatAsync(Key(), "e1")); // the lease was lost
        Assert.False(await b.IsRunningAsync(Key()));
        Assert.True(await ExpiredContainsAsync(b, Key(), "e1"));
    }

    [Fact]
    public async Task Heartbeat_of_a_missing_lease_creates_nothing()
    {
        var (a, b) = await CreatePairAsync();
        Assert.False(await a.HeartbeatAsync(Key(), "never-registered"));
        Assert.False(await b.IsRunningAsync(Key()));
        await PassLeaseTtlAsync();
        Assert.Empty(await b.ListExpiredAsync(10));
    }

    [Fact]
    public async Task Unregistered_expired_lease_is_no_longer_listed()
    {
        var (a, b) = await CreatePairAsync();
        await a.RegisterAsync(Key(), "e1");
        await PassLeaseTtlAsync();
        await EventuallyAsync(() => ExpiredContainsAsync(b, Key(), "e1"), "the lease is listed as expired");
        await b.UnregisterAsync(Key(), "e1");
        Assert.Empty(await b.ListExpiredAsync(10));
    }

    [Fact]
    public async Task Expired_listing_is_oldest_first_and_capped()
    {
        var (a, b) = await CreatePairAsync();
        await a.RegisterAsync(Key("x"), "first");
        await PassAsync(TimeSpan.FromMilliseconds(100));
        await b.RegisterAsync(Key("y"), "second");
        await PassLeaseTtlAsync();
        await EventuallyAsync(async () => (await a.ListExpiredAsync(10)).Count == 2, "both leases expired");

        Assert.Equal([(Key("x"), "first"), (Key("y"), "second")], await a.ListExpiredAsync(10));
        Assert.Equal([(Key("x"), "first")], await b.ListExpiredAsync(1));
    }

    [Fact]
    public async Task Keys_with_separator_characters_round_trip()
    {
        var (a, b) = await CreatePairAsync();
        var odd = new TaskKey("t:\u001f\"[", "o,\"]\\", "id \u00e9\u001f*");
        await a.RegisterAsync(odd, "e:1");
        Assert.True(await b.IsRunningAsync(odd));
        Assert.False(await b.IsRunningAsync(Key()));
        await PassLeaseTtlAsync();
        await EventuallyAsync(() => ExpiredContainsAsync(b, odd, "e:1"), "the odd lease is listed as expired");
    }

    /// <summary>True when <see cref="CreatePairAsync"/> returns two instances; false when it returns one instance twice.</summary>
    protected abstract bool HasTwoInstances { get; }

    [Fact]
    public async Task Cancel_request_reaches_every_reader_of_the_other_instances_but_not_the_sender()
    {
        var (a, b) = await CreatePairAsync();
        using var cts = new CancellationTokenSource(Wait);
        await using var onB = b.CancelRequestsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        await using var onB2 = b.CancelRequestsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        await using var onA = a.CancelRequestsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var nextB = onB.MoveNextAsync(); // registers the reader
        var nextB2 = onB2.MoveNextAsync();
        var nextA = onA.MoveNextAsync();

        await a.RequestCancelAsync(Key("x", owner: "o\u001f1"));

        if (!HasTwoInstances)
        {
            // One instance: nobody else to tell, and the sender cancels its own executions directly.
            Assert.False(nextA.IsCompleted);
            Assert.False(nextB.IsCompleted);
            Assert.False(nextB2.IsCompleted);
            cts.Cancel();
            foreach (var pending in new[] { nextA, nextB, nextB2 })
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            return;
        }
        Assert.True(await nextB);
        Assert.Equal(Key("x", owner: "o\u001f1"), onB.Current);
        Assert.True(await nextB2);
        Assert.Equal(Key("x", owner: "o\u001f1"), onB2.Current);

        // A request from B reaches A after A's own one would have (B saw A's first): A's first request is B's.
        await b.RequestCancelAsync(Key("y"));
        Assert.True(await nextA);
        Assert.Equal(Key("y"), onA.Current);
        cts.Cancel();
    }

    [Fact]
    public async Task Sweep_lease_is_held_by_one_caller_until_it_expires()
    {
        var (a, b) = await CreatePairAsync();
        var duration = Options.LeaseTtl;
        Assert.True(await a.TryAcquireSweepLeaseAsync("retention", duration));
        Assert.False(await b.TryAcquireSweepLeaseAsync("retention", duration));
        Assert.False(await a.TryAcquireSweepLeaseAsync("retention", duration));
        Assert.True(await b.TryAcquireSweepLeaseAsync("other", duration)); // names are separate

        await PassAsync(duration + TimeSpan.FromMilliseconds(50));
        await EventuallyAsync(async () => await b.TryAcquireSweepLeaseAsync("retention", duration), "the sweep lease expired");
        Assert.False(await a.TryAcquireSweepLeaseAsync("retention", duration));
    }
}

public sealed class InMemoryExecutionCoordinatorTests : ExecutionCoordinatorContract
{
    private readonly FakeTimeProvider _clock = new();

    protected override bool HasTwoInstances => false;

    protected override CoordinationOptions Options { get; } = new();

    protected override Task<(ExecutionCoordinator A, ExecutionCoordinator B)> CreatePairAsync()
    {
        var c = new InMemoryExecutionCoordinator(Options, _clock);
        return Task.FromResult<(ExecutionCoordinator, ExecutionCoordinator)>((c, c));
    }

    protected override Task PassAsync(TimeSpan time)
    {
        _clock.Advance(time);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Lease_expiry_follows_the_injected_clock()
    {
        var c = new InMemoryExecutionCoordinator(Options, _clock);
        await c.RegisterAsync(Key(), "e1");
        _clock.Advance(Options.LeaseTtl - TimeSpan.FromMilliseconds(1));
        Assert.True(await c.IsRunningAsync(Key()));
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(await c.IsRunningAsync(Key()));
    }

    [Theory]
    [InlineData(0, 30, 1)]       // no heartbeat
    [InlineData(-1, 30, 1)]
    [InlineData(10, 20, 1)]      // TTL must be more than 2 x heartbeat
    [InlineData(10, 15, 1)]
    [InlineData(10, 30, 0)]      // no poll interval
    public void Invalid_options_are_rejected(int heartbeatSeconds, int ttlSeconds, int pollSeconds)
    {
        var options = new CoordinationOptions
        {
            Heartbeat = TimeSpan.FromSeconds(heartbeatSeconds),
            LeaseTtl = TimeSpan.FromSeconds(ttlSeconds),
            RunningPollInterval = TimeSpan.FromSeconds(pollSeconds),
        };
        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Coordination.", error.Message);
        Assert.Throws<InvalidOperationException>(() => new InMemoryExecutionCoordinator(options));
    }

    [Fact]
    public void Default_options_are_valid() => new CoordinationOptions().Validate();

    [Fact]
    public void AddA2AServer_registers_the_in_memory_coordinator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddA2AServer();
        using var sp = services.BuildServiceProvider();
        Assert.IsType<InMemoryExecutionCoordinator>(sp.GetRequiredService<ExecutionCoordinator>());
    }
}

/// <summary>
/// Two Postgres coordinators on one schema stand for two instances. They run on one fake clock (leases are decided on the
/// clock the test gives them, not on the database's), so expiry takes no real time; only the cancel poll runs on real time.
/// </summary>
[Collection(ContainersCollection.Name)]
public sealed class PostgresExecutionCoordinatorTests(PostgresFixture db) : ExecutionCoordinatorContract, IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new();
    private readonly List<PostgresExecutionCoordinator> _cleanup = [];

    protected override bool HasTwoInstances => true;

    protected override CoordinationOptions Options { get; } = new();

    protected override async Task<(ExecutionCoordinator A, ExecutionCoordinator B)> CreatePairAsync()
    {
        var schema = await db.NewSchemaAsync(_clock);
        schema.CancelPollInterval = TimeSpan.FromMilliseconds(20);
        return (await NewAsync(schema), await NewAsync(schema));
    }

    protected override Task PassAsync(TimeSpan time)
    {
        _clock.Advance(time);
        return Task.CompletedTask;
    }

    private async Task<PostgresExecutionCoordinator> NewAsync(PostgresStoreOptions schema)
    {
        var coordinator = new PostgresExecutionCoordinator(db.DataSource, schema, Options, _clock);
        _cleanup.Add(coordinator);
        await coordinator.StartAsync();
        return coordinator;
    }

    [Fact]
    public async Task Lease_expiry_follows_the_injected_clock()
    {
        var (a, _) = await CreatePairAsync();
        await a.RegisterAsync(Key(), "e1");
        _clock.Advance(Options.LeaseTtl - TimeSpan.FromMilliseconds(1));
        Assert.True(await a.IsRunningAsync(Key()));
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(await a.IsRunningAsync(Key()));
    }

    /// <summary>Stale rows are deleted by the next request, so the table cannot grow without bound.</summary>
    [Fact]
    public async Task Old_cancel_requests_are_deleted_by_the_next_request()
    {
        var schema = await db.NewSchemaAsync(_clock);
        var a = await NewAsync(schema);
        for (var i = 0; i < 5; i++) await a.RequestCancelAsync(Key($"t{i}"));
        Assert.Equal(5, await CancelRowsAsync(schema));

        _clock.Advance(TimeSpan.FromMinutes(6));
        await a.RequestCancelAsync(Key("fresh"));

        Assert.Equal(1, await CancelRowsAsync(schema));
    }

    /// <summary>Only requests made after the coordinator started are delivered.</summary>
    [Fact]
    public async Task Requests_made_before_the_coordinator_started_are_not_delivered()
    {
        var schema = await db.NewSchemaAsync(_clock);
        schema.CancelPollInterval = TimeSpan.FromMilliseconds(20);
        var a = await NewAsync(schema);
        await a.RequestCancelAsync(Key("old"));
        var b = await NewAsync(schema);
        using var cts = new CancellationTokenSource(Wait);
        await using var onB = b.CancelRequestsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var next = onB.MoveNextAsync();

        await a.RequestCancelAsync(Key("new")); // delivered after "old" would have been

        Assert.True(await next);
        Assert.Equal(Key("new"), onB.Current);
        cts.Cancel();
    }

    private async Task<long> CancelRowsAsync(PostgresStoreOptions schema)
    {
        await using var cmd = db.DataSource.CreateCommand($"SELECT count(*) FROM \"{schema.Schema}\".a2a_cancel_requests");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _cleanup) await c.DisposeAsync();
    }
}

/// <summary>Two Redis coordinators on one Redis stand for two instances. Leases are short, so expiry takes real time.</summary>
[Collection(ContainersCollection.Name)]
public sealed class RedisExecutionCoordinatorTests(RedisFixture redis) : ExecutionCoordinatorContract, IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _cleanup = [];

    protected override bool HasTwoInstances => true;

    // 5x slack between heartbeat and TTL, so a slow machine does not expire a lease that is being renewed.
    protected override CoordinationOptions Options { get; } = new()
    {
        LeaseTtl = TimeSpan.FromSeconds(1),
        Heartbeat = TimeSpan.FromMilliseconds(200),
        RunningPollInterval = TimeSpan.FromMilliseconds(50),
    };

    protected override async Task<(ExecutionCoordinator A, ExecutionCoordinator B)> CreatePairAsync()
    {
        var prefix = $"coord:{Guid.NewGuid():N}:";
        return (await NewAsync(prefix), await NewAsync(prefix));
    }

    protected override Task PassAsync(TimeSpan time) => Task.Delay(time);

    private async Task<RedisExecutionCoordinator> NewAsync(string prefix)
    {
        var mux = await redis.ConnectAsync();
        var coordinator = new RedisExecutionCoordinator(mux, new RedisEventBusOptions { ChannelPrefix = prefix }, Options);
        await coordinator.StartAsync();
        _cleanup.Add(mux);
        _cleanup.Insert(0, coordinator);
        return coordinator;
    }

    /// <summary>
    /// A cancel reader that falls its capacity (1,024 requests) behind drops the oldest
    /// and logs it. The log runs outside the coordinator's reader lock: while logging, another thread can still add a reader.
    /// </summary>
    [Fact]
    public async Task Dropped_cancel_request_is_logged_outside_the_reader_lock()
    {
        var prefix = $"coord:{Guid.NewGuid():N}:";
        var logger = new DropProbeLogger();
        var mux = await redis.ConnectAsync();
        _cleanup.Add(mux);
        var a = new RedisExecutionCoordinator(mux, new RedisEventBusOptions { ChannelPrefix = prefix }, Options, logger);
        _cleanup.Insert(0, a);
        await a.StartAsync();
        logger.Coordinator = a;
        var b = await NewAsync(prefix);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var reader = a.CancelRequestsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var first = reader.MoveNextAsync();
        await b.RequestCancelAsync(new TaskKey("", "o", "first"));
        Assert.True(await first); // the reader is subscribed; it reads nothing more from here on

        const int readerCapacity = 1024; // RedisExecutionCoordinator.CancelReaderCapacity (internal)
        for (var i = 0; i <= readerCapacity + 5; i++)
            await b.RequestCancelAsync(new TaskKey("", "o", $"t{i}"));

        Assert.True(await logger.Probed.Task.WaitAsync(TimeSpan.FromSeconds(20)), "the drop was logged while the reader lock was held");
    }

    /// <summary>
    /// On the first dropped-request warning, adds a cancel reader from another thread (which takes the reader lock) and waits for
    /// it: true when that succeeds, false when it was blocked (the warning was logged under the lock).
    /// </summary>
    private sealed class DropProbeLogger : ILogger<RedisExecutionCoordinator>
    {
        private int _probed;

        public RedisExecutionCoordinator? Coordinator { get; set; }

        public TaskCompletionSource<bool> Probed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != 1104 || Coordinator is not { } coordinator || Interlocked.Exchange(ref _probed, 1) == 1) return;
            using var stop = new CancellationTokenSource();
            var add = Task.Run(() =>
            {
                var probe = coordinator.CancelRequestsAsync(stop.Token).GetAsyncEnumerator(stop.Token);
                _ = probe.MoveNextAsync(); // runs up to the reader's registration (under the lock), then waits
                return probe;
            });
            var added = add.Wait(TimeSpan.FromSeconds(5));
            stop.Cancel();
            Probed.TrySetResult(added);
        }
    }

    /// <summary>Review Focus 4: an instance whose clock runs 60 s ahead neither expires nor misreads a fresh lease.</summary>
    [Fact]
    public async Task Skewed_instance_clock_does_not_expire_a_fresh_lease()
    {
        var prefix = $"coord:{Guid.NewGuid():N}:";
        var mux = await redis.ConnectAsync();
        _cleanup.Add(mux);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new SkewedClock(TimeSpan.FromSeconds(60)));
        services.AddA2AServer(o =>
            {
                o.Coordination.LeaseTtl = TimeSpan.FromSeconds(30);
                o.Coordination.Heartbeat = TimeSpan.FromSeconds(10);
            })
            .AddRedisEventBus(mux, o => o.ChannelPrefix = prefix);
        await using var sp = services.BuildServiceProvider();
        var skewed = Assert.IsType<RedisExecutionCoordinator>(sp.GetRequiredService<ExecutionCoordinator>());
        var normal = await NewAsync(prefix);

        await normal.RegisterAsync(Key("by-normal"), "e1");
        await skewed.RegisterAsync(Key("by-skewed"), "e2");

        foreach (var c in new ExecutionCoordinator[] { skewed, normal })
        {
            Assert.True(await c.IsRunningAsync(Key("by-normal")));
            Assert.True(await c.IsRunningAsync(Key("by-skewed")));
            Assert.Empty(await c.ListExpiredAsync(10));
        }
    }

    [Fact]
    public async Task Event_bus_registration_can_leave_the_coordinator_out()
    {
        var mux = await redis.ConnectAsync();
        _cleanup.Add(mux);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddA2AServer().AddRedisEventBus(mux, o => o.RegisterCoordinator = false);
        await using var sp = services.BuildServiceProvider();
        Assert.IsType<InMemoryExecutionCoordinator>(sp.GetRequiredService<ExecutionCoordinator>());

        var explicitServices = new ServiceCollection();
        explicitServices.AddLogging();
        explicitServices.AddA2AServer().AddRedisExecutionCoordinator(mux);
        await using var explicitSp = explicitServices.BuildServiceProvider();
        Assert.IsType<RedisExecutionCoordinator>(explicitSp.GetRequiredService<ExecutionCoordinator>());
        Assert.IsType<TaskEventBus>(explicitSp.GetRequiredService<TaskEventBus>()); // the bus stays local
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var d in _cleanup) await d.DisposeAsync();
    }

    private sealed class SkewedClock(TimeSpan skew) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + skew;
    }
}

/// <summary>Registration checks of the Redis package that need no Redis server.</summary>
public sealed class RedisRegistrationTests
{
    [Fact]
    public void Second_registration_with_another_configuration_string_is_refused()
    {
        var services = new ServiceCollection();
        services.AddA2ARedisEventBus("localhost:6379", o => o.RegisterCoordinator = false);
        services.AddA2ARedisExecutionCoordinator("localhost:6379"); // the same connection: fine
        var error = Assert.Throws<InvalidOperationException>(() => services.AddA2ARedisExecutionCoordinator("other-host:6379"));
        Assert.Contains("other-host:6379", error.Message);
        Assert.Contains("localhost:6379", error.Message);
    }
}
