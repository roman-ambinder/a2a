using System.Net;
using A2A.Json;
using A2A.Server;
using A2A.Server.Push;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests;

/// <summary>
/// A webhook registered after the event it should see (CreateTaskPushNotificationConfig on a finished or waiting task) gets
/// one delivery of the task snapshot, and an event committed while a webhook registers is not missed.
/// </summary>
public class PushLateRegistrationTests
{
    private static TaskKey Key(string taskId) => new("", "alice", taskId);

    private static TaskPushNotificationConfig Hook(string taskId, string path = "cb") => new() { TaskId = taskId, Url = $"https://hooks.example/{path}" };

    private static List<(HttpRequestMessage Request, string Body)> CallsTo(TestAgentHost host, string path) =>
        host.Webhooks.Calls.Where(c => c.Request.RequestUri!.AbsolutePath == "/" + path).ToList();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The condition never became true.");
            await Task.Delay(10);
        }
    }

    private static async Task WaitForStateAsync(A2A.Client.A2AClient client, string taskId, TaskState state)
    {
        for (var i = 0; i < 500; i++)
        {
            if ((await client.GetTaskAsync(taskId)).Status.State == state) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Task {taskId} never reached {state}.");
    }

    /// <summary>A config store that runs <see cref="AfterList"/> after each listing (n counts the listings since it was set).</summary>
    private sealed class HookedConfigStore(InMemoryPushConfigStore inner) : PushConfigStore
    {
        private int _lists;

        public InMemoryPushConfigStore Inner { get; } = inner;

        public Func<int, ValueTask>? AfterList { get; set; }

        public void Arm(Func<int, ValueTask> afterList)
        {
            _lists = 0;
            AfterList = afterList;
        }

        public override async ValueTask<IReadOnlyList<PushConfigRecord>> ListAsync(TaskKey task, CancellationToken cancellationToken = default)
        {
            var list = await Inner.ListAsync(task, cancellationToken);
            if (AfterList is { } after) await after(Interlocked.Increment(ref _lists));
            return list;
        }

        public override ValueTask SaveAsync(PushConfigRecord record, CancellationToken cancellationToken = default) => Inner.SaveAsync(record, cancellationToken);
        public override ValueTask<PushConfigRecord?> GetAsync(TaskKey task, string configId, CancellationToken cancellationToken = default) => Inner.GetAsync(task, configId, cancellationToken);
        public override ValueTask<int> CountAsync(TaskKey task, CancellationToken cancellationToken = default) => Inner.CountAsync(task, cancellationToken);
        public override ValueTask<bool> DeleteAsync(TaskKey task, string configId, CancellationToken cancellationToken = default) => Inner.DeleteAsync(task, configId, cancellationToken);
        public override ValueTask DeleteAllAsync(TaskKey task, CancellationToken cancellationToken = default) => Inner.DeleteAllAsync(task, cancellationToken);
        public override ValueTask<bool> SaveIfUnderLimitAsync(PushConfigRecord record, int limit, CancellationToken cancellationToken = default) => Inner.SaveIfUnderLimitAsync(record, limit, cancellationToken);
    }

    private static async Task<(TestAgentHost Host, HookedConfigStore Store)> StartHookedAsync(Action<A2AServerOptions>? configure = null)
    {
        var store = new HookedConfigStore(new InMemoryPushConfigStore());
        var host = await TestAgentHost.StartAsync(configure, services: s => s.Replace(ServiceDescriptor.Singleton<PushConfigStore>(store)));
        return (host, store);
    }

    private static async Task SaveLateAsync(HookedConfigStore store, string taskId, string id) =>
        await store.Inner.SaveAsync(new PushConfigRecord(Key(taskId), new TaskPushNotificationConfig { Id = id, TaskId = taskId, Url = $"https://hooks.example/{id}" }));

    private static Task StopDeliveryAsync(TestAgentHost host) =>
        host.App.Services.GetRequiredService<PushDeliveryService>().StopAsync(CancellationToken.None);

    [Fact]
    public async Task Registration_on_a_finished_task_delivers_the_snapshot_once()
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync("task")).RequireTask();
        Assert.Equal(TaskState.Completed, task.Status.State);
        await host.WaitIdleAsync();
        Assert.Empty(host.Webhooks.Calls);

        await client.CreateTaskPushNotificationConfigAsync(Hook(task.Id));
        await WaitUntilAsync(() => host.Webhooks.Calls.Count > 0);
        await host.WaitIdleAsync();
        await Task.Delay(100); // negative check: no second delivery appears in this time

        var call = Assert.Single(host.Webhooks.Calls);
        var snapshot = Assert.IsType<TaskEvent>(A2AJson.Deserialize<StreamResponse>(call.Body)).Task;
        Assert.Equal(task.Id, snapshot.Id);
        Assert.Equal(TaskState.Completed, snapshot.Status.State);
        var last = await host.App.Services.GetRequiredService<EventStore>().LastSequenceAsync(Key(task.Id));
        Assert.Equal(last.ToString(), call.Request.Headers.GetValues(A2AProtocol.Headers.NotificationSequence).Single());
    }

    [Fact]
    public async Task Registration_on_a_running_task_delivers_nothing_until_the_next_event()
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync(Message.FromUser("gate late-running"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();
        await WaitForStateAsync(client, task.Id, TaskState.Working);

        await client.CreateTaskPushNotificationConfigAsync(Hook(task.Id));
        await host.WaitForPushIdleAsync();
        await Task.Delay(100); // negative check: no delivery appears in this time
        Assert.Empty(host.Webhooks.Calls);

        ScenarioAgent.Gate("gate late-running").SetResult();
        await host.WaitIdleAsync();

        var call = Assert.Single(host.Webhooks.Calls);
        var update = Assert.IsType<StatusUpdateEvent>(A2AJson.Deserialize<StreamResponse>(call.Body));
        Assert.Equal(TaskState.Completed, update.StatusUpdate.Status.State);
    }

    [Fact]
    public async Task Registration_on_an_input_required_task_delivers_the_snapshot()
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync("input")).RequireTask();
        Assert.Equal(TaskState.InputRequired, task.Status.State);
        await host.WaitIdleAsync();

        await client.CreateTaskPushNotificationConfigAsync(Hook(task.Id));
        await host.WaitIdleAsync();

        var snapshot = Assert.IsType<TaskEvent>(A2AJson.Deserialize<StreamResponse>(Assert.Single(host.Webhooks.Calls).Body)).Task;
        Assert.Equal(TaskState.InputRequired, snapshot.Status.State);
    }

    [Fact]
    public async Task Registration_catch_up_is_off_when_CatchUpOnRegistration_is_false()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.PushNotifications.CatchUpOnRegistration = false);
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync("task")).RequireTask();
        await host.WaitIdleAsync();

        await client.CreateTaskPushNotificationConfigAsync(Hook(task.Id));
        await host.WaitIdleAsync();
        await Task.Delay(100); // negative check: no catch-up delivery appears in this time

        Assert.Empty(host.Webhooks.Calls);
    }

    [Fact]
    public async Task Catch_up_and_a_second_queuing_of_the_same_config_leave_one_delivery()
    {
        await using var host = await TestAgentHost.StartAsync();
        await StopDeliveryAsync(host); // nothing leaves the outbox, so the count is what was queued
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync("task")).RequireTask();
        var created = await client.CreateTaskPushNotificationConfigAsync(Hook(task.Id));
        Assert.Equal(1, host.PushOutbox.Count);

        await host.Engine.CatchUpAsync(Key(task.Id), created.Id!, CancellationToken.None); // the other path, same config and sequence

        Assert.Equal(1, host.PushOutbox.Count);
    }

    [Fact]
    public async Task Config_saved_between_the_listing_and_the_commit_gets_the_final_event()
    {
        var (host, store) = await StartHookedAsync();
        await using var _ = host;
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync(Message.FromUser("gate late-relist"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();
        await WaitForStateAsync(client, task.Id, TaskState.Working);
        // The listing of the completing event finds no config; one is saved right after it, before the commit.
        store.Arm(async n => { if (n == 1) await SaveLateAsync(store, task.Id, "late"); });

        ScenarioAgent.Gate("gate late-relist").SetResult();
        await WaitUntilAsync(() => CallsTo(host, "late").Count > 0);
        await host.WaitIdleAsync();
        await Task.Delay(100); // negative check: no second delivery of "late" appears in this time

        var call = Assert.Single(CallsTo(host, "late"));
        var snapshot = Assert.IsType<TaskEvent>(A2AJson.Deserialize<StreamResponse>(call.Body)).Task;
        Assert.Equal(TaskState.Completed, snapshot.Status.State);
        var last = await host.App.Services.GetRequiredService<EventStore>().LastSequenceAsync(Key(task.Id));
        Assert.Equal(last.ToString(), call.Request.Headers.GetValues(A2AProtocol.Headers.NotificationSequence).Single());
    }

    [Fact]
    public async Task Relisting_is_off_when_CatchUpOnRegistration_is_false()
    {
        var (host, store) = await StartHookedAsync(o => o.PushNotifications.CatchUpOnRegistration = false);
        await using var _ = host;
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync(Message.FromUser("gate late-relist-off"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();
        await WaitForStateAsync(client, task.Id, TaskState.Working);
        store.Arm(async n => { if (n == 1) await SaveLateAsync(store, task.Id, "late"); });

        ScenarioAgent.Gate("gate late-relist-off").SetResult();
        await host.WaitIdleAsync();
        await Task.Delay(100); // negative check: the relisting delivers nothing in this time

        Assert.Empty(host.Webhooks.Calls);
    }

    [Fact]
    public async Task Registration_and_the_relisting_that_both_catch_up_queue_one_delivery()
    {
        var (host, store) = await StartHookedAsync();
        await using var _ = host;
        await StopDeliveryAsync(host);
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync(Message.FromUser("gate late-both"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();
        await WaitForStateAsync(client, task.Id, TaskState.Working);
        // The config is saved after the first listing; its registration reads the finished task after the commit, but before the
        // second listing: both paths queue the catch-up of "late".
        store.Arm(async n =>
        {
            if (n == 1) await SaveLateAsync(store, task.Id, "late");
            if (n == 2) await host.Engine.CatchUpAsync(Key(task.Id), "late", CancellationToken.None);
        });

        ScenarioAgent.Gate("gate late-both").SetResult();
        await host.Handler.WhenIdleAsync();

        Assert.Equal(1, host.PushOutbox.Count);
    }
}

public class PushDeliveryLimitsTests
{
    private static TaskPushNotificationConfig Hook() => new() { Url = "https://hooks.example/cb" };

    [Fact]
    public async Task A_delivery_older_than_MaxDeliveryAge_is_dead_lettered_without_another_attempt()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.ServiceUnavailable);
        var rig = new PushRig(hooks);
        rig.Options.PushDelivery.MaxDeliveryAge = TimeSpan.FromHours(1);
        await rig.SaveConfigAsync(Hook());
        var d = (await rig.CommitAsync("c1")).Single();
        using var service = rig.NewService();

        await service.RunOnceAsync(CancellationToken.None); // 503: retried later
        Assert.Single(hooks.Requests);
        Assert.Empty(await rig.Outbox.ListDeadLettersAsync("", 10));
        rig.Time.Advance(TimeSpan.FromHours(2));
        await service.RunOnceAsync(CancellationToken.None);

        Assert.Single(hooks.Requests); // no second attempt
        var dead = Assert.Single(await rig.Outbox.ListDeadLettersAsync("", 10));
        Assert.Equal(d, dead.Delivery);
        Assert.Contains("Expired", dead.LastError);
        Assert.Contains("503", dead.LastError); // the last real error is kept
        Assert.Equal(0, rig.Outbox.Count);
    }

    [Fact]
    public async Task A_young_delivery_is_not_expired_and_a_null_age_never_expires()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var rig = new PushRig(hooks);
        rig.Options.PushDelivery.MaxDeliveryAge = null;
        await rig.SaveConfigAsync(Hook());
        await rig.CommitAsync("c1");
        using var service = rig.NewService();

        await service.RunOnceAsync(CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromDays(30));
        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, hooks.Requests.Count);
        Assert.Empty(await rig.Outbox.ListDeadLettersAsync("", 10));
        Assert.Equal(0, rig.Outbox.Count);
    }

    [Fact]
    public async Task Without_MaxAttempts_a_failing_delivery_keeps_retrying_until_it_succeeds()
    {
        var codes = Enumerable.Repeat(HttpStatusCode.ServiceUnavailable, 15).Append(HttpStatusCode.OK).ToArray();
        var hooks = new ScriptedHooks(codes);
        var rig = new PushRig(hooks);
        Assert.Null(rig.Options.PushDelivery.MaxAttempts);
        await rig.SaveConfigAsync(Hook());
        await rig.CommitAsync("c1");
        using var service = rig.NewService();

        await rig.DrainAsync(service);

        Assert.Equal(16, hooks.Requests.Count);
        Assert.Empty(await rig.Outbox.ListDeadLettersAsync("", 10));
    }

    [Fact]
    public async Task MaxAttempts_when_set_caps_the_attempts()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.ServiceUnavailable);
        var rig = new PushRig(hooks);
        rig.Options.PushDelivery.MaxAttempts = 4;
        await rig.SaveConfigAsync(Hook());
        await rig.CommitAsync("c1");
        using var service = rig.NewService();

        await rig.DrainAsync(service);

        Assert.Equal(4, hooks.Requests.Count);
        Assert.Equal(4, Assert.Single(await rig.Outbox.ListDeadLettersAsync("", 10)).Attempt);
    }

    [Fact]
    public async Task OnDeadLetter_gets_the_task_config_sequence_and_error_without_secrets()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.BadRequest);
        var rig = new PushRig(hooks);
        var seen = new List<PushDeadLetterInfo>();
        rig.Options.PushDelivery.OnDeadLetter = info =>
        {
            seen.Add(info);
            return ValueTask.CompletedTask;
        };
        await rig.SaveConfigAsync(new TaskPushNotificationConfig { Url = "https://hooks.example/cb", Token = "s3cret-token" });
        var d = (await rig.CommitAsync("c1")).Single();
        using var service = rig.NewService();

        await rig.DrainAsync(service);

        var info = Assert.Single(seen);
        Assert.Equal(PushRig.Key, info.Task);
        Assert.Equal("c1", info.ConfigId);
        Assert.Equal(d.Sequence, info.Sequence);
        Assert.Contains("400", info.LastError);
        Assert.DoesNotContain("s3cret-token", info.LastError);
    }

    [Fact]
    public async Task OnDeadLetter_also_fires_for_an_expired_delivery()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.ServiceUnavailable);
        var rig = new PushRig(hooks);
        rig.Options.PushDelivery.MaxDeliveryAge = TimeSpan.FromMinutes(5);
        var errors = new List<string?>();
        rig.Options.PushDelivery.OnDeadLetter = info =>
        {
            errors.Add(info.LastError);
            return ValueTask.CompletedTask;
        };
        await rig.SaveConfigAsync(Hook());
        await rig.CommitAsync("c1");
        using var service = rig.NewService();
        await service.RunOnceAsync(CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromHours(1));

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Contains("Expired", Assert.Single(errors));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_throwing_OnDeadLetter_does_not_affect_delivery(bool throwsAsync)
    {
        var hooks = new ScriptedHooks(HttpStatusCode.BadRequest);
        var rig = new PushRig(hooks);
        var calls = 0;
        rig.Options.PushDelivery.OnDeadLetter = async _ =>
        {
            calls++;
            if (throwsAsync) await Task.Yield();
            throw new InvalidOperationException("hook failed");
        };
        await rig.SaveConfigAsync(Hook());
        await rig.SaveConfigAsync(Hook(), "c1x");
        var d = (await rig.CommitAsync("c1", "c1x")).First();
        using var service = rig.NewService();

        await rig.DrainAsync(service); // does not throw

        Assert.Equal(2, calls); // both deliveries were dead-lettered and each called the hook
        Assert.Equal(2, (await rig.Outbox.ListDeadLettersAsync("", 10)).Count);
        Assert.Equal(d.Sequence, (await rig.Outbox.ListDeadLettersAsync("", 10))[0].Delivery.Sequence);
        Assert.Equal(0, rig.Outbox.Count);
    }

    [Fact]
    public async Task Invalid_age_or_attempt_options_are_refused_when_the_service_starts()
    {
        var rig = new PushRig(new ScriptedHooks());
        rig.Options.PushDelivery.MaxDeliveryAge = TimeSpan.Zero;
        using var zeroAge = rig.NewService();
        Assert.Contains("MaxDeliveryAge", (await Assert.ThrowsAsync<InvalidOperationException>(() => zeroAge.StartAsync(CancellationToken.None))).Message);

        rig.Options.PushDelivery.MaxDeliveryAge = null;
        rig.Options.PushDelivery.MaxAttempts = 0;
        using var zeroAttempts = rig.NewService();
        Assert.Contains("MaxAttempts", (await Assert.ThrowsAsync<InvalidOperationException>(() => zeroAttempts.StartAsync(CancellationToken.None))).Message);
    }
}
