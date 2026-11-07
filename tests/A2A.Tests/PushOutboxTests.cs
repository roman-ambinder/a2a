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

    public PushRig(HttpMessageHandler hooks)
    {
        _hooks = hooks;
        Events = new InMemoryEventStore(new StreamingOptions(), Time);
        Outbox = new InMemoryPushOutbox(Time);
        Tasks = new InMemoryTaskStore(Events, Outbox);
        Options.PushNotifications.AllowPrivateNetworks = true;
    }

    public FakeTimeProvider Time { get; } = new();
    public InMemoryEventStore Events { get; }
    public InMemoryPushOutbox Outbox { get; }
    public InMemoryTaskStore Tasks { get; }
    public InMemoryPushConfigStore Configs { get; } = new();
    public EphemeralPushSecretProtector Protector { get; } = new();
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

    public PushDeliveryService NewService() => new(Outbox, Events, Configs, Protector, new Factory(_hooks),
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

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpStatusCode code;
        lock (Requests)
        {
            Requests.Add(request);
            code = codes.Length == 0 ? HttpStatusCode.OK : codes[Math.Min(_i++, codes.Length - 1)];
        }
        return Task.FromResult(new HttpResponseMessage(code));
    }

    public static string Header(HttpRequestMessage r, string name) => r.Headers.GetValues(name).Single();
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
