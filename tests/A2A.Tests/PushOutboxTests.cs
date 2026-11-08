using System.Diagnostics.Metrics;
using System.Net;
using A2A.Client;
using A2A.Json;
using A2A.Server;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace A2A.Tests;

/// <summary>
/// A push delivery pipeline wired by hand: in-memory task, event and outbox stores on a manual clock, a push config store,
/// and <see cref="PushDeliveryService"/> instances that are driven one pass at a time (<see cref="PushDeliveryService.RunOnceAsync"/>).
/// </summary>
internal sealed class PushRig
{
    public static readonly TaskKey Key = new("", "alice", "t1");

    private readonly HttpMessageHandler _hooks;
    private TaskRecord? _record;

    public PushRig(HttpMessageHandler hooks, int replayBufferSize = 1000)
    {
        _hooks = hooks;
        Events = new InMemoryEventStore(Streaming, Time);
        Streaming.ReplayBufferSize = replayBufferSize;
        Outbox = new InMemoryPushOutbox(Time);
        Tasks = new InMemoryTaskStore(Events, Outbox);
        Options.PushNotifications.AllowPrivateNetworks = true;
    }

    public FakeTimeProvider Time { get; } = new();
    public StreamingOptions Streaming { get; } = new();
    public InMemoryEventStore Events { get; }
    public InMemoryPushOutbox Outbox { get; }
    public InMemoryTaskStore Tasks { get; }
    public InMemoryPushConfigStore Configs { get; } = new();
    public IPushSecretProtector Protector { get; set; } = new EphemeralPushSecretProtector();
    public A2AServerOptions Options { get; } = new();

    /// <summary>Stores <paramref name="config"/> for the task as the handler does: with its secrets protected.</summary>
    public async Task SaveConfigAsync(TaskPushNotificationConfig config, string id = "c1")
    {
        var stored = A2AJson.Clone(config);
        stored.Id = id;
        stored.TaskId = Key.TaskId;
        if (!string.IsNullOrEmpty(stored.Token)) stored.Token = Protector.Protect(stored.Token);
        if (stored.Authentication is { Credentials.Length: > 0 } a) a.Credentials = Protector.Protect(a.Credentials);
        await Configs.SaveAsync(new PushConfigRecord(Key, stored));
    }

    /// <summary>Commits the next event of the task (the first one creates it) with one delivery per config id.</summary>
    public async Task<IReadOnlyList<PushDelivery>> CommitAsync(params string[] configIds)
    {
        var deliveries = configIds.Select(id => new PushDelivery(Key, id, 0, Guid.NewGuid().ToString("N"))).ToList();
        CommitResult committed;
        if (_record is null)
        {
            var now = Time.GetUtcNow();
            var record = new TaskRecord(Key.Tenant, Key.Owner,
                new AgentTask { Id = Key.TaskId, ContextId = "ctx", Status = new AgentTaskStatus { State = TaskState.Submitted } }, 0, now, now);
            committed = await Tasks.CommitAsync(new TaskCommit(record, 0, StreamResponse.Of(A2AJson.Clone(record.Task)), deliveries));
        }
        else
        {
            var evt = StreamResponse.Of(new TaskStatusUpdateEvent
            {
                TaskId = Key.TaskId, ContextId = "ctx", Status = new AgentTaskStatus { State = TaskState.Working },
            });
            committed = await Tasks.CommitAsync(new TaskCommit(_record, _record.Version, evt, deliveries));
        }
        _record = committed.Record;
        return deliveries.Select(d => d with { Sequence = committed.Sequence }).ToList();
    }

    public PushDeliveryService NewService(PushOutbox? outbox = null) => new(outbox ?? Outbox, Configs, Protector, new Factory(_hooks),
        Microsoft.Extensions.Options.Options.Create(Options), logger: null, time: Time);

    /// <summary>Runs passes, moving the clock past every backoff, until the outbox is empty.</summary>
    public async Task DrainAsync(PushDeliveryService service)
    {
        for (var i = 0; i < 100 && Outbox.Count > 0; i++)
        {
            await service.RunOnceAsync(CancellationToken.None);
            Time.Advance(Options.PushDelivery.MaxBackoff * 2);
        }
        Assert.Equal(0, Outbox.Count);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

/// <summary>Answers webhook POSTs with the given status codes in turn (the last one repeats).</summary>
internal sealed class ScriptedHooks(params HttpStatusCode[] codes) : HttpMessageHandler
{
    private int _i;
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>The body of each request, in order (read before the sender disposes the request).</summary>
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        HttpStatusCode code;
        lock (Requests)
        {
            Requests.Add(request);
            Bodies.Add(body);
            code = codes.Length == 0 ? HttpStatusCode.OK : codes[Math.Min(_i++, codes.Length - 1)];
        }
        return new HttpResponseMessage(code);
    }

    public static string Header(HttpRequestMessage r, string name) => r.Headers.GetValues(name).Single();
}

/// <summary>Holds requests to <paramref name="slowHost"/> until <paramref name="release"/> completes (at most 20 s); answers others at once.</summary>
internal sealed class BlockingHooks(string slowHost, Task release) : HttpMessageHandler
{
    private int _slow;
    private int _fast;
    public int Slow => Volatile.Read(ref _slow);
    public int Fast => Volatile.Read(ref _fast);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.Host == slowHost)
        {
            Interlocked.Increment(ref _slow);
            await Task.WhenAny(release, Task.Delay(TimeSpan.FromSeconds(20), ct));
        }
        else Interlocked.Increment(ref _fast);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

/// <summary>Stores secrets as they are and fails every unprotect with an unexpected exception.</summary>
internal sealed class BrokenProtector : IPushSecretProtector
{
    public string Protect(string plaintext) => plaintext;
    public string Unprotect(string protectedValue) => throw new InvalidOperationException("broken");
}

[Collection("PushGuard")]
public class PushOutboxTests
{
    private static TaskPushNotificationConfig Hook(string url = "https://hooks.example/cb") => new() { Url = url, Token = "tok" };

    [Fact]
    public async Task Delivery_survives_service_restart()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.OK);
        var rig = new PushRig(hooks);
        await rig.SaveConfigAsync(Hook());
        var d = (await rig.CommitAsync("c1")).Single();

        // The first instance leases the delivery and dies before sending it.
        using (rig.NewService())
            Assert.Single(await rig.Outbox.LeaseDueAsync(10, rig.Options.PushDelivery.Lease));

        using var restarted = rig.NewService();
        Assert.Equal(0, await restarted.RunOnceAsync(CancellationToken.None)); // still leased by the dead instance
        Assert.Empty(hooks.Requests);

        rig.Time.Advance(rig.Options.PushDelivery.Lease);
        Assert.Equal(1, await restarted.RunOnceAsync(CancellationToken.None));
        var request = Assert.Single(hooks.Requests);
        Assert.Equal(d.DeliveryId, ScriptedHooks.Header(request, A2AProtocol.Headers.DeliveryId));
        Assert.Equal(0, rig.Outbox.Count);
    }

    [Fact]
    public async Task Failed_delivery_retries_with_backoff_then_succeeds()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        var rig = new PushRig(hooks);
        await rig.SaveConfigAsync(Hook());
        await rig.CommitAsync("c1");
        using var service = rig.NewService();

        await service.RunOnceAsync(CancellationToken.None); // 500
        Assert.Single(hooks.Requests);

        // First retry after about 1 s (±20 %).
        rig.Time.Advance(TimeSpan.FromMilliseconds(790));
        Assert.Equal(0, await service.RunOnceAsync(CancellationToken.None));
        rig.Time.Advance(TimeSpan.FromMilliseconds(420));
        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None)); // 500
        Assert.Equal(2, hooks.Requests.Count);

        // Second retry after about 2 s.
        rig.Time.Advance(TimeSpan.FromMilliseconds(1590));
        Assert.Equal(0, await service.RunOnceAsync(CancellationToken.None));
        rig.Time.Advance(TimeSpan.FromMilliseconds(820));
        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None)); // 200
        Assert.Equal(3, hooks.Requests.Count);

        Assert.Equal(0, rig.Outbox.Count);
        Assert.Single(hooks.Requests.Select(r => ScriptedHooks.Header(r, A2AProtocol.Headers.DeliveryId)).Distinct());
        Assert.Empty(await rig.Outbox.ListDeadLettersAsync("", 10));
    }

    [Fact]
    public async Task Dead_letter_after_max_attempts()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.ServiceUnavailable);
        var rig = new PushRig(hooks);
        rig.Options.PushDelivery.MaxAttempts = 3;
        await rig.SaveConfigAsync(Hook());
        var d = (await rig.CommitAsync("c1")).Single();
        using var service = rig.NewService();

        await rig.DrainAsync(service);

        Assert.Equal(3, hooks.Requests.Count);
        var dead = Assert.Single(await rig.Outbox.ListDeadLettersAsync("", 10));
        Assert.Equal(d, dead.Delivery);
        Assert.Equal(3, dead.Attempt);
        Assert.Contains("503", dead.LastError);
    }

    [Fact]
    public async Task Redrive_sends_again()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        var rig = new PushRig(hooks);
        rig.Options.PushDelivery.MaxAttempts = 1;
        await rig.SaveConfigAsync(Hook());
        var d = (await rig.CommitAsync("c1")).Single();
        using var service = rig.NewService();
        await rig.DrainAsync(service);
        var dead = Assert.Single(await rig.Outbox.ListDeadLettersAsync("", 10));

        var admin = new A2APushAdmin(rig.Outbox);
        Assert.Equal(dead, Assert.Single(await admin.ListDeadLettersAsync("", 10)));
        Assert.True(await admin.RedriveAsync(dead.Delivery));
        Assert.False(await admin.RedriveAsync(dead.Delivery)); // no longer a dead letter
        Assert.Empty(await admin.ListDeadLettersAsync("", 10));

        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None));
        Assert.Equal(2, hooks.Requests.Count);
        Assert.Equal(d.DeliveryId, ScriptedHooks.Header(hooks.Requests[1], A2AProtocol.Headers.DeliveryId));
        Assert.Equal(0, rig.Outbox.Count);
    }

    [Fact]
    public async Task Receiver_down_longer_than_terminal_retention_still_gets_the_event()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var rig = new PushRig(hooks);
        await rig.SaveConfigAsync(Hook());
        await rig.CommitAsync();
        await rig.CommitAsync("c1"); // the last event of the task
        await rig.Events.MarkTerminalAsync(PushRig.Key);
        using var service = rig.NewService();

        await service.RunOnceAsync(CancellationToken.None); // 503: the receiver is down
        rig.Time.Advance(rig.Streaming.TerminalRetention + TimeSpan.FromMinutes(1));
        Assert.Empty(await ReadLogAsync(rig)); // the event log of the finished task is gone
        await service.RunOnceAsync(CancellationToken.None); // still down
        rig.Time.Advance(rig.Options.PushDelivery.MaxBackoff);
        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None)); // back

        Assert.Equal(3, hooks.Requests.Count);
        Assert.Equal(0, rig.Outbox.Count);
        Assert.Equal("2", ScriptedHooks.Header(hooks.Requests[2], A2AProtocol.Headers.NotificationSequence));
        Assert.Equal(TaskState.Working, Assert.IsType<StatusUpdateEvent>(A2AJson.Deserialize<StreamResponse>(hooks.Bodies[2])).StatusUpdate.Status.State);
    }

    [Fact]
    public async Task Event_trimmed_from_the_replay_buffer_is_still_delivered()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.OK);
        var rig = new PushRig(hooks, replayBufferSize: 2);
        await rig.SaveConfigAsync(Hook());
        await rig.CommitAsync("c1"); // seq 1, the snapshot
        for (var i = 0; i < 3; i++) await rig.CommitAsync();
        Assert.Equal(3, (await ReadLogAsync(rig))[0].Sequence); // seq 1 was trimmed
        using var service = rig.NewService();

        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None));

        Assert.Equal("1", ScriptedHooks.Header(Assert.Single(hooks.Requests), A2AProtocol.Headers.NotificationSequence));
        Assert.Equal(PushRig.Key.TaskId, Assert.IsType<TaskEvent>(A2AJson.Deserialize<StreamResponse>(hooks.Bodies[0])).Task.Id);
    }

    [Fact]
    public async Task Redrive_after_the_event_was_evicted_is_delivered()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        var rig = new PushRig(hooks);
        rig.Options.PushDelivery.MaxAttempts = 1;
        await rig.SaveConfigAsync(Hook());
        await rig.CommitAsync("c1");
        await rig.Events.MarkTerminalAsync(PushRig.Key);
        using var service = rig.NewService();
        await rig.DrainAsync(service);
        var dead = Assert.Single(await rig.Outbox.ListDeadLettersAsync("", 10));

        rig.Time.Advance(rig.Streaming.TerminalRetention + TimeSpan.FromHours(1));
        Assert.Empty(await ReadLogAsync(rig));
        Assert.True(await new A2APushAdmin(rig.Outbox).RedriveAsync(dead.Delivery));
        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None));

        Assert.Equal(2, hooks.Requests.Count);
        Assert.Equal(hooks.Bodies[0], hooks.Bodies[1]);
        Assert.IsType<TaskEvent>(A2AJson.Deserialize<StreamResponse>(hooks.Bodies[1]));
        Assert.Equal(0, rig.Outbox.Count);
    }

    private static async Task<List<SequencedEvent>> ReadLogAsync(PushRig rig)
    {
        var list = new List<SequencedEvent>();
        await foreach (var e in rig.Events.ReadAfterAsync(PushRig.Key, 0)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Order_is_preserved_per_task_and_config()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        var rig = new PushRig(hooks);
        await rig.SaveConfigAsync(Hook());
        await rig.CommitAsync("c1");
        await rig.CommitAsync("c1");
        await rig.CommitAsync("c1");
        using var service = rig.NewService();

        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None)); // seq 1 fails
        Assert.Equal(0, await service.RunOnceAsync(CancellationToken.None)); // seq 2 and 3 wait behind it
        await rig.DrainAsync(service);

        var sequences = hooks.Requests.Select(r => ScriptedHooks.Header(r, A2AProtocol.Headers.NotificationSequence)).ToList();
        Assert.Equal(["1", "1", "2", "3"], sequences);
    }

    [Fact]
    public async Task Configs_of_one_task_are_delivered_independently()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.OK);
        var rig = new PushRig(hooks);
        await rig.SaveConfigAsync(Hook("https://a.example/cb"), "a");
        await rig.SaveConfigAsync(Hook("https://b.example/cb"), "b");
        await rig.CommitAsync("a", "b");
        using var service = rig.NewService();

        Assert.Equal(2, await service.RunOnceAsync(CancellationToken.None));
        Assert.Equal(["a.example", "b.example"], hooks.Requests.Select(r => r.RequestUri!.Host).Order());
        Assert.Equal(2, hooks.Requests.Select(r => ScriptedHooks.Header(r, A2AProtocol.Headers.DeliveryId)).Distinct().Count());
    }

    [Fact]
    public async Task Blocked_url_is_dead_lettered_without_retry()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.OK);
        var rig = new PushRig(hooks);
        rig.Options.PushNotifications.AllowPrivateNetworks = false; // the guard runs again at delivery
        await rig.SaveConfigAsync(Hook("https://10.0.0.5/cb"));
        await rig.CommitAsync("c1");
        using var service = rig.NewService();

        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None));

        Assert.Empty(hooks.Requests);
        var dead = Assert.Single(await rig.Outbox.ListDeadLettersAsync("", 10));
        Assert.Equal(1, dead.Attempt);
        Assert.Equal(0, rig.Outbox.Count);
    }

    [Fact]
    public async Task Delivery_of_a_deleted_config_is_dropped()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.OK);
        var rig = new PushRig(hooks);
        await rig.SaveConfigAsync(Hook());
        await rig.CommitAsync("c1");
        await rig.Configs.DeleteAsync(PushRig.Key, "c1");
        using var service = rig.NewService();

        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None));

        Assert.Empty(hooks.Requests);
        Assert.Equal(0, rig.Outbox.Count);
        Assert.Empty(await rig.Outbox.ListDeadLettersAsync("", 10));
    }

    [Fact]
    public async Task Counters_record_outcomes()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.InternalServerError, HttpStatusCode.OK, HttpStatusCode.BadRequest);
        var rig = new PushRig(hooks);
        await rig.SaveConfigAsync(Hook());
        var outcomes = new List<string>();
        var deadLetters = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "A2A" && instrument.Name is PushDeliveryService.DeliveriesCounterName or PushDeliveryService.DeadLettersCounterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (outcomes)
            {
                if (instrument.Name == PushDeliveryService.DeadLettersCounterName) deadLetters += value;
                else foreach (var t in tags) if (t.Key == "outcome") outcomes.Add((string)t.Value!);
            }
        });
        listener.Start();

        await rig.CommitAsync("c1");
        await rig.CommitAsync("c1");
        using var service = rig.NewService();
        await rig.DrainAsync(service); // seq 1: 500 then 200; seq 2: 400 (not retryable)

        Assert.Equal(["retried", "sent", "dead_lettered"], outcomes);
        Assert.Equal(1, deadLetters);
    }

    [Fact]
    public async Task One_slow_receiver_does_not_hold_up_the_others()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new BlockingHooks("slow.example", release.Task);
        var rig = new PushRig(hooks);
        await rig.SaveConfigAsync(Hook("https://slow.example/cb"), "slow");
        await rig.SaveConfigAsync(Hook("https://fast.example/cb"), "fast");
        await rig.CommitAsync("slow", "fast");
        for (var i = 0; i < 4; i++) await rig.CommitAsync("fast"); // in order behind the first, one after another
        using var service = rig.NewService();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await service.StartAsync(CancellationToken.None);
        try
        {
            while (hooks.Fast < 5 && clock.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(10);
            Assert.Equal(5, hooks.Fast);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"fast deliveries took {clock.Elapsed}");
            Assert.Equal(1, hooks.Slow);
            Assert.False(release.Task.IsCompleted); // the slow receiver is still holding its request
        }
        finally
        {
            release.TrySetResult();
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The poll wins the service's wait. The first pass then misses a delivery whose commit was not visible yet, and the
    /// commit's signal comes just after the wake-up, before the service waits again. The signal must still wake it: the
    /// delivery goes out without another poll (the clock does not move). Repeated, because the lost wake-up this guards
    /// against depended on thread-pool timing.
    /// </summary>
    [Fact]
    public async Task Signal_just_after_a_poll_wake_is_not_lost()
    {
        for (var i = 0; i < 20; i++)
        {
            var hooks = new ScriptedHooks(HttpStatusCode.OK);
            var rig = new PushRig(hooks);
            await rig.SaveConfigAsync(Hook());
            await rig.CommitAsync("c1");
            var outbox = new MissesFirstPass(rig.Outbox);
            using var service = rig.NewService(outbox);
            await service.StartAsync(CancellationToken.None);
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (rig.Time.PendingTimers == 0 && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(1); // the poll delay is set
                Assert.Equal(1, rig.Time.PendingTimers);
                rig.Time.Advance(rig.Options.PushDelivery.PollInterval); // the poll wins; the first pass signals at once

                clock.Restart();
                while (rig.Outbox.Count != 0 && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(1);
                Assert.True(outbox.Passes > 0, $"run {i}: the poll did not wake the service");
                Assert.True(rig.Outbox.Count == 0, $"run {i}: the signal was lost; the delivery waits for the next poll");
                Assert.Single(hooks.Requests);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Lease_must_outlast_a_send()
    {
        var rig = new PushRig(new ScriptedHooks());
        rig.Options.PushNotifications.Timeout = TimeSpan.FromSeconds(30); // a send may take 2 x 30 s; the default lease is 60 s
        using (var service = rig.NewService())
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            Assert.Contains("PushDelivery.Lease", ex.Message);
        }
        rig.Options.PushDelivery.Lease = TimeSpan.FromSeconds(66);
        using var valid = rig.NewService();
        valid.ValidateOptions();
    }

    [Fact]
    public async Task Lease_check_rejects_infinite_and_non_positive_timeouts_without_overflow()
    {
        foreach (var timeout in new[] { Timeout.InfiniteTimeSpan, TimeSpan.Zero, TimeSpan.FromSeconds(-1), TimeSpan.MaxValue, TimeSpan.MinValue })
        {
            var rig = new PushRig(new ScriptedHooks());
            rig.Options.PushNotifications.Timeout = timeout;
            using var service = rig.NewService();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            Assert.Contains("PushNotifications.Timeout", ex.Message);
        }
        var huge = new PushRig(new ScriptedHooks());
        huge.Options.PushNotifications.Timeout = TimeSpan.MaxValue;
        huge.Options.PushDelivery.Lease = TimeSpan.MaxValue;
        using var hugeService = huge.NewService();
        Assert.Contains("PushDelivery.Lease", Assert.Throws<InvalidOperationException>(hugeService.ValidateOptions).Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Poll_interval_must_be_positive(int seconds)
    {
        var rig = new PushRig(new ScriptedHooks());
        rig.Options.PushDelivery.PollInterval = seconds == 0 ? TimeSpan.Zero : Timeout.InfiniteTimeSpan;
        using var service = rig.NewService();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Contains("PushDelivery.PollInterval", ex.Message);
    }

    /// <summary>
    /// Delegates to <paramref name="inner"/>, except that its first <see cref="LeaseDueAsync"/> (the service's first pass)
    /// leases nothing, as if the pending delivery's commit were not visible yet, and signals at once, as that commit does.
    /// </summary>
    private sealed class MissesFirstPass(InMemoryPushOutbox inner) : PushOutbox
    {
        private int _passes;
        public int Passes => Volatile.Read(ref _passes);

        public override ValueTask<IReadOnlyList<OutboxEntry>> LeaseDueAsync(int max, TimeSpan lease, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _passes) > 1) return inner.LeaseDueAsync(max, lease, ct);
            NotifyPending();
            return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>([]);
        }

        public override ValueTask<bool> CompleteAsync(OutboxEntry leased, CancellationToken ct = default) => inner.CompleteAsync(leased, ct);
        public override ValueTask<bool> RescheduleAsync(OutboxEntry leased, int attempt, DateTimeOffset dueAt, string error, CancellationToken ct = default) => inner.RescheduleAsync(leased, attempt, dueAt, error, ct);
        public override ValueTask<bool> DeadLetterAsync(OutboxEntry leased, string error, CancellationToken ct = default) => inner.DeadLetterAsync(leased, error, ct);
        public override ValueTask<IReadOnlyList<OutboxEntry>> ListDeadLettersAsync(string tenant, int max, CancellationToken ct = default) => inner.ListDeadLettersAsync(tenant, max, ct);
        public override ValueTask<bool> RedriveAsync(PushDelivery d, CancellationToken ct = default) => inner.RedriveAsync(d, ct);
        public override ValueTask DeleteAllAsync(TaskKey task, CancellationToken ct = default) => inner.DeleteAllAsync(task, ct);
    }

    /// <summary>An outbox whose CompleteAsync fails once, then works. Everything else goes to the inner outbox.</summary>
    private sealed class CompleteFailsOnce(InMemoryPushOutbox inner) : PushOutbox
    {
        private int _failed;
        public override ValueTask<IReadOnlyList<OutboxEntry>> LeaseDueAsync(int max, TimeSpan lease, CancellationToken ct = default) => inner.LeaseDueAsync(max, lease, ct);
        public override ValueTask<bool> CompleteAsync(OutboxEntry leased, CancellationToken ct = default) =>
            Interlocked.Exchange(ref _failed, 1) == 0 ? throw new InvalidOperationException("outbox is down") : inner.CompleteAsync(leased, ct);
        public override ValueTask<bool> RescheduleAsync(OutboxEntry leased, int attempt, DateTimeOffset dueAt, string error, CancellationToken ct = default) => inner.RescheduleAsync(leased, attempt, dueAt, error, ct);
        public override ValueTask<bool> DeadLetterAsync(OutboxEntry leased, string error, CancellationToken ct = default) => inner.DeadLetterAsync(leased, error, ct);
        public override ValueTask<IReadOnlyList<OutboxEntry>> ListDeadLettersAsync(string tenant, int max, CancellationToken ct = default) => inner.ListDeadLettersAsync(tenant, max, ct);
        public override ValueTask<bool> RedriveAsync(PushDelivery d, CancellationToken ct = default) => inner.RedriveAsync(d, ct);
        public override ValueTask DeleteAllAsync(TaskKey task, CancellationToken ct = default) => inner.DeleteAllAsync(task, ct);
    }

    [Fact]
    public async Task Failed_complete_after_a_send_waits_for_the_lease_and_does_not_count_a_failed_attempt()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.OK);
        var rig = new PushRig(hooks);
        await rig.SaveConfigAsync(Hook());
        await rig.CommitAsync("c1");
        var flaky = new CompleteFailsOnce(rig.Outbox);
        using var service = rig.NewService(flaky);

        long sent = 0, retried = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "A2A" && instrument.Name == PushDeliveryService.DeliveriesCounterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key != "outcome") continue;
                if ((string?)tag.Value == "sent") Interlocked.Add(ref sent, value);
                if ((string?)tag.Value == "retried") Interlocked.Add(ref retried, value);
            }
        });
        listener.Start();

        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None));
        Assert.Single(hooks.Requests);
        Assert.Equal(0, Interlocked.Read(ref sent)); // not recorded: the outbox still holds the delivery
        Assert.Equal(0, Interlocked.Read(ref retried));

        rig.Time.Advance(TimeSpan.FromSeconds(30)); // past any backoff, inside the lease
        Assert.Equal(0, await service.RunOnceAsync(CancellationToken.None)); // still leased, not rescheduled
        Assert.Single(hooks.Requests);

        rig.Time.Advance(rig.Options.PushDelivery.Lease); // the lease ends: at-least-once sends it again, as attempt 1
        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None));
        Assert.Equal(2, hooks.Requests.Count);
        Assert.Equal(0, rig.Outbox.Count);
        Assert.Equal(1, Interlocked.Read(ref sent));
        Assert.Empty(await rig.Outbox.ListDeadLettersAsync("", 10));
    }

    [Fact]
    public async Task Redrive_on_a_key_collision_keeps_the_pending_entry_and_the_dead_letter()
    {
        var rig = new PushRig(new ScriptedHooks());
        await rig.SaveConfigAsync(Hook());
        var d = (await rig.CommitAsync("c1")).Single();
        var dead = Assert.Single(await rig.Outbox.LeaseDueAsync(10, TimeSpan.FromMinutes(1)));
        Assert.True(await rig.Outbox.DeadLetterAsync(dead, "HTTP 500"));
        rig.Outbox.Add([d], dead.Payload); // a new entry with the dead letter's key (a task log that restarted at this sequence)
        var leased = Assert.Single(await rig.Outbox.LeaseDueAsync(10, TimeSpan.FromMinutes(1)));

        Assert.False(await rig.Outbox.RedriveAsync(d));

        Assert.Single(await rig.Outbox.ListDeadLettersAsync("", 10));
        Assert.Empty(await rig.Outbox.LeaseDueAsync(10, TimeSpan.FromMinutes(1)));
        Assert.True(await rig.Outbox.CompleteAsync(leased));
    }

    [Fact]
    public async Task Unexpected_failure_counts_as_an_attempt_and_ends_as_a_dead_letter()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.OK);
        var rig = new PushRig(hooks) { Protector = new BrokenProtector() };
        rig.Options.PushDelivery.MaxAttempts = 3;
        await rig.SaveConfigAsync(Hook());
        await rig.CommitAsync("c1");
        using var service = rig.NewService();

        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None));
        rig.Time.Advance(TimeSpan.FromSeconds(1.2)); // rescheduled with backoff, not left leased until the lease ends
        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None));
        await rig.DrainAsync(service);

        Assert.Empty(hooks.Requests);
        var dead = Assert.Single(await rig.Outbox.ListDeadLettersAsync("", 10));
        Assert.Equal(3, dead.Attempt);
        Assert.Contains(nameof(InvalidOperationException), dead.LastError);
    }

    [Fact]
    public async Task Failed_create_commit_removes_the_inline_push_config()
    {
        var store = new FailingCreateStore();
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddTaskStore(store));
        store.Configs = host.App.Services.GetRequiredService<PushConfigStore>();

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(ProtocolBindings.JsonRpc).SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser("task"),
            Configuration = new SendMessageConfiguration { TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = "http://hooks.test/x", Id = "hook" } },
        }));
        await host.WaitIdleAsync();

        Assert.Equal(A2AErrorCode.Internal, ex.Code);
        Assert.True(store.ConfigSavedBeforeCommit);
        Assert.Null(await store.Configs.GetAsync(store.FailedTask!.Value, "hook"));
        Assert.Equal(0, store.Outbox.Count);
    }

    private sealed class FailingCreateStore : DelegatingTaskStore
    {
        public PushConfigStore? Configs { get; set; }
        public TaskKey? FailedTask { get; private set; }
        public bool ConfigSavedBeforeCommit { get; private set; }

        public override async ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default)
        {
            if (!commit.IsCreate) return await base.CommitAsync(commit, ct);
            FailedTask = commit.Record.Key;
            ConfigSavedBeforeCommit = await Configs!.GetAsync(commit.Record.Key, "hook", ct) is not null;
            throw new InvalidOperationException("the store is down");
        }
    }

    [Fact]
    public async Task Handler_commits_one_delivery_per_push_config()
    {
        var store = new RecordingDeliveriesStore();
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddTaskStore(store));
        var client = host.Client(ProtocolBindings.JsonRpc);

        var task = (await client.SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser("input"),
            Configuration = new SendMessageConfiguration { TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = "http://hooks.test/one", Id = "one" } },
        })).RequireTask();
        await host.WaitIdleAsync();
        Assert.All(store.Commits, c => Assert.Equal(["one"], c.Select(d => d.ConfigId)));

        await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = task.Id, Url = "http://hooks.test/two", Id = "two" });
        var before = store.Commits.Count;
        await client.SendMessageAsync(Message.FromUser("more", taskId: task.Id));
        await host.WaitIdleAsync();

        var later = store.Commits.Skip(before).ToList();
        Assert.NotEmpty(later);
        Assert.All(later, c => Assert.Equal(["one", "two"], c.Select(d => d.ConfigId).Order()));
        Assert.Equal(store.Commits.Sum(c => c.Count), store.Commits.SelectMany(c => c).Select(d => d.DeliveryId).Distinct().Count());

        // Everything committed was delivered, each to its own webhook.
        Assert.Equal(store.Commits.Count, host.Webhooks.Calls.Count(c => c.Request.RequestUri!.AbsolutePath == "/one"));
        Assert.Equal(later.Count, host.Webhooks.Calls.Count(c => c.Request.RequestUri!.AbsolutePath == "/two"));
    }

    private sealed class RecordingDeliveriesStore : DelegatingTaskStore
    {
        public List<IReadOnlyList<PushDelivery>> Commits { get; } = [];

        public override ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default)
        {
            lock (Commits) Commits.Add(commit.Deliveries);
            return base.CommitAsync(commit, ct);
        }
    }
}
