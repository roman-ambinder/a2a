using System.Net.Http;
using A2A.Client;
using A2A.Client.Outbox;
using A2A.Client.Push;

namespace A2A.Tests;

/// <summary>The dispatcher over the in-memory store and a scripted client. The timer is off and a fake clock moves time; sweeps are driven with RunOnceAsync.</summary>
public sealed class ClientOutboxDispatcherTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly ScriptedA2AClient _client = new();
    private readonly InMemoryClientOutboxStore _store;
    private IA2AClient _resolved;

    public ClientOutboxDispatcherTests()
    {
        _store = new InMemoryClientOutboxStore(_time);
        _resolved = _client;
    }

    private static ClientOutboxOptions Options(Action<ClientOutboxOptions>? change = null)
    {
        var o = new ClientOutboxOptions { SweepInterval = Timeout.InfiniteTimeSpan };
        change?.Invoke(o);
        return o;
    }

    private ClientOutboxDispatcher Dispatcher(Action<ClientOutboxOptions>? change = null) =>
        new(_store, (_, _) => new(_resolved), Options(change), _time);

    private async Task<string> EnqueueAsync(string id = "m1", string peer = "peer-a", string? correlation = null)
    {
        var message = ClientOutboxMessage.Create(peer, new SendMessageRequest { Message = Message.FromUser("hello " + id) }, correlation, id);
        Assert.True(await _store.EnqueueAsync(message));
        return id;
    }

    private async Task<ClientOutboxEntry> GetAsync(string id = "m1") => (await _store.GetAsync(id))!;

    private static Task<SendMessageResponse> Fail(Exception error) => System.Threading.Tasks.Task.FromException<SendMessageResponse>(error);

    private static Task<SendMessageResponse> Answer(AgentTask task) => System.Threading.Tasks.Task.FromResult<SendMessageResponse>(new TaskResponse(task));

    // ---- sending ----

    [Fact]
    public async Task A_queued_message_is_sent_with_the_entry_id_as_idempotency_key_and_becomes_tracking()
    {
        await using var d = Dispatcher();
        var id = await EnqueueAsync();
        string? key = null;
        bool? returnImmediately = null;
        _client.OnSend = (request, options, _) =>
        {
            key = options?.IdempotencyKey;
            returnImmediately = request.Configuration?.ReturnImmediately;
            return Answer(ScriptedA2AClient.Make("task-1", TaskState.Working));
        };

        Assert.Equal(1, await d.RunOnceAsync());

        Assert.Equal(id, key);
        Assert.True(returnImmediately);
        var e = await GetAsync();
        Assert.Equal(ClientOutboxStatus.Tracking, e.Status);
        Assert.Equal("task-1", e.RemoteTaskId);
        Assert.Equal("ctx-task-1", e.RemoteContextId);
        Assert.Equal(TaskState.Working, e.RemoteState);
        Assert.Equal(0, e.Attempts);
        Assert.False(e.InDoubt);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromSeconds(5), e.DueAt);
    }

    [Fact]
    public async Task A_final_task_in_the_send_response_finishes_the_entry()
    {
        await using var d = Dispatcher();
        await EnqueueAsync();
        _client.OnSend = (_, _, _) => Answer(ScriptedA2AClient.Make("t", TaskState.Completed));

        await d.RunOnceAsync();

        var e = await GetAsync();
        Assert.Equal(ClientOutboxStatus.Finished, e.Status);
        Assert.Equal(TaskState.Completed, Assert.IsType<TaskResponse>(e.GetResult()).Task.Status.State);
        Assert.Equal("t", e.RemoteTaskId);
    }

    [Fact]
    public async Task An_interrupted_task_and_a_message_answer_are_final()
    {
        await using var d = Dispatcher();
        await EnqueueAsync("input");
        await EnqueueAsync("reply");
        _client.OnSend = (_, options, _) => options!.IdempotencyKey == "input"
            ? Answer(ScriptedA2AClient.Make("t-in", TaskState.InputRequired))
            : System.Threading.Tasks.Task.FromResult<SendMessageResponse>(new MessageResponse(Message.FromAgent("done", taskId: "t-reply")));

        await d.RunOnceAsync();

        Assert.Equal(ClientOutboxStatus.Finished, (await GetAsync("input")).Status);
        Assert.Equal(TaskState.InputRequired, (await GetAsync("input")).RemoteState);
        var reply = await GetAsync("reply");
        Assert.Equal(ClientOutboxStatus.Finished, reply.Status);
        Assert.IsType<MessageResponse>(reply.GetResult());
        Assert.Equal("t-reply", reply.RemoteTaskId);
    }

    [Fact]
    public async Task An_entry_that_is_not_due_is_not_sent()
    {
        await using var d = Dispatcher();
        await EnqueueAsync();
        _client.OnSend = (_, _, _) => Fail(new A2AException(A2AErrorCode.InvalidRequest) { HttpStatus = 503 });
        await d.RunOnceAsync();
        Assert.Single(_client.SendKeys);

        Assert.Equal(0, await d.RunOnceAsync()); // the retry is not due yet

        Assert.Single(_client.SendKeys);
    }

    public static TheoryData<string, Exception, ClientOutboxStatus, bool> Sorted => new()
    {
        { "503 retry", new A2AException(A2AErrorCode.InvalidRequest) { HttpStatus = 503 }, ClientOutboxStatus.Queued, false },
        { "500 in doubt", new A2AException(A2AErrorCode.InvalidRequest) { HttpStatus = 500 }, ClientOutboxStatus.Queued, true },
        { "400 failed", new A2AException(A2AErrorCode.InvalidRequest) { HttpStatus = 400 }, ClientOutboxStatus.Failed, false },
        { "unsent connection", new HttpRequestException(HttpRequestError.ConnectionError, "refused"), ClientOutboxStatus.Queued, false },
        { "other exception", new InvalidOperationException("boom"), ClientOutboxStatus.Queued, true },
        { "dropped after sending", new HttpRequestException(HttpRequestError.ResponseEnded, "reset"), ClientOutboxStatus.Queued, true },
    };

    [Theory]
    [MemberData(nameof(Sorted))]
    public async Task A_failed_send_is_sorted_by_the_table(string name, Exception error, ClientOutboxStatus status, bool inDoubt)
    {
        await using var d = Dispatcher();
        await EnqueueAsync();
        _client.OnSend = (_, _, _) => Fail(error);

        await d.RunOnceAsync();

        var e = await GetAsync();
        Assert.Equal(status, e.Status);
        Assert.Equal(inDoubt, e.InDoubt);
        Assert.False(string.IsNullOrEmpty(e.LastError), name);
        if (status == ClientOutboxStatus.Queued) Assert.True(e.DueAt > _time.GetUtcNow());
    }

    [Fact]
    public async Task The_resolver_failing_is_a_retry_that_is_not_in_doubt_and_the_peer_is_not_called()
    {
        await EnqueueAsync();
        await using var d = new ClientOutboxDispatcher(_store, (_, _) => throw new InvalidOperationException("no credentials"), Options(), _time);

        await d.RunOnceAsync();

        var e = await GetAsync();
        Assert.Equal(ClientOutboxStatus.Queued, e.Status);
        Assert.False(e.InDoubt);
        Assert.Contains("no credentials", e.LastError);
        Assert.Empty(_client.SendKeys);
    }

    [Fact]
    public async Task The_resolver_gets_the_peer_name()
    {
        string? asked = null;
        await EnqueueAsync(peer: "billing");
        await using var d = new ClientOutboxDispatcher(_store, (peer, _) => { asked = peer; return new(_client); }, Options(), _time);
        await d.RunOnceAsync();
        Assert.Equal("billing", asked);
    }

    [Fact]
    public async Task A_clean_failure_does_not_clear_an_earlier_doubt()
    {
        await using var d = Dispatcher();
        await EnqueueAsync();
        _client.OnSend = (_, _, _) => Fail(new InvalidOperationException("dropped"));
        await d.RunOnceAsync();
        Assert.True((await GetAsync()).InDoubt);

        _time.Advance(TimeSpan.FromMinutes(10));
        _client.OnSend = (_, _, _) => Fail(new A2AException(A2AErrorCode.InvalidRequest) { HttpStatus = 503 });
        await d.RunOnceAsync();

        Assert.True((await GetAsync()).InDoubt);
    }

    [Fact]
    public async Task Retry_after_wins_over_backoff()
    {
        await using var d = Dispatcher();
        await EnqueueAsync();
        _client.OnSend = (_, _, _) => Fail(A2AException.RateLimited(TimeSpan.FromSeconds(42)));

        await d.RunOnceAsync();

        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromSeconds(42), (await GetAsync()).DueAt);
    }

    [Fact]
    public async Task Backoff_doubles_with_jitter_up_to_the_maximum()
    {
        await using var d = Dispatcher(o => { o.RetryBaseDelay = TimeSpan.FromSeconds(10); o.RetryMaxDelay = TimeSpan.FromSeconds(50); });
        await EnqueueAsync();
        _client.OnSend = (_, _, _) => Fail(new A2AException(A2AErrorCode.InvalidRequest) { HttpStatus = 503 });

        // attempt n waits 10 s * 2^(n-1), at most 50 s, with +-20 % jitter
        foreach (var expected in new[] { 10.0, 20.0, 40.0, 50.0, 50.0 })
        {
            var before = _time.GetUtcNow();
            await d.RunOnceAsync();
            var wait = (await GetAsync()).DueAt - before;
            Assert.InRange(wait.TotalSeconds, expected * 0.8 - 0.001, Math.Min(expected * 1.2, 50.0) + 0.001);
            _time.Advance(wait);
        }
    }

    [Fact]
    public async Task A_send_that_times_out_is_in_doubt()
    {
        await using var d = Dispatcher(o => o.CallTimeout = TimeSpan.FromSeconds(10));
        await EnqueueAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.OnSend = async (_, _, ct) =>
        {
            started.TrySetResult();
            await new TaskCompletionSource<bool>().Task.WaitAsync(ct); // never answers; ends when the call timeout cancels it
            throw new InvalidOperationException("unreachable");
        };

        var run = d.RunOnceAsync();
        await started.Task;
        _time.Advance(TimeSpan.FromSeconds(10));
        await run;

        var e = await GetAsync();
        Assert.Equal(ClientOutboxStatus.Queued, e.Status);
        Assert.True(e.InDoubt);
        Assert.False(string.IsNullOrEmpty(e.LastError));
    }

    // ---- limits ----

    [Fact]
    public async Task A_message_older_than_MaxQueueAge_fails_without_calling_the_peer()
    {
        await using var d = Dispatcher(o => o.MaxQueueAge = TimeSpan.FromHours(1));
        await EnqueueAsync();
        _time.Advance(TimeSpan.FromHours(2));

        await d.RunOnceAsync();

        var e = await GetAsync();
        Assert.Equal(ClientOutboxStatus.Failed, e.Status);
        Assert.Contains("MaxQueueAge", e.LastError);
        Assert.Empty(_client.SendKeys);
    }

    [Fact]
    public async Task A_failure_that_pushes_the_message_over_MaxQueueAge_fails_it_instead_of_retrying()
    {
        await using var d = Dispatcher(o => o.MaxQueueAge = TimeSpan.FromMinutes(1));
        await EnqueueAsync();
        _time.Advance(TimeSpan.FromSeconds(59));
        _client.OnSend = (_, _, _) =>
        {
            _time.Advance(TimeSpan.FromSeconds(5)); // the call took long enough to cross the limit
            return Fail(new A2AException(A2AErrorCode.InvalidRequest) { HttpStatus = 503 });
        };

        await d.RunOnceAsync();

        Assert.Equal(ClientOutboxStatus.Failed, (await GetAsync()).Status);
        Assert.Single(_client.SendKeys);
    }

    [Fact]
    public async Task With_RetryInDoubt_off_an_entry_in_doubt_fails_without_a_second_call()
    {
        await using var d = Dispatcher(o => o.RetryInDoubt = false);
        await EnqueueAsync();
        _client.OnSend = (_, _, _) => Fail(new InvalidOperationException("dropped"));
        await d.RunOnceAsync();
        Assert.True((await GetAsync()).InDoubt);

        _time.Advance(TimeSpan.FromMinutes(10));
        await d.RunOnceAsync();

        var e = await GetAsync();
        Assert.Equal(ClientOutboxStatus.Failed, e.Status);
        Assert.Contains("RetryInDoubt", e.LastError);
        Assert.Single(_client.SendKeys);
    }

    [Fact]
    public async Task With_RetryInDoubt_off_a_clean_failure_is_still_retried()
    {
        await using var d = Dispatcher(o => o.RetryInDoubt = false);
        await EnqueueAsync();
        _client.OnSend = (_, _, _) => Fail(new A2AException(A2AErrorCode.InvalidRequest) { HttpStatus = 503 });
        await d.RunOnceAsync();
        _time.Advance(TimeSpan.FromMinutes(10));

        await d.RunOnceAsync();

        Assert.Equal(ClientOutboxStatus.Queued, (await GetAsync()).Status);
        Assert.Equal(2, _client.SendKeys.Count);
    }

    [Fact]
    public async Task An_unreadable_stored_request_fails_the_entry()
    {
        await _store.EnqueueAsync(new ClientOutboxMessage("bad", "peer", System.Text.Encoding.UTF8.GetBytes("not json")));
        await using var d = Dispatcher();

        await d.RunOnceAsync();

        Assert.Equal(ClientOutboxStatus.Failed, (await GetAsync("bad")).Status);
        Assert.Empty(_client.SendKeys);
    }

    // ---- tracking ----

    private async Task<ClientOutboxDispatcher> TrackedAsync(Action<ClientOutboxOptions>? change = null)
    {
        var d = Dispatcher(change);
        await EnqueueAsync();
        _client.OnSend = (_, _, _) => Answer(ScriptedA2AClient.Make("task-1", TaskState.Working));
        await d.RunOnceAsync();
        Assert.Equal(ClientOutboxStatus.Tracking, (await GetAsync()).Status);
        _time.Advance(TimeSpan.FromSeconds(5));
        return d;
    }

    [Fact]
    public async Task A_tracked_task_that_is_final_finishes_the_entry_with_the_task()
    {
        await using var d = await TrackedAsync();
        GetTaskRequest? seen = null;
        _client.OnGetTask = (request, _, _) =>
        {
            seen = request;
            var task = ScriptedA2AClient.Make("task-1", TaskState.Completed);
            task.History = [Message.FromAgent("answer")];
            return System.Threading.Tasks.Task.FromResult(task);
        };

        await d.RunOnceAsync();

        Assert.Equal("task-1", seen!.Id);
        Assert.Null(seen.HistoryLength); // the stored result may need the history
        var e = await GetAsync();
        Assert.Equal(ClientOutboxStatus.Finished, e.Status);
        Assert.Equal(TaskState.Completed, e.RemoteState);
        var task2 = Assert.IsType<TaskResponse>(e.GetResult()).Task;
        Assert.Equal("answer", task2.History![0].GetText());
        Assert.Single(_client.SendKeys); // not sent again
    }

    [Fact]
    public async Task A_tracked_task_that_is_not_final_is_polled_again_with_growing_waits()
    {
        await using var d = await TrackedAsync(o => { o.PollInterval = TimeSpan.FromSeconds(5); o.MaxPollInterval = TimeSpan.FromSeconds(30); });
        _client.OnGetTask = (request, _, _) => System.Threading.Tasks.Task.FromResult(ScriptedA2AClient.Make(request.Id, TaskState.Working));

        // The first wait after sending was the poll interval (5 s, already waited); the checks then wait 5, 10, 20, 30, 30 s.
        foreach (var seconds in new[] { 5, 10, 20, 30, 30 })
        {
            var before = _time.GetUtcNow();
            Assert.Equal(1, await d.RunOnceAsync());
            Assert.Equal(before + TimeSpan.FromSeconds(seconds), (await GetAsync()).DueAt);
            _time.Advance(TimeSpan.FromSeconds(seconds));
        }
        Assert.Equal(ClientOutboxStatus.Tracking, (await GetAsync()).Status);
        Assert.Equal(5, _client.Gets.Count);
    }

    [Fact]
    public async Task TaskNotFound_while_tracking_fails_the_entry_and_keeps_the_task_id()
    {
        await using var d = await TrackedAsync();
        _client.OnGetTask = (_, _, _) => System.Threading.Tasks.Task.FromException<AgentTask>(A2AException.TaskNotFound("task-1"));

        await d.RunOnceAsync();

        var e = await GetAsync();
        Assert.Equal(ClientOutboxStatus.Failed, e.Status);
        Assert.Equal("task-1", e.RemoteTaskId);
    }

    [Fact]
    public async Task A_failed_check_is_retried_and_honours_retry_after()
    {
        await using var d = await TrackedAsync();
        _client.OnGetTask = (_, _, _) => System.Threading.Tasks.Task.FromException<AgentTask>(A2AException.RateLimited(TimeSpan.FromSeconds(90)));

        await d.RunOnceAsync();

        var e = await GetAsync();
        Assert.Equal(ClientOutboxStatus.Tracking, e.Status);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromSeconds(90), e.DueAt);
        Assert.False(string.IsNullOrEmpty(e.LastError));
    }

    [Fact]
    public async Task A_task_tracked_longer_than_MaxTrackingAge_fails_and_keeps_the_task_id()
    {
        await using var d = await TrackedAsync(o => o.MaxTrackingAge = TimeSpan.FromHours(1));
        _time.Advance(TimeSpan.FromHours(2));

        await d.RunOnceAsync();

        var e = await GetAsync();
        Assert.Equal(ClientOutboxStatus.Failed, e.Status);
        Assert.Equal("task-1", e.RemoteTaskId);
        Assert.Contains("MaxTrackingAge", e.LastError);
        Assert.Empty(_client.Gets);
    }

    [Fact]
    public async Task Tracking_uses_the_tenant_of_the_stored_request()
    {
        var d = Dispatcher();
        await _store.EnqueueAsync(ClientOutboxMessage.Create("peer", new SendMessageRequest { Tenant = "acme", Message = Message.FromUser("x") }, id: "m1"));
        _client.OnSend = (_, _, _) => Answer(ScriptedA2AClient.Make("task-1", TaskState.Working));
        await using (d)
        {
            await d.RunOnceAsync();
            _time.Advance(TimeSpan.FromSeconds(5));
            string? tenant = null;
            _client.OnGetTask = (request, _, _) => { tenant = request.Tenant; return System.Threading.Tasks.Task.FromResult(ScriptedA2AClient.Make("task-1", TaskState.Completed)); };
            await d.RunOnceAsync();
            Assert.Equal("acme", tenant);
        }
    }

    // ---- webhook hint ----

    private static PushDelivery Delivery(string taskId, TaskState state) =>
        new(StreamResponse.Of(new TaskStatusUpdateEvent { TaskId = taskId, ContextId = "c", Status = new AgentTaskStatus { State = state } }), "d1", 1);

    [Fact]
    public async Task A_final_webhook_makes_the_tracked_entry_due_now()
    {
        var d = Dispatcher(o => o.PollInterval = TimeSpan.FromMinutes(1));
        await using var _ = d;
        await EnqueueAsync();
        _client.OnSend = (_, _, _) => Answer(ScriptedA2AClient.Make("task-1", TaskState.Working));
        await d.RunOnceAsync();
        Assert.Equal(0, await d.RunOnceAsync()); // not due for a minute

        Assert.True(await d.NotifyAsync("peer-a", Delivery("task-1", TaskState.Completed)));

        Assert.Equal(1, await d.RunOnceAsync());
        Assert.Equal(ClientOutboxStatus.Finished, (await GetAsync()).Status); // GetTask answered; the webhook itself is only a hint
    }

    [Fact]
    public async Task A_webhook_that_is_not_final_or_names_another_peer_does_nothing()
    {
        var d = Dispatcher(o => o.PollInterval = TimeSpan.FromMinutes(1));
        await using var _ = d;
        await EnqueueAsync();
        _client.OnSend = (_, _, _) => Answer(ScriptedA2AClient.Make("task-1", TaskState.Working));
        await d.RunOnceAsync();

        Assert.False(await d.NotifyAsync("peer-a", Delivery("task-1", TaskState.Working)));
        Assert.False(await d.NotifyAsync("peer-b", Delivery("task-1", TaskState.Completed))); // task ids are per peer
        Assert.False(await d.NotifyAsync("peer-a", Delivery("task-2", TaskState.Completed)));
        Assert.False(await d.NotifyAsync("peer-a", new PushDelivery(StreamResponse.Of(Message.FromAgent("hi")), null, null)));
        Assert.Equal(0, await d.RunOnceAsync());
    }

    // ---- cancellation, concurrency, lifetime ----

    [Fact]
    public async Task Cancelled_work_writes_nothing_and_the_lease_runs_out()
    {
        await using var d = Dispatcher();
        await EnqueueAsync();
        using var cts = new CancellationTokenSource();
        _client.OnSend = (_, _, _) =>
        {
            cts.Cancel(); // the process stops after the peer took the message
            return Answer(ScriptedA2AClient.Make("task-1", TaskState.Completed));
        };

        await d.RunOnceAsync(cts.Token);

        var e = await GetAsync();
        Assert.Equal(ClientOutboxStatus.Queued, e.Status); // nothing was written
        Assert.True(e.InDoubt);
        Assert.Equal(1, e.Attempts);
        Assert.Empty(await _store.LeaseDueAsync(10, TimeSpan.FromMinutes(1))); // still leased
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Single(await _store.LeaseDueAsync(10, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task A_store_failure_is_survived_and_the_entry_comes_back_after_its_lease()
    {
        var crashing = new CrashingClientOutboxStore(_store);
        crashing.CrashOn.Add(nameof(ClientOutboxStore.MarkTrackingAsync));
        await using var d = new ClientOutboxDispatcher(crashing, (_, _) => new(_client), Options(), _time);
        await EnqueueAsync();

        await d.RunOnceAsync();

        Assert.Single(_client.SendKeys);
        Assert.Equal(ClientOutboxStatus.Queued, (await GetAsync()).Status);
        crashing.CrashOn.Clear();
        _time.Advance(TimeSpan.FromMinutes(6));
        await d.RunOnceAsync();
        Assert.Equal(2, _client.SendKeys.Count);
        Assert.Equal(["m1", "m1"], _client.SendKeys); // the same key both times
        Assert.Equal(ClientOutboxStatus.Tracking, (await GetAsync()).Status);
    }

    [Fact]
    public async Task At_most_MaxConcurrency_sends_run_at_once()
    {
        await using var d = Dispatcher(o => o.MaxConcurrency = 2);
        for (var i = 0; i < 5; i++) await EnqueueAsync("m" + i);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var two = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int current = 0, max = 0;
        _client.OnSend = async (_, options, _) =>
        {
            var now = Interlocked.Increment(ref current);
            lock (gate) max = Math.Max(max, now);
            if (now == 2) two.TrySetResult();
            await gate.Task;
            Interlocked.Decrement(ref current);
            return new TaskResponse(ScriptedA2AClient.Make("t-" + options!.IdempotencyKey, TaskState.Completed));
        };

        var run = d.RunOnceAsync();
        await two.Task;
        gate.SetResult();
        Assert.Equal(5, await run);

        Assert.Equal(2, max);
        Assert.Equal(5, _client.SendKeys.Count);
    }

    [Fact]
    public async Task Dispose_cancels_work_in_flight_waits_for_it_and_writes_nothing()
    {
        var d = Dispatcher();
        await EnqueueAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.OnSend = async (_, _, ct) =>
        {
            started.TrySetResult();
            await new TaskCompletionSource<bool>().Task.WaitAsync(ct); // ends when the dispatcher is disposed
            throw new InvalidOperationException("unreachable");
        };
        var run = d.RunOnceAsync();
        await started.Task;

        await d.DisposeAsync();

        await run;
        var e = await GetAsync();
        Assert.Equal(ClientOutboxStatus.Queued, e.Status);
        Assert.True(e.InDoubt); // like a crash mid-send
        Assert.Null(e.LastError);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => d.RunOnceAsync());
        await d.DisposeAsync(); // twice is fine
    }

    [Fact]
    public async Task The_timer_sweeps_on_the_dispatchers_clock()
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.OnSend = (_, _, _) =>
        {
            sent.TrySetResult();
            return Answer(ScriptedA2AClient.Make("t", TaskState.Completed));
        };
        await using var d = new ClientOutboxDispatcher(_store, (_, _) => new(_client), new ClientOutboxOptions { SweepInterval = TimeSpan.FromSeconds(1) }, _time);
        await EnqueueAsync();
        Assert.Empty(_client.SendKeys);

        _time.Advance(TimeSpan.FromSeconds(1));
        await sent.Task;

        Assert.Single(_client.SendKeys);
    }

    [Fact]
    public async Task Collected_entries_are_purged_after_their_retention()
    {
        await using var d = Dispatcher(o => { o.PurgeInterval = TimeSpan.FromHours(1); o.CollectedRetention = TimeSpan.FromDays(1); });
        await EnqueueAsync();
        _client.OnSend = (_, _, _) => Answer(ScriptedA2AClient.Make("t", TaskState.Completed));
        await d.RunOnceAsync();
        var leased = Assert.Single(await _store.LeaseResultsAsync(null, 10, TimeSpan.FromMinutes(1)));
        Assert.True(await _store.AcknowledgeAsync(leased));

        _time.Advance(TimeSpan.FromHours(2));
        await d.RunOnceAsync();
        Assert.NotNull(await _store.GetAsync("m1")); // purge is due but the retention is a day

        _time.Advance(TimeSpan.FromDays(1));
        await d.RunOnceAsync();
        Assert.Null(await _store.GetAsync("m1"));
    }
}
