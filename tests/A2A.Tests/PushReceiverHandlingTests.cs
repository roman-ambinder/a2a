using System.Collections.Concurrent;
using System.Text;
using A2A.Client;
using A2A.Client.Push;
using A2A.Push;

namespace A2A.Tests;

/// <summary>HandleAsync, the delivery store, the task tracker and RegisterPushAsync.</summary>
public class PushReceiverHandlingTests
{
    private static readonly byte[] Secret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static byte[] Body(string taskId = "t1", string state = "TASK_STATE_WORKING") =>
        Encoding.UTF8.GetBytes("{\"statusUpdate\":{\"taskId\":\"" + taskId + "\",\"contextId\":\"c1\",\"status\":{\"state\":\"" + state + "\"}}}");

    private static Dictionary<string, string> TokenHeaders(string id, string token = "tok", long? sequence = null)
    {
        var h = new Dictionary<string, string> { [A2AProtocol.Headers.NotificationToken] = token, [A2AProtocol.Headers.DeliveryId] = id };
        if (sequence is { } s) h[A2AProtocol.Headers.NotificationSequence] = s.ToString();
        return h;
    }

    private static Dictionary<string, string> SignedHeaders(ManualClock clock, string id, byte[] body, byte[]? secret = null)
    {
        var ts = clock.GetUtcNow().ToUnixTimeSeconds();
        return new Dictionary<string, string>
        {
            [WebhookSignature.IdHeader] = id,
            [WebhookSignature.TimestampHeader] = ts.ToString(),
            [WebhookSignature.SignatureHeader] = WebhookSignature.Sign(secret ?? Secret, id, ts, body),
        };
    }

    // ---- ExpectedTask (spec §4.3.3) ----

    private static readonly Func<PushDelivery, CancellationToken, Task> Ok = (_, _) => Task.CompletedTask;

    [Fact]
    public async Task A_delivery_for_an_unexpected_task_is_refused_before_the_handler()
    {
        var calls = 0;
        var receiver = new PushNotificationReceiver { ExpectedTask = (id, _) => ValueTask.FromResult(id == "mine") };

        var other = await receiver.HandleAsync(TokenHeaders("d1"), Body("theirs"), (_, _) => { calls++; return Task.CompletedTask; }, expectedToken: "tok");
        var mine = await receiver.HandleAsync(TokenHeaders("d2"), Body("mine"), (_, _) => { calls++; return Task.CompletedTask; }, expectedToken: "tok");

        Assert.Equal(PushHandleOutcome.UnexpectedTask, other.Outcome);
        Assert.Equal(404, other.StatusCode);
        Assert.Equal(PushHandleOutcome.Handled, mine.Outcome);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task An_event_without_a_task_id_is_unexpected_when_the_check_is_on()
    {
        var receiver = new PushNotificationReceiver { ExpectedTask = (_, _) => ValueTask.FromResult(true) };
        var body = Encoding.UTF8.GetBytes("{\"message\":{\"messageId\":\"m1\",\"role\":\"ROLE_AGENT\",\"parts\":[{\"text\":\"hi\"}]}}");

        var result = await receiver.HandleAsync(TokenHeaders("d1"), body, Ok, expectedToken: "tok");

        Assert.Equal(PushHandleOutcome.UnexpectedTask, result.Outcome);
    }

    [Fact]
    public async Task Without_the_check_every_task_reaches_the_handler()
    {
        var receiver = new PushNotificationReceiver();
        var result = await receiver.HandleAsync(TokenHeaders("d1"), Body("anything"), Ok, expectedToken: "tok");
        Assert.Equal(PushHandleOutcome.Handled, result.Outcome);
    }

    [Fact]
    public async Task A_refused_delivery_is_not_remembered_and_a_handled_one_stays_a_duplicate()
    {
        var expected = new HashSet<string>();
        var receiver = new PushNotificationReceiver { ExpectedTask = (id, _) => ValueTask.FromResult(expected.Contains(id)) };

        Assert.Equal(PushHandleOutcome.UnexpectedTask, (await receiver.HandleAsync(TokenHeaders("d1"), Body("t1"), Ok, expectedToken: "tok")).Outcome);
        expected.Add("t1");
        Assert.Equal(PushHandleOutcome.Handled, (await receiver.HandleAsync(TokenHeaders("d1"), Body("t1"), Ok, expectedToken: "tok")).Outcome);
        expected.Clear(); // the task finished and is no longer tracked
        Assert.Equal(PushHandleOutcome.Duplicate, (await receiver.HandleAsync(TokenHeaders("d1"), Body("t1"), Ok, expectedToken: "tok")).Outcome);
    }

    [Fact]
    public async Task A_failing_check_is_a_handler_failure_and_the_retry_gets_through()
    {
        var fail = true;
        var receiver = new PushNotificationReceiver
        {
            ExpectedTask = (_, _) => fail ? throw new InvalidOperationException("store down") : ValueTask.FromResult(true)
        };

        var first = await receiver.HandleAsync(TokenHeaders("d1"), Body(), Ok, expectedToken: "tok");
        fail = false;
        var retry = await receiver.HandleAsync(TokenHeaders("d1"), Body(), Ok, expectedToken: "tok");

        Assert.Equal(PushHandleOutcome.HandlerFailed, first.Outcome);
        Assert.Equal(500, first.StatusCode);
        Assert.Equal(PushHandleOutcome.Handled, retry.Outcome);
    }

    [Fact]
    public async Task The_tracker_is_the_expected_task_check()
    {
        var client = new FakeClient(_ => throw new InvalidOperationException("not called"));
        using var tracker = new PushTaskTracker(client, (_, _) => Task.CompletedTask, sweepInterval: Timeout.InfiniteTimeSpan);
        await tracker.TrackAsync("t1", TimeSpan.FromMinutes(5));
        var receiver = new PushNotificationReceiver { ExpectedTask = tracker.IsTrackedAsync };

        Assert.Equal(PushHandleOutcome.Handled, (await receiver.HandleAsync(TokenHeaders("d1"), Body("t1"), Ok, expectedToken: "tok")).Outcome);
        Assert.Equal(PushHandleOutcome.UnexpectedTask, (await receiver.HandleAsync(TokenHeaders("d2"), Body("t2"), Ok, expectedToken: "tok")).Outcome);
        Assert.False(await tracker.IsTrackedAsync(""));
    }

    // ---- HandleAsync ----

    [Fact]
    public async Task Handler_failure_forgets_the_id_so_the_retry_reaches_the_handler_again()
    {
        var receiver = new PushNotificationReceiver();
        var calls = 0;
        var body = Body();

        var first = await receiver.HandleAsync(TokenHeaders("d1"), body, (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("db down");
        }, expectedToken: "tok");
        Assert.Equal(PushHandleOutcome.HandlerFailed, first.Outcome);
        Assert.Equal(500, first.StatusCode);
        Assert.IsType<InvalidOperationException>(first.Error);

        var retry = await receiver.HandleAsync(TokenHeaders("d1"), body, (_, _) => { calls++; return Task.CompletedTask; }, expectedToken: "tok");
        Assert.Equal(PushHandleOutcome.Handled, retry.Outcome);
        Assert.Equal(200, retry.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Validate_loses_the_retry_after_a_handler_failure_which_is_why_HandleAsync_exists()
    {
        var receiver = new PushNotificationReceiver();
        Assert.Equal(PushValidationResult.Valid, receiver.Validate(TokenHeaders("d1"), "tok"));
        // The app fails here and answers 500. The sender retries with the same id:
        Assert.Equal(PushValidationResult.Duplicate, receiver.Validate(TokenHeaders("d1"), "tok"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Success_then_retry_is_a_duplicate_and_does_not_run_the_handler()
    {
        var receiver = new PushNotificationReceiver();
        var calls = 0;
        Task Handler(PushDelivery d, CancellationToken ct) { calls++; return Task.CompletedTask; }

        var first = await receiver.HandleAsync(TokenHeaders("d1"), Body(), Handler, expectedToken: "tok");
        var retry = await receiver.HandleAsync(TokenHeaders("d1"), Body(), Handler, expectedToken: "tok");

        Assert.Equal(PushHandleOutcome.Handled, first.Outcome);
        Assert.Equal(PushHandleOutcome.Duplicate, retry.Outcome);
        Assert.Equal(200, retry.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Concurrent_delivery_with_the_same_id_gets_a_retryable_503_and_the_handler_runs_once()
    {
        var receiver = new PushNotificationReceiver();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var calls = 0;

        var firstCall = receiver.HandleAsync(TokenHeaders("d1"), Body(), async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            started.SetResult();
            await release.Task;
        }, expectedToken: "tok");
        await started.Task;

        var second = await receiver.HandleAsync(TokenHeaders("d1"), Body(), (_, _) => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, expectedToken: "tok");
        Assert.Equal(PushHandleOutcome.InProgress, second.Outcome);
        Assert.Equal(503, second.StatusCode);
        Assert.Contains(second.StatusCode, new[] { 408, 429, 500, 502, 503, 504 }); // a status the sender retries; never 409

        release.SetResult();
        Assert.Equal(PushHandleOutcome.Handled, (await firstCall).Outcome);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_crashed_claim_can_be_taken_over_after_the_lease()
    {
        var clock = new ManualClock();
        var receiver = new PushNotificationReceiver(clock) { ProcessingLease = TimeSpan.FromMinutes(1) };
        var gate = new TaskCompletionSource();
        var stuck = receiver.HandleAsync(TokenHeaders("d1"), Body(), (_, _) => gate.Task, expectedToken: "tok");

        Assert.Equal(PushHandleOutcome.InProgress,
            (await receiver.HandleAsync(TokenHeaders("d1"), Body(), (_, _) => Task.CompletedTask, expectedToken: "tok")).Outcome);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(PushHandleOutcome.Handled,
            (await receiver.HandleAsync(TokenHeaders("d1"), Body(), (_, _) => Task.CompletedTask, expectedToken: "tok")).Outcome);

        gate.SetResult();
        await stuck;
    }

    [Fact]
    public async Task Bad_token_gets_401_and_does_not_run_the_handler()
    {
        var receiver = new PushNotificationReceiver();
        var calls = 0;
        var result = await receiver.HandleAsync(TokenHeaders("d1", token: "wrong"), Body(), (_, _) => { calls++; return Task.CompletedTask; }, expectedToken: "tok");
        Assert.Equal(PushHandleOutcome.Unauthorized, result.Outcome);
        Assert.Equal(401, result.StatusCode);
        Assert.Equal(0, calls);

        // A bad delivery must not poison the id: the real one still gets through.
        Assert.Equal(PushHandleOutcome.Handled,
            (await receiver.HandleAsync(TokenHeaders("d1"), Body(), (_, _) => { calls++; return Task.CompletedTask; }, expectedToken: "tok")).Outcome);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Bad_or_stale_signature_gets_401_and_does_not_run_the_handler()
    {
        var clock = new ManualClock();
        var receiver = new PushNotificationReceiver(clock);
        var calls = 0;
        Task Handler(PushDelivery d, CancellationToken ct) { calls++; return Task.CompletedTask; }
        var body = Body();

        var wrongSecret = await receiver.HandleAsync(SignedHeaders(clock, "d1", body, new byte[32]), body, Handler, signingSecret: Secret);
        Assert.Equal((PushHandleOutcome.Unauthorized, 401), (wrongSecret.Outcome, wrongSecret.StatusCode));

        var tampered = await receiver.HandleAsync(SignedHeaders(clock, "d2", body), Body("other"), Handler, signingSecret: Secret);
        Assert.Equal(401, tampered.StatusCode);

        var missing = await receiver.HandleAsync(new Dictionary<string, string>(), body, Handler, signingSecret: Secret);
        Assert.Equal(401, missing.StatusCode);

        var old = SignedHeaders(clock, "d3", body);
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(401, (await receiver.HandleAsync(old, body, Handler, signingSecret: Secret)).StatusCode);

        Assert.Equal(0, calls);
        var good = await receiver.HandleAsync(SignedHeaders(clock, "d4", body), body, Handler, signingSecret: Secret);
        Assert.Equal(PushHandleOutcome.Handled, good.Outcome);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Token_and_signature_must_both_match_when_both_are_given()
    {
        var clock = new ManualClock();
        var receiver = new PushNotificationReceiver(clock);
        var body = Body();
        var headers = SignedHeaders(clock, "d1", body);
        headers[A2AProtocol.Headers.NotificationToken] = "tok";

        Assert.Equal(401, (await receiver.HandleAsync(headers, body, (_, _) => Task.CompletedTask, "other", Secret)).StatusCode);
        Assert.Equal(200, (await receiver.HandleAsync(headers, body, (_, _) => Task.CompletedTask, "tok", Secret)).StatusCode);
    }

    [Fact]
    public async Task Handler_gets_the_event_the_delivery_id_and_the_sequence()
    {
        var receiver = new PushNotificationReceiver();
        PushDelivery? seen = null;
        await receiver.HandleAsync(TokenHeaders("d9", sequence: 7), Body("t9"), (d, _) => { seen = d; return Task.CompletedTask; }, expectedToken: "tok");

        Assert.NotNull(seen);
        Assert.Equal("d9", seen.DeliveryId);
        Assert.Equal(7, seen.Sequence);
        Assert.Equal("t9", Assert.IsType<StatusUpdateEvent>(seen.Event).StatusUpdate.TaskId);
    }

    [Fact]
    public async Task Garbage_body_with_good_credentials_gets_400_and_the_id_stays_free()
    {
        var receiver = new PushNotificationReceiver();
        var calls = 0;
        var bad = await receiver.HandleAsync(TokenHeaders("d1"), Encoding.UTF8.GetBytes("not json"), (_, _) => { calls++; return Task.CompletedTask; }, expectedToken: "tok");
        Assert.Equal(400, bad.StatusCode);
        Assert.Equal(0, calls);
        Assert.Equal(200, (await receiver.HandleAsync(TokenHeaders("d1"), Body(), (_, _) => { calls++; return Task.CompletedTask; }, expectedToken: "tok")).StatusCode);
    }

    [Fact]
    public async Task A_cancelled_handler_forgets_the_id_and_rethrows()
    {
        var receiver = new PushNotificationReceiver();
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiver.HandleAsync(TokenHeaders("d1"), Body(), (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, expectedToken: "tok", cancellationToken: cts.Token));
        Assert.Equal(PushHandleOutcome.Handled,
            (await receiver.HandleAsync(TokenHeaders("d1"), Body(), (_, _) => Task.CompletedTask, expectedToken: "tok")).Outcome);
    }

    [Fact]
    public async Task HandleAsync_needs_a_token_or_a_secret()
    {
        var receiver = new PushNotificationReceiver();
        await Assert.ThrowsAsync<ArgumentException>(() => receiver.HandleAsync(TokenHeaders("d1"), Body(), (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task Handled_ids_are_forgotten_after_ten_minutes()
    {
        var clock = new ManualClock();
        var receiver = new PushNotificationReceiver(clock);
        var calls = 0;
        Task Handler(PushDelivery d, CancellationToken ct) { calls++; return Task.CompletedTask; }

        await receiver.HandleAsync(TokenHeaders("d1"), Body(), Handler, expectedToken: "tok");
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(PushHandleOutcome.Duplicate, (await receiver.HandleAsync(TokenHeaders("d1"), Body(), Handler, expectedToken: "tok")).Outcome);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(PushHandleOutcome.Handled, (await receiver.HandleAsync(TokenHeaders("d1"), Body(), Handler, expectedToken: "tok")).Outcome);
        Assert.Equal(2, calls);
    }

    // ---- delivery store ----

    private sealed class RecordingStore : IPushDeliveryStore
    {
        private readonly InMemoryPushDeliveryStore _inner = new();
        public ConcurrentQueue<string> Calls { get; } = new();

        public Task<PushDeliveryClaim> TryBeginAsync(string id, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken ct)
        {
            Calls.Enqueue("begin:" + id);
            return _inner.TryBeginAsync(id, now, leaseUntil, ct);
        }

        public Task CompleteAsync(string id, DateTimeOffset keepUntil, CancellationToken ct)
        {
            Calls.Enqueue("complete:" + id);
            return _inner.CompleteAsync(id, keepUntil, ct);
        }

        public Task ReleaseAsync(string id, CancellationToken ct)
        {
            Calls.Enqueue("release:" + id);
            return _inner.ReleaseAsync(id, ct);
        }
    }

    [Fact]
    public async Task The_store_is_used_and_a_second_receiver_over_the_same_store_sees_the_duplicate()
    {
        var store = new RecordingStore();
        var first = new PushNotificationReceiver(store);
        var calls = 0;
        Task Handler(PushDelivery d, CancellationToken ct) { calls++; return Task.CompletedTask; }

        await first.HandleAsync(TokenHeaders("d1"), Body(), Handler, expectedToken: "tok");
        await first.HandleAsync(TokenHeaders("d2"), Body(), (_, _) => throw new InvalidOperationException(), expectedToken: "tok");
        Assert.Equal(["begin:delivery:d1", "complete:delivery:d1", "begin:delivery:d2", "release:delivery:d2"], store.Calls.ToArray());

        var restarted = new PushNotificationReceiver(store); // a new instance, as after a restart with a durable store
        Assert.Equal(PushHandleOutcome.Duplicate, (await restarted.HandleAsync(TokenHeaders("d1"), Body(), Handler, expectedToken: "tok")).Outcome);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task With_the_default_in_memory_store_a_restart_repeats_the_delivery()
    {
        var calls = 0;
        Task Handler(PushDelivery d, CancellationToken ct) { calls++; return Task.CompletedTask; }
        await new PushNotificationReceiver().HandleAsync(TokenHeaders("d1"), Body(), Handler, expectedToken: "tok");
        await new PushNotificationReceiver().HandleAsync(TokenHeaders("d1"), Body(), Handler, expectedToken: "tok");
        Assert.Equal(2, calls);
    }

    // ---- tracker ----

    private sealed class FakeClient(Func<string, AgentTask> getTask) : IA2AClient
    {
        public ConcurrentQueue<string> Calls { get; } = new();

        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue("GetTask:" + request.Id);
            return Task.FromResult(getTask(request.Id));
        }

        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue("Register:" + config.TaskId);
            return Task.FromResult(config);
        }

        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static AgentTask TaskIn(string id, TaskState state) => new() { Id = id, Status = new AgentTaskStatus { State = state } };

    private static StreamResponse Update(string taskId, TaskState state) =>
        new StatusUpdateEvent(new TaskStatusUpdateEvent { TaskId = taskId, Status = new AgentTaskStatus { State = state } });

    private sealed class Rig : IDisposable
    {
        public ManualClock Clock { get; } = new();
        public InMemoryPushTaskStore Store { get; } = new();
        public ConcurrentQueue<PushTaskCheck> Checks { get; } = new();
        public FakeClient Client { get; }
        public PushTaskTracker Tracker { get; }

        public Rig(TaskState serverState = TaskState.Working)
        {
            Client = new FakeClient(id => TaskIn(id, serverState));
            Tracker = new PushTaskTracker(Client, (c, _) => { Checks.Enqueue(c); return Task.CompletedTask; }, Store, Clock, Timeout.InfiniteTimeSpan);
        }

        public void Dispose() => Tracker.Dispose();
    }

    [Fact]
    public async Task Deadline_passing_without_a_final_state_calls_GetTask_and_reports_it()
    {
        using var rig = new Rig(TaskState.Completed);
        await rig.Tracker.TrackAsync("t1", TimeSpan.FromSeconds(30));

        await rig.Tracker.CheckDueAsync();
        Assert.Empty(rig.Client.Calls); // not due yet

        rig.Clock.Advance(TimeSpan.FromSeconds(31));
        await rig.Tracker.CheckDueAsync();

        Assert.Equal(["GetTask:t1"], rig.Client.Calls.ToArray());
        var check = Assert.Single(rig.Checks);
        Assert.Equal(PushCheckReason.DeadlinePassed, check.Reason);
        Assert.Equal(TaskState.Completed, check.Task!.Status.State);
        Assert.Null(await rig.Store.GetAsync("t1", default)); // it was final: cleaned up
    }

    [Fact]
    public async Task A_check_that_finds_the_task_still_running_checks_again_after_the_timeout()
    {
        using var rig = new Rig(TaskState.Working);
        await rig.Tracker.TrackAsync("t1", TimeSpan.FromSeconds(30));
        rig.Clock.Advance(TimeSpan.FromSeconds(31));
        await rig.Tracker.CheckDueAsync();
        await rig.Tracker.CheckDueAsync(); // not due again yet
        Assert.Single(rig.Client.Calls);

        rig.Clock.Advance(TimeSpan.FromSeconds(31));
        await rig.Tracker.CheckDueAsync();
        Assert.Equal(2, rig.Client.Calls.Count);
        Assert.NotNull(await rig.Store.GetAsync("t1", default));
    }

    [Fact]
    public async Task A_sequence_gap_triggers_GetTask_at_once()
    {
        using var rig = new Rig(TaskState.Working);
        await rig.Tracker.TrackAsync("t1", TimeSpan.FromHours(1));

        await rig.Tracker.NotifyAsync(Update("t1", TaskState.Working), 1);
        await rig.Tracker.NotifyAsync(Update("t1", TaskState.Working), 2);
        Assert.Empty(rig.Client.Calls); // no gap

        await rig.Tracker.NotifyAsync(Update("t1", TaskState.Working), 4); // 3 is missing

        Assert.Equal(["GetTask:t1"], rig.Client.Calls.ToArray());
        Assert.Equal(PushCheckReason.SequenceGap, Assert.Single(rig.Checks).Reason);

        await rig.Tracker.NotifyAsync(Update("t1", TaskState.Working), 5); // the gap is not reported twice
        Assert.Single(rig.Client.Calls);
    }

    [Fact]
    public async Task A_repeated_or_late_sequence_is_not_a_gap()
    {
        using var rig = new Rig();
        await rig.Tracker.TrackAsync("t1", TimeSpan.FromHours(1));
        await rig.Tracker.NotifyAsync(Update("t1", TaskState.Working), 5);
        await rig.Tracker.NotifyAsync(Update("t1", TaskState.Working), 5);
        await rig.Tracker.NotifyAsync(Update("t1", TaskState.Working), 3);
        Assert.Empty(rig.Client.Calls);
    }

    [Fact]
    public async Task A_final_state_cleans_up_the_entry_and_no_check_follows()
    {
        using var rig = new Rig();
        await rig.Tracker.TrackAsync("t1", TimeSpan.FromSeconds(30));
        Assert.NotNull(await rig.Store.GetAsync("t1", default));

        await rig.Tracker.NotifyAsync(Update("t1", TaskState.Completed), 3);
        Assert.Null(await rig.Store.GetAsync("t1", default));

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await rig.Tracker.CheckDueAsync();
        Assert.Empty(rig.Client.Calls);
        Assert.Empty(rig.Checks);
    }

    [Fact]
    public async Task A_final_task_event_and_an_interrupted_state_also_clean_up()
    {
        using var rig = new Rig();
        await rig.Tracker.TrackAsync("t1", TimeSpan.FromSeconds(30));
        await rig.Tracker.TrackAsync("t2", TimeSpan.FromSeconds(30));

        await rig.Tracker.NotifyAsync(new TaskEvent(TaskIn("t1", TaskState.Failed)));
        await rig.Tracker.NotifyAsync(Update("t2", TaskState.InputRequired));

        Assert.Null(await rig.Store.GetAsync("t1", default));
        Assert.Null(await rig.Store.GetAsync("t2", default));
    }

    [Fact]
    public async Task Notifications_for_untracked_tasks_are_ignored()
    {
        using var rig = new Rig();
        await rig.Tracker.NotifyAsync(Update("nobody", TaskState.Working), 9);
        await rig.Tracker.NotifyAsync(Update("nobody", TaskState.Working), 20);
        Assert.Empty(rig.Client.Calls);
    }

    [Fact]
    public async Task A_failing_GetTask_is_reported_and_the_task_stays_tracked()
    {
        var clock = new ManualClock();
        var checks = new ConcurrentQueue<PushTaskCheck>();
        var store = new InMemoryPushTaskStore();
        var client = new FakeClient(_ => throw new HttpRequestException("down"));
        using var tracker = new PushTaskTracker(client, (c, _) => { checks.Enqueue(c); return Task.CompletedTask; }, store, clock, Timeout.InfiniteTimeSpan);
        await tracker.TrackAsync("t1", TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(11));

        await tracker.CheckDueAsync();

        var check = Assert.Single(checks);
        Assert.IsType<HttpRequestException>(check.Error);
        Assert.Null(check.Task);
        Assert.NotNull(await store.GetAsync("t1", default));
    }

    [Fact]
    public async Task The_timer_sweeps_overdue_tasks()
    {
        var clock = new ManualClock();
        var checked1 = new TaskCompletionSource<PushTaskCheck>();
        var client = new FakeClient(id => TaskIn(id, TaskState.Completed));
        using var tracker = new PushTaskTracker(client, (c, _) => { checked1.TrySetResult(c); return Task.CompletedTask; }, null, clock, TimeSpan.FromMilliseconds(20));
        await tracker.TrackAsync("t1", TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));

        var check = await checked1.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("t1", check.TaskId);
    }

    [Fact]
    public async Task The_tracker_works_from_a_receiver_handler()
    {
        using var rig = new Rig();
        await rig.Tracker.TrackAsync("t1", TimeSpan.FromHours(1));
        var receiver = new PushNotificationReceiver();

        await receiver.HandleAsync(TokenHeaders("d1", sequence: 1), Body("t1"), (d, ct) => rig.Tracker.NotifyAsync(d, ct), expectedToken: "tok");
        await receiver.HandleAsync(TokenHeaders("d3", sequence: 3), Body("t1"), (d, ct) => rig.Tracker.NotifyAsync(d, ct), expectedToken: "tok");

        Assert.Equal(PushCheckReason.SequenceGap, Assert.Single(rig.Checks).Reason);
    }

    // ---- RegisterPushAsync ----

    [Fact]
    public async Task RegisterPushAsync_registers_first_then_calls_GetTask_and_sees_a_finished_task()
    {
        var fake = new FakeClient(id => TaskIn(id, TaskState.Completed));
        var client = new A2AClient(fake);

        var result = await client.RegisterPushAsync("t1", new TaskPushNotificationConfig { Url = "https://hooks.example/cb" });

        Assert.Equal(["Register:t1", "GetTask:t1"], fake.Calls.ToArray());
        Assert.True(result.IsFinal);
        Assert.Equal(TaskState.Completed, result.Task.Status.State);
        Assert.Equal("t1", result.Config.TaskId);
    }

    [Fact]
    public async Task RegisterPushAsync_reports_a_running_task_as_not_final()
    {
        var client = new A2AClient(new FakeClient(id => TaskIn(id, TaskState.Working)));
        var result = await client.RegisterPushAsync("t1", new TaskPushNotificationConfig { Url = "https://hooks.example/cb" });
        Assert.False(result.IsFinal);
    }

    [Fact]
    public async Task RegisterPushAsync_refuses_a_config_for_another_task()
    {
        var client = new A2AClient(new FakeClient(id => TaskIn(id, TaskState.Working)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.RegisterPushAsync("t1", new TaskPushNotificationConfig { TaskId = "t2", Url = "https://hooks.example/cb" }));
    }
}
