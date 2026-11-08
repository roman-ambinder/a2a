using A2A.Server;
using A2A.Server.Retention;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace A2A.Tests;

/// <summary>
/// Artifact objects leave with their task (<see cref="A2ATaskAdmin"/>), and the retention sweep removes the ones no live task
/// refers to. The worst failure is deleting an object of a live task, so every doubt (a failed read, an odd key) keeps it.
/// </summary>
public sealed class ArtifactCleanupTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromHours(1);
    private static readonly Uri Base = new(TestAgentHost.BaseUrl);

    /// <summary>Records every delete, in order, and can fail them.</summary>
    private sealed class RecordingObjects(List<string> log, TimeProvider time) : ArtifactObjectStore
    {
        public InMemoryArtifactObjectStore Inner { get; } = new(time);
        public bool FailDeletes { get; set; }

        public override ValueTask<ArtifactObjectWriteResult> PutAsync(string key, Stream content, ArtifactObjectInfo info, long maxBytes, CancellationToken cancellationToken = default) =>
            Inner.PutAsync(key, content, info, maxBytes, cancellationToken);

        public override ValueTask<ArtifactObjectContent?> OpenReadAsync(string key, ArtifactByteRange? range = null, CancellationToken cancellationToken = default) =>
            Inner.OpenReadAsync(key, range, cancellationToken);

        public override ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            lock (log) log.Add("object:" + key);
            if (FailDeletes) throw new IOException("bucket down");
            return Inner.DeleteAsync(key, cancellationToken);
        }

        public override IAsyncEnumerable<ArtifactObjectEntry> ListAsync(string prefix, CancellationToken cancellationToken = default) =>
            Inner.ListAsync(prefix, cancellationToken);
    }

    /// <summary>Logs task deletes and can fail the maintenance read.</summary>
    private sealed class RecordingTasks(List<string> log) : DelegatingTaskStore
    {
        public bool FailMaintenanceRead { get; set; }
        public int MaintenanceReads { get; private set; }

        public override ValueTask<bool> DeleteAsync(TaskKey key, CancellationToken ct = default)
        {
            lock (log) log.Add("task:" + key.TaskId);
            return Inner.DeleteAsync(key, ct);
        }

        public override ValueTask<TaskRecord?> GetForMaintenanceAsync(string tenant, string taskId, CancellationToken ct = default)
        {
            MaintenanceReads++;
            if (FailMaintenanceRead) throw new IOException("database down");
            return Inner.GetForMaintenanceAsync(tenant, taskId, ct);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required FakeTimeProvider Clock { get; init; }
        public required List<string> Log { get; init; }
        public required RecordingTasks Tasks { get; init; }
        public RecordingObjects? Objects { get; init; }
        public required TestAgentHost Host { get; init; }

        public A2ATaskAdmin Admin => Host.App.Services.GetRequiredService<A2ATaskAdmin>();

        public A2ARetentionService Sweeper(Action<A2ARetentionOptions>? configure = null)
        {
            var options = new A2ARetentionOptions();
            configure?.Invoke(options);
            return ActivatorUtilities.CreateInstance<A2ARetentionService>(Host.App.Services, Options.Create(options));
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private static async Task<Rig> StartAsync(bool withStore = true, TimeSpan? grace = null, TimeSpan? interval = null)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var log = new List<string>();
        var tasks = new RecordingTasks(log);
        var objects = withStore ? new RecordingObjects(log, clock) : null;
        var host = await TestAgentHost.StartAsync(
            o =>
            {
                o.ArtifactStorage.PublicBaseUrl = Base;
                o.ArtifactStorage.OrphanGracePeriod = grace ?? Grace;
                if (interval is { } i) o.ArtifactStorage.OrphanSweepInterval = i;
            },
            services: s =>
            {
                s.AddSingleton<TimeProvider>(clock);
                s.AddTaskStore(tasks);
                if (objects is not null) s.AddSingleton<ArtifactObjectStore>(objects);
            });
        return new Rig { Clock = clock, Log = log, Tasks = tasks, Objects = objects, Host = host };
    }

    private static Message AgentMessage(Uri url) =>
        new() { MessageId = Guid.NewGuid().ToString(), Role = Role.Agent, Parts = [Part.FromUrl(url)] };

    /// <summary>Creates a task that refers to <paramref name="referenced"/> object ids in an artifact.</summary>
    private static async Task<TaskKey> CreateTaskAsync(Rig rig, string tenant, string owner, string taskId, params string[] referenced)
    {
        var now = rig.Clock.GetUtcNow();
        var task = new AgentTask
        {
            Id = taskId, ContextId = "ctx", Status = new AgentTaskStatus { State = TaskState.Completed, Timestamp = now },
            Artifacts = referenced.Length == 0 ? null :
            [
                new Artifact { ArtifactId = "a", Parts = referenced.Select(id => (Part)Part.FromUrl(ArtifactLink.Create(Base, tenant, taskId, id))).ToList() },
            ],
        };
        var record = await rig.Tasks.CreateAsync(new TaskRecord(tenant, owner, task, 0, now, now));
        return record.Key;
    }

    private static async Task<string> PutAsync(Rig rig, string tenant, string taskId)
    {
        var id = ArtifactObjectKey.NewObjectId();
        await PutKeyAsync(rig, ArtifactObjectKey.Create(tenant, taskId, id));
        return id;
    }

    private static Task PutKeyAsync(Rig rig, string key) =>
        rig.Objects!.PutAsync(key, new MemoryStream([1, 2, 3]), new ArtifactObjectInfo(null, null), long.MaxValue).AsTask();

    private static async Task<bool> ExistsAsync(Rig rig, string key) => await rig.Objects!.ExistsAsync(key);

    private static string KeyOf(string tenant, string taskId, string objectId) => ArtifactObjectKey.Create(tenant, taskId, objectId);

    [Fact]
    public async Task Purge_deletes_task_then_objects()
    {
        await using var rig = await StartAsync();
        var key = await CreateTaskAsync(rig, "", "alice", "t1");
        var a = await PutAsync(rig, "", "t1");
        var b = await PutAsync(rig, "", "t1");
        var otherTask = await PutAsync(rig, "", "t10"); // t1's prefix must not match t10
        var otherTenant = await PutAsync(rig, "acme", "t1");
        rig.Log.Clear();

        Assert.True(await rig.Admin.DeleteTaskAsync(key));

        Assert.Equal("task:t1", rig.Log[0]); // the row goes first: never a task that points at nothing
        Assert.Equal(
            new[] { "object:" + KeyOf("", "t1", a), "object:" + KeyOf("", "t1", b) }.Order(),
            rig.Log.Skip(1).Order());
        Assert.False(await ExistsAsync(rig, KeyOf("", "t1", a)));
        Assert.False(await ExistsAsync(rig, KeyOf("", "t1", b)));
        Assert.True(await ExistsAsync(rig, KeyOf("", "t10", otherTask)));
        Assert.True(await ExistsAsync(rig, KeyOf("acme", "t1", otherTenant)));
    }

    [Fact]
    public async Task Purge_succeeds_when_object_delete_fails()
    {
        await using var rig = await StartAsync();
        var key = await CreateTaskAsync(rig, "", "alice", "t1");
        var a = await PutAsync(rig, "", "t1");
        rig.Objects!.FailDeletes = true;

        Assert.True(await rig.Admin.DeleteTaskAsync(key));

        Assert.Null(await rig.Tasks.GetAsync(key));
        Assert.True(await ExistsAsync(rig, KeyOf("", "t1", a))); // left to the sweeper
    }

    [Fact]
    public async Task Sweep_deletes_object_of_missing_task_after_grace()
    {
        await using var rig = await StartAsync();
        var id = await PutAsync(rig, "", "gone");
        rig.Clock.Advance(Grace + TimeSpan.FromMinutes(1));

        await rig.Sweeper().RunOnceAsync(CancellationToken.None);

        Assert.False(await ExistsAsync(rig, KeyOf("", "gone", id)));
    }

    [Fact]
    public async Task Sweep_keeps_young_orphan()
    {
        await using var rig = await StartAsync();
        var id = await PutAsync(rig, "", "gone"); // crash window: object written, task not committed (yet)
        rig.Clock.Advance(Grace - TimeSpan.FromMinutes(1));

        await rig.Sweeper().RunOnceAsync(CancellationToken.None);
        Assert.True(await ExistsAsync(rig, KeyOf("", "gone", id)));

        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        await rig.Sweeper().RunOnceAsync(CancellationToken.None);
        Assert.False(await ExistsAsync(rig, KeyOf("", "gone", id)));
    }

    [Fact]
    public async Task Sweep_deletes_object_not_referenced_by_its_task()
    {
        await using var rig = await StartAsync();
        var kept = ArtifactObjectKey.NewObjectId();
        await PutKeyAsync(rig, KeyOf("", "t1", kept));
        var stray = await PutAsync(rig, "", "t1");
        await CreateTaskAsync(rig, "", "alice", "t1", kept);
        rig.Clock.Advance(Grace + TimeSpan.FromMinutes(1));

        await rig.Sweeper().RunOnceAsync(CancellationToken.None);

        Assert.True(await ExistsAsync(rig, KeyOf("", "t1", kept)));
        Assert.False(await ExistsAsync(rig, KeyOf("", "t1", stray)));
    }

    [Fact]
    public async Task Sweep_keeps_referenced_object_of_any_owner()
    {
        await using var rig = await StartAsync();
        var alice = ArtifactObjectKey.NewObjectId();
        var bob = ArtifactObjectKey.NewObjectId();
        var inHistory = ArtifactObjectKey.NewObjectId();
        var inStatus = ArtifactObjectKey.NewObjectId();
        await PutKeyAsync(rig, KeyOf("", "ta", alice));
        await PutKeyAsync(rig, KeyOf("acme", "tb", bob));
        await PutKeyAsync(rig, KeyOf("", "tc", inHistory));
        await PutKeyAsync(rig, KeyOf("", "td", inStatus));
        await CreateTaskAsync(rig, "", "alice", "ta", alice);
        await CreateTaskAsync(rig, "acme", "bob", "tb", bob);
        var now = rig.Clock.GetUtcNow();
        await rig.Tasks.CreateAsync(new TaskRecord("", "carol", new AgentTask
        {
            Id = "tc", ContextId = "ctx", Status = new AgentTaskStatus { State = TaskState.Completed },
            History = [AgentMessage(ArtifactLink.Create(Base, "", "tc", inHistory))],
        }, 0, now, now));
        await rig.Tasks.CreateAsync(new TaskRecord("", "dave", new AgentTask
        {
            Id = "td", ContextId = "ctx",
            Status = new AgentTaskStatus { State = TaskState.InputRequired, Message = AgentMessage(ArtifactLink.Create(Base, "", "td", inStatus)) },
        }, 0, now, now));
        rig.Clock.Advance(Grace + TimeSpan.FromMinutes(1)); // past the grace, before the tasks expire

        await rig.Sweeper().RunOnceAsync(CancellationToken.None);

        Assert.True(await ExistsAsync(rig, KeyOf("", "ta", alice)));
        Assert.True(await ExistsAsync(rig, KeyOf("acme", "tb", bob)));
        Assert.True(await ExistsAsync(rig, KeyOf("", "tc", inHistory)));
        Assert.True(await ExistsAsync(rig, KeyOf("", "td", inStatus)));
    }

    [Fact]
    public async Task Sweep_deletes_nothing_when_task_read_fails()
    {
        await using var rig = await StartAsync();
        var live = ArtifactObjectKey.NewObjectId();
        await PutKeyAsync(rig, KeyOf("", "t1", live));
        await CreateTaskAsync(rig, "", "alice", "t1", live);
        var orphan = await PutAsync(rig, "", "gone");
        rig.Clock.Advance(Grace + TimeSpan.FromMinutes(1));
        rig.Tasks.FailMaintenanceRead = true;

        await rig.Sweeper().RunOnceAsync(CancellationToken.None);

        Assert.True(rig.Tasks.MaintenanceReads > 0);
        Assert.True(await ExistsAsync(rig, KeyOf("", "t1", live)));
        Assert.True(await ExistsAsync(rig, KeyOf("", "gone", orphan))); // any doubt keeps it
        Assert.DoesNotContain(rig.Log, e => e.StartsWith("object:", StringComparison.Ordinal));

        rig.Tasks.FailMaintenanceRead = false; // the next sweep, with the database back
        await rig.Sweeper().RunOnceAsync(CancellationToken.None);
        Assert.True(await ExistsAsync(rig, KeyOf("", "t1", live)));
        Assert.False(await ExistsAsync(rig, KeyOf("", "gone", orphan)));
    }

    [Fact]
    public async Task Sweep_skips_keys_it_cannot_parse()
    {
        await using var rig = await StartAsync();
        string[] keys = ["readme.txt", "a/b", "a/b/c/d", "_/t1/NOT-AN-OBJECT-ID", "%5F/t1/" + ArtifactObjectKey.NewObjectId()];
        foreach (var k in keys) await PutKeyAsync(rig, k);
        rig.Clock.Advance(Grace + TimeSpan.FromMinutes(1));

        await rig.Sweeper().RunOnceAsync(CancellationToken.None);

        foreach (var k in keys) Assert.True(await ExistsAsync(rig, k), k);
        Assert.Equal(0, rig.Tasks.MaintenanceReads);
    }

    [Fact]
    public async Task Sweep_deletes_at_most_batch_size_objects()
    {
        await using var rig = await StartAsync();
        var ids = new List<string>();
        for (var i = 0; i < 5; i++) ids.Add(await PutAsync(rig, "", "gone"));
        rig.Clock.Advance(Grace + TimeSpan.FromMinutes(1));

        await rig.Sweeper(o => o.BatchSize = 2).RunOnceAsync(CancellationToken.None);
        Assert.Equal(3, await CountAsync(rig, ids));

        await rig.Sweeper(o => o.BatchSize = 2).RunOnceAsync(CancellationToken.None);
        await rig.Sweeper(o => o.BatchSize = 2).RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, await CountAsync(rig, ids));
    }

    private static async Task<int> CountAsync(Rig rig, List<string> ids)
    {
        var n = 0;
        foreach (var id in ids) if (await ExistsAsync(rig, KeyOf("", "gone", id))) n++;
        return n;
    }

    [Fact]
    public async Task No_sweep_without_a_store()
    {
        await using var rig = await StartAsync(withStore: false);
        await CreateTaskAsync(rig, "", "alice", "t1");
        rig.Clock.Advance(Grace + TimeSpan.FromMinutes(1));

        await rig.Sweeper().RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, rig.Tasks.MaintenanceReads);
        Assert.NotNull(await rig.Tasks.GetForMaintenanceAsync("", "t1")); // nothing else changed
    }

    [Fact]
    public async Task Zero_grace_period_is_rejected_at_start()
    {
        await using var rig = await StartAsync(grace: TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Sweeper().StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Negative_grace_period_is_rejected_at_start()
    {
        await using var rig = await StartAsync(grace: TimeSpan.FromMinutes(-5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Sweeper().StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Non_positive_grace_period_without_a_store_still_starts()
    {
        await using var rig = await StartAsync(withStore: false, grace: TimeSpan.Zero);
        var sweeper = rig.Sweeper();

        await sweeper.StartAsync(CancellationToken.None);
        await sweeper.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Orphan_step_runs_at_most_once_per_interval()
    {
        await using var rig = await StartAsync();
        var live = ArtifactObjectKey.NewObjectId();
        await PutKeyAsync(rig, KeyOf("", "t1", live));
        await CreateTaskAsync(rig, "", "alice", "t1", live);
        var sweeper = rig.Sweeper();
        rig.Clock.Advance(Grace + TimeSpan.FromMinutes(1));
        await sweeper.RunOnceAsync(CancellationToken.None); // the first sweep runs the step
        var reads = rig.Tasks.MaintenanceReads;
        Assert.True(reads > 0);

        var orphan = await PutAsync(rig, "", "gone");
        rig.Clock.Advance(Grace + TimeSpan.FromMinutes(1)); // old enough to delete, but the interval (6 hours) has not passed
        await sweeper.RunOnceAsync(CancellationToken.None);
        Assert.Equal(reads, rig.Tasks.MaintenanceReads);
        Assert.True(await ExistsAsync(rig, KeyOf("", "gone", orphan)));

        rig.Clock.Advance(TimeSpan.FromHours(6));
        await sweeper.RunOnceAsync(CancellationToken.None);
        Assert.False(await ExistsAsync(rig, KeyOf("", "gone", orphan)));
        Assert.True(await ExistsAsync(rig, KeyOf("", "t1", live))); // a live object survives
    }

    [Fact]
    public async Task Zero_orphan_sweep_interval_is_rejected_at_start()
    {
        await using var rig = await StartAsync(interval: TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Sweeper().StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Non_positive_orphan_sweep_interval_without_a_store_still_starts()
    {
        await using var rig = await StartAsync(withStore: false, interval: TimeSpan.Zero);
        var sweeper = rig.Sweeper();

        await sweeper.StartAsync(CancellationToken.None);
        await sweeper.StopAsync(CancellationToken.None);
    }
}
