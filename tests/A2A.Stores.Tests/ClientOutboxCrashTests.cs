using System.Collections.Concurrent;
using A2A.Client;
using A2A.Client.Outbox;
using A2A.Client.Postgres;
using A2A.Tests;

namespace A2A.Stores.Tests;

/// <summary>
/// What survives a crash. "Restart" is a new dispatcher and a new store object over the same storage. A crash in the middle of a send is
/// simulated by a client that does the send and then cancels the dispatcher's token: the dispatcher writes nothing after cancellation,
/// like a process that died. The clock is fake and every sweep is driven by <c>RunOnceAsync</c>.
/// </summary>
public abstract class ClientOutboxCrashContract
{
    protected abstract Task<ClientOutboxStore> CreateAsync(TimeProvider time);

    /// <summary>A new store object over the same storage (the process restarted). In-memory: the same object, which is all the memory there is.</summary>
    protected abstract ClientOutboxStore Restart(ClientOutboxStore store, TimeProvider time);

    protected readonly FakeTimeProvider Time = new();
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private static ClientOutboxOptions Options(Action<ClientOutboxOptions>? change = null)
    {
        var o = new ClientOutboxOptions { SweepInterval = Timeout.InfiniteTimeSpan, Lease = Lease };
        change?.Invoke(o);
        return o;
    }

    private ClientOutboxDispatcher NewDispatcher(ClientOutboxStore store, IA2AClient client, Action<ClientOutboxOptions>? change = null) =>
        new(store, (_, _) => new(client), Options(change), Time);

    protected static ClientOutboxMessage Msg(string id, string peer = "peer", string? correlation = null) =>
        ClientOutboxMessage.Create(peer, new SendMessageRequest { Message = Message.FromUser("hello " + id) }, correlation, id);

    private static Task<SendMessageResponse> Working(string taskId) =>
        System.Threading.Tasks.Task.FromResult<SendMessageResponse>(new TaskResponse(ScriptedA2AClient.Make(taskId, TaskState.Working)));

    private static Task<SendMessageResponse> Completed(string taskId) =>
        System.Threading.Tasks.Task.FromResult<SendMessageResponse>(new TaskResponse(ScriptedA2AClient.Make(taskId, TaskState.Completed)));

    /// <summary>A client whose send does the send, then "kills the process" by cancelling <paramref name="process"/>.</summary>
    private static ScriptedA2AClient CrashesAfterSend(CancellationTokenSource process, Func<string, Task<SendMessageResponse>>? answer = null)
    {
        var client = new ScriptedA2AClient();
        client.OnSend = (_, options, _) =>
        {
            var response = answer?.Invoke(options!.IdempotencyKey!) ?? Working("task-" + options!.IdempotencyKey);
            process.Cancel();
            return response;
        };
        return client;
    }

    // 1. Enqueue committed, crash before send: a new dispatcher sends.
    [SkippableFact]
    public async Task Crash_point_1_committed_entry_is_sent_by_a_new_dispatcher()
    {
        var store = await CreateAsync(Time);
        Assert.True(await store.EnqueueAsync(Msg("a")));
        // The first process died here, before any dispatcher swept.

        var client = new ScriptedA2AClient();
        await using var dispatcher = NewDispatcher(Restart(store, Time), client);
        await dispatcher.RunOnceAsync();

        Assert.Equal(["a"], client.SendKeys);
        Assert.Equal(ClientOutboxStatus.Tracking, (await store.GetAsync("a"))!.Status);
    }

    // 3 (unit level). Crash after the peer took it, before the store write: the lease runs out and the resend has the same key.
    [SkippableFact]
    public async Task Crash_point_3_resend_after_the_lease_has_the_same_key_and_is_in_doubt()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        using var process = new CancellationTokenSource();
        var first = CrashesAfterSend(process);
        await using (var a = NewDispatcher(store, first)) await a.RunOnceAsync(process.Token);
        var afterCrash = (await store.GetAsync("a"))!;
        Assert.Equal(ClientOutboxStatus.Queued, afterCrash.Status); // nothing was written after the send
        Assert.True(afterCrash.InDoubt); // and nothing had to be: the doubt was set when the entry was leased

        Time.Advance(Lease);
        var second = new ScriptedA2AClient();
        await using var b = NewDispatcher(Restart(store, Time), second);
        await b.RunOnceAsync();

        Assert.Equal(["a"], second.SendKeys); // same key as the first send
        Assert.Equal(first.SendKeys, second.SendKeys);
        Assert.Equal(ClientOutboxStatus.Tracking, (await store.GetAsync("a"))!.Status);
    }

    // 3 (store write fails after the send): the same, with a store that throws at the write.
    [SkippableFact]
    public async Task Crash_point_3_store_write_fails_after_the_send_and_the_entry_is_sent_again_with_the_same_key()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        var crashing = new CrashingClientOutboxStore(store);
        crashing.CrashOn.Add(nameof(ClientOutboxStore.MarkTrackingAsync));
        var client = new ScriptedA2AClient();
        await using var a = NewDispatcher(crashing, client);
        await a.RunOnceAsync();
        Assert.Equal(ClientOutboxStatus.Queued, (await store.GetAsync("a"))!.Status);

        Time.Advance(Lease);
        crashing.CrashOn.Clear();
        await a.RunOnceAsync();

        Assert.Equal(["a", "a"], client.SendKeys);
        Assert.Equal(ClientOutboxStatus.Tracking, (await store.GetAsync("a"))!.Status);
    }

    // 3. End to end: the peer is a real in-process A2A server; one task even though the message was sent twice.
    [SkippableFact]
    public async Task Crash_point_3_end_to_end_the_peer_has_one_task()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        await using var peer = await ClientOutboxPeer.StartAsync();
        var real = peer.Client();

        // The first process: the send reaches the peer, then the process dies before it writes anything.
        using var process = new CancellationTokenSource();
        var sends = 0;
        var dying = new ScriptedA2AClient
        {
            OnSend = async (request, options, ct) =>
            {
                Interlocked.Increment(ref sends);
                var response = await real.SendMessageAsync(request, options, ct);
                process.Cancel();
                return response;
            },
        };
        await using (var a = NewDispatcher(store, dying)) await a.RunOnceAsync(process.Token);
        await peer.State.Started.Task; // the peer took the message and started work
        Assert.Equal(ClientOutboxStatus.Queued, (await store.GetAsync("a"))!.Status);

        // The second process, after the lease: same key, so the peer answers with the task it already has.
        Time.Advance(Lease);
        var resending = new ScriptedA2AClient { OnSend = (request, options, ct) => { Interlocked.Increment(ref sends); return real.SendMessageAsync(request, options, ct); } };
        await using var b = NewDispatcher(Restart(store, Time), resending);
        await b.RunOnceAsync();

        Assert.Equal(2, sends);
        Assert.Equal(1, peer.State.Runs);
        var tasks = await real.ListTasksAsync(new ListTasksRequest());
        var only = Assert.Single(tasks.Tasks);
        var e = (await store.GetAsync("a"))!;
        Assert.Equal(only.Id, e.RemoteTaskId);
        Assert.Equal(ClientOutboxStatus.Tracking, e.Status);

        // The task finishes; the ledger collects the answer by polling.
        peer.State.Gate.SetResult();
        await peer.State.Completed.Task;
        Time.Advance(TimeSpan.FromMinutes(1));
        await b.RunOnceAsync();
        var result = Assert.Single(await store.LeaseResultsAsync(null, 10, Lease));
        Assert.Equal(TaskState.Completed, Assert.IsType<TaskResponse>(result.GetResult()).Task.Status.State);
        Assert.Equal(1, peer.State.Runs);
    }

    // 4. Crash after MarkTracking: a new dispatcher polls by the stored task id and does not send again.
    [SkippableFact]
    public async Task Crash_point_4_after_tracking_a_new_dispatcher_polls_and_does_not_send_again()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        var first = new ScriptedA2AClient { OnSend = (_, _, _) => Working("remote-1") };
        await using (var a = NewDispatcher(store, first)) await a.RunOnceAsync();
        Assert.Equal(ClientOutboxStatus.Tracking, (await store.GetAsync("a"))!.Status);
        // The process died here.

        Time.Advance(TimeSpan.FromSeconds(5));
        var second = new ScriptedA2AClient();
        await using var b = NewDispatcher(Restart(store, Time), second);
        await b.RunOnceAsync();

        Assert.Empty(second.SendKeys);
        Assert.Equal(["remote-1"], second.Gets);
        Assert.Equal(ClientOutboxStatus.Finished, (await store.GetAsync("a"))!.Status);
    }

    // 5. Result stored, not collected, restart: harvest returns it. Crash before ack: returned again after the lease.
    [SkippableFact]
    public async Task Crash_point_5_a_stored_result_is_harvested_after_restart_and_again_after_a_crash_before_ack()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a", correlation: "job"));
        var client = new ScriptedA2AClient { OnSend = (_, _, _) => Completed("done-1") };
        await using (var a = NewDispatcher(store, client)) await a.RunOnceAsync();

        var restarted = Restart(store, Time);
        var first = Assert.Single(await restarted.LeaseResultsAsync("job", 10, Lease));
        Assert.Equal("done-1", Assert.IsType<TaskResponse>(first.GetResult()).Task.Id);
        // The app crashed before it acknowledged.

        Assert.Empty(await Restart(store, Time).LeaseResultsAsync("job", 10, Lease)); // still under the lease
        Time.Advance(Lease);
        var again = Assert.Single(await Restart(store, Time).LeaseResultsAsync("job", 10, Lease));
        Assert.True(await store.AcknowledgeAsync(again));
        Assert.Empty(await Restart(store, Time).LeaseResultsAsync("job", 10, Lease));
    }

    // 6. A stale lease holder writes nothing.
    [SkippableFact]
    public async Task Crash_point_6_a_dispatcher_whose_lease_was_taken_writes_nothing()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        var inSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new ScriptedA2AClient
        {
            OnSend = async (_, _, _) =>
            {
                inSend.TrySetResult();
                await release.Task; // the answer arrives late and ignores every timeout
                return new TaskResponse(ScriptedA2AClient.Make("from-slow", TaskState.Completed));
            },
        };
        await using var a = NewDispatcher(store, slow);
        var running = a.RunOnceAsync();
        await inSend.Task;

        Time.Advance(Lease); // the lease ran out while the first dispatcher waits
        var quick = new ScriptedA2AClient { OnSend = (_, _, _) => Completed("from-quick") };
        await using var b = NewDispatcher(Restart(store, Time), quick);
        await b.RunOnceAsync();
        release.SetResult();
        await running;

        var e = (await store.GetAsync("a"))!;
        Assert.Equal(ClientOutboxStatus.Finished, e.Status);
        Assert.Equal("from-quick", e.RemoteTaskId);
        Assert.Equal("from-quick", Assert.IsType<TaskResponse>(e.GetResult()).Task.Id);
    }

    // 7. Two dispatchers on one table: each entry is sent once.
    [SkippableFact]
    public async Task Crash_point_7_two_dispatchers_on_one_table_send_each_entry_once()
    {
        var store = await CreateAsync(Time);
        const int count = 40;
        for (var i = 0; i < count; i++) await store.EnqueueAsync(Msg("m" + i));
        var sent = new ConcurrentDictionary<string, int>();
        var client = new ScriptedA2AClient { OnSend = (_, options, _) => { sent.AddOrUpdate(options!.IdempotencyKey!, 1, (_, n) => n + 1); return Completed("t-" + options.IdempotencyKey); } };
        await using var a = NewDispatcher(store, client, o => o.BatchSize = 6);
        await using var b = NewDispatcher(Restart(store, Time), client, o => o.BatchSize = 6);

        int leased;
        do leased = (await Task.WhenAll(a.RunOnceAsync(), b.RunOnceAsync())).Sum();
        while (leased > 0);

        Assert.Equal(count, sent.Count);
        Assert.All(sent, kv => Assert.Equal(1, kv.Value));
        Assert.Equal(count, (await store.LeaseResultsAsync(null, 100, Lease)).Count);
    }

    // 8. RetryInDoubt = false: a crash in the middle of a send gives Failed, and no second call.
    [SkippableFact]
    public async Task Crash_point_8_with_RetryInDoubt_off_a_crash_mid_send_fails_the_entry_and_does_not_call_again()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        using var process = new CancellationTokenSource();
        var first = CrashesAfterSend(process);
        await using (var a = NewDispatcher(store, first, o => o.RetryInDoubt = false)) await a.RunOnceAsync(process.Token);

        Time.Advance(Lease);
        var second = new ScriptedA2AClient();
        await using var b = NewDispatcher(Restart(store, Time), second, o => o.RetryInDoubt = false);
        await b.RunOnceAsync();

        Assert.Empty(second.SendKeys);
        var result = Assert.Single(await store.LeaseResultsAsync(null, 10, Lease));
        Assert.Equal(ClientOutboxStatus.Failed, result.Status);
        Assert.Contains("RetryInDoubt", result.LastError);
    }
}

public sealed class InMemoryClientOutboxCrashTests : ClientOutboxCrashContract
{
    protected override Task<ClientOutboxStore> CreateAsync(TimeProvider time) => Task.FromResult<ClientOutboxStore>(new InMemoryClientOutboxStore(time));

    protected override ClientOutboxStore Restart(ClientOutboxStore store, TimeProvider time) => store;
}

[Collection(ContainersCollection.Name)]
public sealed class PostgresClientOutboxCrashTests(PostgresFixture db) : ClientOutboxCrashContract
{
    private PostgresClientOutboxOptions? _options;

    protected override async Task<ClientOutboxStore> CreateAsync(TimeProvider time)
    {
        _options = await db.NewClientOutboxSchemaAsync(time);
        return new PostgresClientOutboxStore(db.DataSource, _options, time);
    }

    protected override ClientOutboxStore Restart(ClientOutboxStore store, TimeProvider time) => new PostgresClientOutboxStore(db.DataSource, _options, time);

    // 2. App transaction rolled back: nothing is sent.
    [SkippableFact]
    public async Task Crash_point_2_a_rolled_back_transaction_sends_nothing_and_a_committed_one_does()
    {
        var store = (PostgresClientOutboxStore)await CreateAsync(Time);
        var client = new ScriptedA2AClient();
        await using var dispatcher = new ClientOutboxDispatcher(Restart(store, Time), (_, _) => new(client),
            new ClientOutboxOptions { SweepInterval = Timeout.InfiniteTimeSpan }, Time);

        await using (var conn = await db.DataSource.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            Assert.True(await store.EnqueueAsync(Msg("rolled-back"), conn, tx));
            await tx.RollbackAsync(); // the app's own work failed
        }
        Assert.Equal(0, await dispatcher.RunOnceAsync());
        Assert.Empty(client.SendKeys);
        Assert.Null(await store.GetAsync("rolled-back"));

        await using (var conn = await db.DataSource.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            Assert.True(await store.EnqueueAsync(Msg("committed"), conn, tx));
            await tx.CommitAsync();
        }
        Assert.Equal(1, await dispatcher.RunOnceAsync());
        Assert.Equal(["committed"], client.SendKeys);
    }
}
