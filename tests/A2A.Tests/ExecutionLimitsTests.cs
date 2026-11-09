using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using A2A.Diagnostics;
using A2A.Server;
using A2A.Server.Execution;
using A2A.Server.Idempotency;
using A2A.Server.Storage;
using A2A.Tests.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace A2A.Tests;

/// <summary>
/// The execution limit (<see cref="A2ALimits.MaxConcurrentExecutions"/> and the start queue) and the task budgets
/// (<see cref="A2ALimits.MaxTaskTokens"/>, <see cref="A2ALimits.MaxTaskCostUnits"/>) through a running server. Tests wait on
/// signals and on the fake clock, never on a delay.
/// </summary>
public sealed class ExecutionLimitsTests
{
    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static ServerCallContext Call(string user = "alice") => new()
    {
        Principal = user,
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user)], "Test")),
    };

    private static SendMessageRequest Req(string text, string? taskId = null, string? messageId = null, bool immediately = false)
    {
        var message = Message.FromUser(text, taskId: taskId);
        if (messageId is not null) message.MessageId = messageId;
        return new SendMessageRequest
        {
            Message = message,
            Configuration = immediately ? new SendMessageConfiguration { ReturnImmediately = true } : null,
        };
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Scripted by the first word of the message: <c>block</c> works and waits for its gate (and stops with its token),
    /// <c>stubborn</c> waits for its gate and ignores the token, <c>input</c> asks for input, <c>throw</c> throws, anything else
    /// completes. <see cref="Started"/> is set when a message reaches the agent.
    /// </summary>
    private sealed class ScriptAgent : IContinuableAgentExecutor
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();

        public TaskCompletionSource Started(string text) => _started.GetOrAdd(text, _ => NewSignal());

        public TaskCompletionSource Gate(string text) => _gates.GetOrAdd(text, _ => NewSignal());

        public Func<AgentRequestContext, TaskUpdater, CancellationToken, Task>? OnExecute { get; set; }

        public Func<AgentTask, TaskUpdater, CancellationToken, Task>? OnCancel { get; set; }

        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
        {
            var text = context.UserText;
            Started(text).TrySetResult();
            if (OnExecute is not null)
            {
                await OnExecute(context, updater, cancellationToken);
                return;
            }
            switch (text.Split(' ')[0])
            {
                case "block":
                    await updater.StartWorkAsync(cancellationToken: cancellationToken);
                    await Gate(text).Task.WaitAsync(cancellationToken);
                    await updater.CompleteAsync("done", cancellationToken);
                    break;
                case "stubborn":
                    await updater.StartWorkAsync(cancellationToken: cancellationToken);
                    await Gate(text).Task;
                    break;
                case "input":
                    await updater.RequireInputAsync("more?", cancellationToken);
                    break;
                case "throw":
                    throw new InvalidOperationException("boom");
                default:
                    await updater.CompleteAsync("done", cancellationToken);
                    break;
            }
        }

        public Task CancelAsync(AgentTask task, TaskUpdater updater, CancellationToken cancellationToken) =>
            OnCancel is { } cancel ? cancel(task, updater, cancellationToken) : updater.CancelAsync(cancellationToken: cancellationToken).AsTask();

        public Task ContinueAsync(TaskContinuationContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
            updater.CompleteAsync("continued", cancellationToken).AsTask();
    }

    private static Task<TestAgentHost> StartAsync(ScriptAgent agent, Action<A2AServerOptions>? configure = null, Action<IServiceCollection>? services = null) =>
        TestAgentHost.StartAsync(configure, services: s =>
        {
            s.AddSingleton<IAgentExecutor>(agent);
            services?.Invoke(s);
        });

    private static Action<A2AServerOptions> Limits(int max, int queue = 0, double waitSeconds = 30, int perTenant = 0) => o =>
    {
        o.Limits.MaxConcurrentExecutions = max;
        o.Limits.MaxConcurrentExecutionsPerTenant = perTenant;
        o.Limits.MaxQueuedExecutions = queue;
        o.Limits.MaxExecutionQueueWait = TimeSpan.FromSeconds(waitSeconds);
    };

    /// <summary>Starts a <c>block ...</c> task and waits until the agent runs, so it holds a slot.</summary>
    private static async Task<AgentTask> StartBlockedAsync(TestAgentHost host, ScriptAgent agent, string text)
    {
        var task = (await host.Handler.SendMessageAsync(Req(text, immediately: true), Call()).WaitAsync(Wait)).RequireTask();
        await agent.Started(text).Task.WaitAsync(Wait);
        return task;
    }

    private static Task WaitUnusedAsync(TestAgentHost host) =>
        host.Engine.Admission.WhenAsync((running, queued) => running == 0 && queued == 0).WaitAsync(Wait);

    private static async Task<A2AException> RefusedAsync(Func<Task> start)
    {
        var ex = await Assert.ThrowsAsync<A2AException>(start);
        Assert.Equal(A2AErrorCode.RateLimited, ex.Code);
        Assert.Equal("execution_capacity", ex.Metadata!["reason"]);
        return ex;
    }

    // ---------------------------------------------------------------- defaults

    [Fact]
    public void Defaults_are_on_for_concurrency_and_off_for_budgets()
    {
        var limits = new A2ALimits();
        Assert.Equal(1024, limits.MaxConcurrentExecutions);
        Assert.Equal(0, limits.MaxConcurrentExecutionsPerTenant);
        Assert.Equal(256, limits.MaxQueuedExecutions);
        Assert.Equal(TimeSpan.FromSeconds(10), limits.MaxExecutionQueueWait);
        Assert.Null(limits.MaxTaskTokens);
        Assert.Null(limits.MaxTaskCostUnits);
    }

    [Fact]
    public async Task Default_options_do_not_reject_a_moderate_burst()
    {
        var agent = new ScriptAgent();
        await using var host = await StartAsync(agent);

        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(i =>
            host.Handler.SendMessageAsync(Req($"quick {i}"), Call()).WaitAsync(Wait)));

        Assert.All(results, r => Assert.Equal(TaskState.Completed, r.RequireTask().Status.State));
        await WaitUnusedAsync(host);
    }

    // ---------------------------------------------------------------- rejection

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Burst_over_the_limit_gets_429_with_retry_after_and_stores_no_task(string binding)
    {
        var agent = new ScriptAgent();
        await using var host = await StartAsync(agent, Limits(max: 2, queue: 0));
        await StartBlockedAsync(host, agent, "block one");
        await StartBlockedAsync(host, agent, "block two");

        using var http = host.Http();
        using var resp = await http.SendAsync(RawSend(binding, "block three", "m-three"));

        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), resp.Headers.RetryAfter?.Delta);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.Contains("RATE_LIMITED", text);
        Assert.Contains("execution_capacity", text);
        Assert.Contains("retryable", text);
        Assert.False(agent.Started("block three").Task.IsCompleted);
        var list = await host.Handler.ListTasksAsync(new ListTasksRequest(), Call());
        Assert.Equal(2, list.Tasks.Count); // the refused request stored nothing

        agent.Gate("block one").TrySetResult();
        agent.Gate("block two").TrySetResult();
        await WaitUnusedAsync(host);
    }

    [Fact]
    public async Task Streaming_send_over_the_limit_is_refused_before_anything_is_stored()
    {
        var agent = new ScriptAgent();
        await using var host = await StartAsync(agent, Limits(max: 1, queue: 0));
        await StartBlockedAsync(host, agent, "block one");

        await RefusedAsync(async () =>
        {
            await foreach (var _ in host.Handler.SendStreamingMessageAsync(Req("quick"), Call())) { }
        });

        Assert.Single((await host.Handler.ListTasksAsync(new ListTasksRequest(), Call())).Tasks);
        agent.Gate("block one").TrySetResult();
        await WaitUnusedAsync(host);
    }

    [Fact]
    public async Task Queued_send_starts_when_a_slot_frees()
    {
        var agent = new ScriptAgent();
        await using var host = await StartAsync(agent, Limits(max: 1, queue: 5));
        await StartBlockedAsync(host, agent, "block one");

        var second = host.Handler.SendMessageAsync(Req("quick two"), Call()).WaitAsync(Wait);
        await host.Engine.Admission.WhenAsync((_, queued) => queued == 1).WaitAsync(Wait);
        Assert.False(agent.Started("quick two").Task.IsCompleted); // waiting, not running
        Assert.False(second.IsCompleted);

        agent.Gate("block one").TrySetResult();

        Assert.Equal(TaskState.Completed, (await second).RequireTask().Status.State);
        await WaitUnusedAsync(host);
    }

    [Fact]
    public async Task Queued_streaming_send_starts_when_a_slot_frees()
    {
        var agent = new ScriptAgent();
        await using var host = await StartAsync(agent, Limits(max: 1, queue: 5));
        await StartBlockedAsync(host, agent, "block one");

        var events = Task.Run(async () =>
        {
            var seen = new List<StreamResponse>();
            await foreach (var e in host.Handler.SendStreamingMessageAsync(Req("quick two"), Call())) seen.Add(e.Event);
            return seen;
        }).WaitAsync(Wait);
        await host.Engine.Admission.WhenAsync((_, queued) => queued == 1).WaitAsync(Wait);
        Assert.False(agent.Started("quick two").Task.IsCompleted);

        agent.Gate("block one").TrySetResult();

        var seenEvents = await events;
        Assert.Contains(seenEvents, e => e is StatusUpdateEvent { StatusUpdate.Status.State: TaskState.Completed });
        await WaitUnusedAsync(host);
    }

    [Fact]
    public async Task Queue_timeout_refuses_the_start_and_stores_nothing()
    {
        var time = new FakeTimeProvider();
        var agent = new ScriptAgent();
        await using var host = await StartAsync(agent, Limits(max: 1, queue: 5, waitSeconds: 20), services: s => s.AddSingleton<TimeProvider>(time));
        await StartBlockedAsync(host, agent, "block one");

        var second = host.Handler.SendMessageAsync(Req("quick two"), Call()).WaitAsync(Wait);
        await host.Engine.Admission.WhenAsync((_, queued) => queued == 1).WaitAsync(Wait);
        time.Advance(TimeSpan.FromSeconds(20));

        var ex = await RefusedAsync(() => second);
        Assert.Equal(TimeSpan.FromSeconds(20), ex.RetryAfter);
        Assert.Single((await host.Handler.ListTasksAsync(new ListTasksRequest(), Call())).Tasks);
        agent.Gate("block one").TrySetResult();
        await WaitUnusedAsync(host);
    }

    [Fact]
    public async Task Queued_request_renews_its_idempotency_claim_every_heartbeat()
    {
        var time = new FakeTimeProvider();
        var agent = new ScriptAgent();
        var store = new CountingIdempotencyStore();
        await using var host = await StartAsync(agent, o =>
        {
            Limits(max: 1, queue: 5, waitSeconds: 60)(o);
            o.Coordination.Heartbeat = TimeSpan.FromSeconds(5);
            o.Coordination.LeaseTtl = TimeSpan.FromSeconds(30);
        }, services: s =>
        {
            s.AddSingleton<TimeProvider>(time);
            s.AddSingleton<IdempotencyStore>(sp => store.Wrap(new InMemoryIdempotencyStore(sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Idempotency, time)));
        });
        // Warm up: the first run creates the timers the server keeps for good (for example the idempotency sweep).
        (await host.Handler.SendMessageAsync(Req("quick warm", messageId: "m-warm"), Call()).WaitAsync(Wait)).RequireTask();
        await WaitUnusedAsync(host);
        await StartBlockedAsync(host, agent, "block one");
        var timersBefore = time.PendingTimers;

        var second = host.Handler.SendMessageAsync(Req("quick two", messageId: "m-queued"), Call()).WaitAsync(Wait);
        await host.Engine.Admission.WhenAsync((_, queued) => queued == 1).WaitAsync(Wait);
        Assert.True(time.PendingTimers >= timersBefore + 2, "the wait's timeout and the claim renewal timers exist");
        Assert.Equal(0, store.Renews);

        time.Advance(TimeSpan.FromSeconds(5));
        await store.Renewed.Task.WaitAsync(Wait);

        agent.Gate("block one").TrySetResult();
        Assert.Equal(TaskState.Completed, (await second).RequireTask().Status.State);
        await WaitUnusedAsync(host);
        // That the wait's timers are disposed when it ends is checked on the admission itself (ExecutionAdmissionTests).
    }

    [Fact]
    public async Task A_refused_follow_up_leaves_the_task_history_unchanged()
    {
        var agent = new ScriptAgent();
        await using var host = await StartAsync(agent, Limits(max: 1, queue: 0));
        var first = (await host.Handler.SendMessageAsync(Req("input first"), Call()).WaitAsync(Wait)).RequireTask();
        Assert.Equal(TaskState.InputRequired, first.Status.State);
        await WaitUnusedAsync(host);
        var before = await host.Handler.GetTaskAsync(new GetTaskRequest { Id = first.Id }, Call());
        await StartBlockedAsync(host, agent, "block holder");

        await RefusedAsync(() => host.Handler.SendMessageAsync(Req("again", taskId: first.Id), Call()));

        var after = await host.Handler.GetTaskAsync(new GetTaskRequest { Id = first.Id }, Call());
        Assert.Equal(before.History!.Count, after.History!.Count);
        Assert.Equal(before.Status.Timestamp, after.Status.Timestamp);
        Assert.False(agent.Started("again").Task.IsCompleted);
        agent.Gate("block holder").TrySetResult();
        await WaitUnusedAsync(host);
    }

    [Fact]
    public async Task An_idempotent_retry_after_a_rejection_runs()
    {
        var agent = new ScriptAgent();
        await using var host = await StartAsync(agent, Limits(max: 1, queue: 0));
        await StartBlockedAsync(host, agent, "block holder");
        var request = Req("quick retry", messageId: "m-retry");

        await RefusedAsync(() => host.Handler.SendMessageAsync(request, Call()));
        Assert.False(agent.Started("quick retry").Task.IsCompleted);
        agent.Gate("block holder").TrySetResult();
        await WaitUnusedAsync(host);
        var retried = (await host.Handler.SendMessageAsync(request, Call()).WaitAsync(Wait)).RequireTask();

        Assert.Equal(TaskState.Completed, retried.Status.State);
        Assert.True(agent.Started("quick retry").Task.IsCompleted);
    }

    [Fact]
    public async Task An_idempotent_replay_needs_no_slot_when_the_server_is_full()
    {
        var agent = new ScriptAgent();
        await using var host = await StartAsync(agent, Limits(max: 1, queue: 0));
        var request = Req("quick once", messageId: "m-once");
        var first = (await host.Handler.SendMessageAsync(request, Call()).WaitAsync(Wait)).RequireTask();
        await WaitUnusedAsync(host);
        await StartBlockedAsync(host, agent, "block holder");

        var replay = (await host.Handler.SendMessageAsync(request, Call()).WaitAsync(Wait)).RequireTask();

        Assert.Equal(first.Id, replay.Id);
        agent.Gate("block holder").TrySetResult();
        await WaitUnusedAsync(host);
    }

    [Fact]
    public async Task CancelTask_runs_when_the_server_is_full_and_takes_no_slot()
    {
        var agent = new ScriptAgent();
        await using var host = await StartAsync(agent, o =>
        {
            Limits(max: 1, queue: 0)(o);
            o.CancelGracePeriod = TimeSpan.FromMilliseconds(200);
        });
        var task = await StartBlockedAsync(host, agent, "stubborn holder"); // holds the only slot and ignores its token

        var canceled = await host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = task.Id }, Call()).WaitAsync(Wait);

        Assert.Equal(TaskState.Canceled, canceled.Status.State);
        Assert.Equal(1, host.Engine.Admission.Running); // only the holder: the cancel run is not counted
        agent.Gate("stubborn holder").TrySetResult();
        await WaitUnusedAsync(host);
    }

    [Fact]
    public async Task ContinueTaskAsync_returns_AtCapacity_when_full()
    {
        var agent = new ScriptAgent();
        await using var host = await StartAsync(agent, Limits(max: 1, queue: 0));
        var task = (await host.Handler.SendMessageAsync(Req("input first"), Call()).WaitAsync(Wait)).RequireTask();
        await WaitUnusedAsync(host);
        await StartBlockedAsync(host, agent, "block holder");
        var key = new TaskKey("", "alice", task.Id);

        var refused = await host.Engine.ContinueTaskAsync(key);

        Assert.Equal(TaskContinuationStatus.AtCapacity, refused.Status);
        Assert.False(refused.Started);
        Assert.True(refused.Completion.IsCompleted);
        agent.Gate("block holder").TrySetResult();
        await WaitUnusedAsync(host);

        var started = await host.Engine.ContinueTaskAsync(key);
        Assert.Equal(TaskContinuationStatus.Started, started.Status);
        await started.Completion.WaitAsync(Wait);
        await WaitUnusedAsync(host);
    }

    // ---------------------------------------------------------------- the slot is always given back

    [Fact]
    public async Task Slot_is_released_after_the_agent_returns()
    {
        var agent = new ScriptAgent();
        await using var host = await StartAsync(agent, Limits(max: 1, queue: 0));

        for (var i = 0; i < 3; i++)
            Assert.Equal(TaskState.Completed, (await host.Handler.SendMessageAsync(Req($"quick {i}"), Call()).WaitAsync(Wait)).RequireTask().Status.State);

        await WaitUnusedAsync(host);
        Assert.Equal(0, host.Engine.Admission.TrackedTenants);
    }

    [Fact]
    public async Task Slot_is_released_after_the_agent_throws()
    {
        var agent = new ScriptAgent();
        await using var host = await StartAsync(agent, Limits(max: 1, queue: 0));

        var failed = (await host.Handler.SendMessageAsync(Req("throw now"), Call()).WaitAsync(Wait)).RequireTask();
        Assert.Equal(TaskState.Failed, failed.Status.State);
        await WaitUnusedAsync(host);

        Assert.Equal(TaskState.Completed, (await host.Handler.SendMessageAsync(Req("quick"), Call()).WaitAsync(Wait)).RequireTask().Status.State);
        await WaitUnusedAsync(host);
    }

    /// <summary>The system clock, whose timers fail while <see cref="Fail"/> is set: the execution's heartbeat timer cannot start.</summary>
    private sealed class FailingTimers : TimeProvider
    {
        public volatile bool Fail;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Fail ? throw new InvalidOperationException("timer unavailable") : System.CreateTimer(callback, state, dueTime, period);
    }

    [Fact]
    public async Task Slot_is_released_after_a_failed_start()
    {
        var clock = new FailingTimers();
        var agent = new ScriptAgent();
        await using var host = await StartAsync(agent, Limits(max: 1, queue: 0), services: s => s.AddSingleton<TimeProvider>(clock));

        clock.Fail = true;
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => host.Handler.SendMessageAsync(Req("quick lost"), Call()).WaitAsync(Wait));
        }
        finally
        {
            clock.Fail = false;
        }

        Assert.Equal(0, host.Engine.Admission.Running);
        Assert.Equal(TaskState.Completed, (await host.Handler.SendMessageAsync(Req("quick after"), Call()).WaitAsync(Wait)).RequireTask().Status.State);
        await WaitUnusedAsync(host);
    }

    [Fact]
    public async Task Slot_is_held_until_the_agent_returned_and_gives_the_next_waiter_its_turn()
    {
        var agent = new ScriptAgent();
        await using var host = await StartAsync(agent, Limits(max: 1, queue: 5, perTenant: 1));
        await StartBlockedAsync(host, agent, "block one");
        var second = host.Handler.SendMessageAsync(Req("quick two"), Call()).WaitAsync(Wait);
        await host.Engine.Admission.WhenAsync((_, queued) => queued == 1).WaitAsync(Wait);

        agent.Gate("block one").TrySetResult();

        Assert.Equal(TaskState.Completed, (await second).RequireTask().Status.State);
        await WaitUnusedAsync(host);
    }

    // ---------------------------------------------------------------- budgets

    private static (long Tokens, decimal Cost) Usage(AgentTask task) => TaskUsageMeter.Read(task.Metadata);

    private static Task<TestAgentHost> BudgetHostAsync(ScriptAgent agent, long? tokens = null, decimal? cost = null) =>
        StartAsync(agent, o =>
        {
            o.Limits.MaxTaskTokens = tokens;
            o.Limits.MaxTaskCostUnits = cost;
        });

    [Fact]
    public async Task Crossing_the_token_budget_fails_the_task_fires_the_token_and_throws_to_the_agent()
    {
        var thrown = NewSignal();
        var tokenFired = false;
        var agent = new ScriptAgent
        {
            OnExecute = async (_, u, ct) =>
            {
                await u.ReportUsageAsync(60, cancellationToken: ct);
                try
                {
                    await u.ReportUsageAsync(50, cancellationToken: ct);
                    thrown.TrySetException(new InvalidOperationException("the report did not throw"));
                }
                catch (OperationCanceledException)
                {
                    tokenFired = ct.IsCancellationRequested;
                    thrown.TrySetResult();
                    throw;
                }
            },
        };
        await using var host = await BudgetHostAsync(agent, tokens: 100);

        var task = (await host.Handler.SendMessageAsync(Req("spend"), Call()).WaitAsync(Wait)).RequireTask();

        await thrown.Task.WaitAsync(Wait);
        Assert.True(tokenFired);
        Assert.Equal(TaskState.Failed, task.Status.State);
        Assert.Equal(TaskExecutionEngine.BudgetExceededText, task.Status.Message!.GetText());
        Assert.Equal(110, Usage(task).Tokens);
        await WaitUnusedAsync(host);
    }

    [Fact]
    public async Task Crossing_the_cost_budget_fails_the_task()
    {
        var agent = new ScriptAgent
        {
            OnExecute = async (_, u, ct) =>
            {
                await u.ReportUsageAsync(0, 1.0m, ct);
                await u.ReportUsageAsync(0, 0.6m, ct); // 1.6 > 1.5
                await u.CompleteAsync("not reached", ct);
            },
        };
        await using var host = await BudgetHostAsync(agent, cost: 1.5m);

        var task = (await host.Handler.SendMessageAsync(Req("spend"), Call()).WaitAsync(Wait)).RequireTask();

        Assert.Equal(TaskState.Failed, task.Status.State);
        Assert.Equal(TaskExecutionEngine.BudgetExceededText, task.Status.Message!.GetText());
        Assert.Equal(1.6m, Usage(task).Cost);
        await WaitUnusedAsync(host);
    }

    [Fact]
    public async Task Usage_exactly_at_the_budget_passes()
    {
        var agent = new ScriptAgent
        {
            OnExecute = async (_, u, ct) =>
            {
                await u.ReportUsageAsync(100, 2.5m, ct);
                await u.CompleteAsync("done", ct);
            },
        };
        await using var host = await BudgetHostAsync(agent, tokens: 100, cost: 2.5m);

        var task = (await host.Handler.SendMessageAsync(Req("spend"), Call()).WaitAsync(Wait)).RequireTask();

        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal((100L, 2.5m), Usage(task));
    }

    [Fact]
    public async Task Usage_is_stored_in_task_metadata_and_carries_over_to_a_follow_up()
    {
        long seenAtStart = -1;
        var agent = new ScriptAgent
        {
            OnExecute = async (context, u, ct) =>
            {
                if (context.IsNewTask)
                {
                    await u.ReportUsageAsync(40, 0.5m, ct);
                    await u.RequireInputAsync("more?", ct);
                    return;
                }
                seenAtStart = u.UsedTokens;
                await u.ReportUsageAsync(30, 0.25m, ct);
                Assert.Equal(70, u.UsedTokens);
                Assert.Equal(0.75m, u.UsedCostUnits);
                await u.CompleteAsync("done", ct);
            },
        };
        await using var host = await BudgetHostAsync(agent, tokens: 1000);

        var first = (await host.Handler.SendMessageAsync(Req("spend"), Call()).WaitAsync(Wait)).RequireTask();
        Assert.Equal((40L, 0.5m), Usage(first));
        Assert.True(first.Metadata!.ContainsKey("a2a.usage"));
        await WaitUnusedAsync(host);
        var second = (await host.Handler.SendMessageAsync(Req("again", taskId: first.Id), Call()).WaitAsync(Wait)).RequireTask();

        Assert.Equal(40, seenAtStart);
        Assert.Equal(TaskState.Completed, second.Status.State);
        Assert.Equal((70L, 0.75m), Usage(second));
    }

    [Fact]
    public async Task A_follow_up_that_crosses_the_budget_with_earlier_usage_fails()
    {
        var agent = new ScriptAgent
        {
            OnExecute = async (context, u, ct) =>
            {
                if (context.IsNewTask)
                {
                    await u.ReportUsageAsync(80, cancellationToken: ct);
                    await u.RequireInputAsync("more?", ct);
                    return;
                }
                await u.ReportUsageAsync(30, cancellationToken: ct); // 110 in all
                await u.CompleteAsync("not reached", ct);
            },
        };
        await using var host = await BudgetHostAsync(agent, tokens: 100);

        var first = (await host.Handler.SendMessageAsync(Req("spend"), Call()).WaitAsync(Wait)).RequireTask();
        Assert.Equal(TaskState.InputRequired, first.Status.State);
        await WaitUnusedAsync(host);
        var second = (await host.Handler.SendMessageAsync(Req("again", taskId: first.Id), Call()).WaitAsync(Wait)).RequireTask();

        Assert.Equal(TaskState.Failed, second.Status.State);
        Assert.Equal(TaskExecutionEngine.BudgetExceededText, second.Status.Message!.GetText());
        Assert.Equal(110, Usage(second).Tokens);
    }

    [Fact]
    public async Task An_agent_cannot_write_the_usage_key_in_event_metadata()
    {
        var agent = new ScriptAgent
        {
            OnExecute = async (_, u, ct) =>
            {
                var fake = JsonSerializer.SerializeToElement(new { tokens = 999999, costUnits = 5 });
                await u.UpdateStatusAsync(TaskState.Working, null, new() { ["a2a.usage"] = fake }, ct);
                await u.AddArtifactAsync(new Artifact { ArtifactId = "a", Parts = [Part.FromText("x")] },
                    new Dictionary<string, JsonElement> { ["a2a.usage"] = fake }, cancellationToken: ct);
                await u.ReportUsageAsync(5, cancellationToken: ct);
                await u.CompleteAsync("done", ct);
            },
        };
        await using var host = await BudgetHostAsync(agent, tokens: 100);

        var task = (await host.Handler.SendMessageAsync(Req("spend"), Call()).WaitAsync(Wait)).RequireTask();

        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal((5L, 0m), Usage(task));
    }

    [Fact]
    public async Task Without_a_report_the_usage_key_never_appears()
    {
        var agent = new ScriptAgent
        {
            OnExecute = async (_, u, ct) =>
            {
                var fake = JsonSerializer.SerializeToElement(new { tokens = 999999 });
                await u.UpdateStatusAsync(TaskState.Working, null, new() { ["a2a.usage"] = fake }, ct);
                await u.CompleteAsync("done", ct);
            },
        };
        await using var host = await BudgetHostAsync(agent, tokens: 100);

        var task = (await host.Handler.SendMessageAsync(Req("spend"), Call()).WaitAsync(Wait)).RequireTask();

        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.False(task.Metadata?.ContainsKey("a2a.usage") ?? false);
    }

    [Fact]
    public async Task Negative_usage_throws()
    {
        var errors = new ConcurrentBag<Exception>();
        var agent = new ScriptAgent
        {
            OnExecute = async (_, u, ct) =>
            {
                try { await u.ReportUsageAsync(-1, cancellationToken: ct); } catch (Exception ex) { errors.Add(ex); }
                try { await u.ReportUsageAsync(1, -0.01m, ct); } catch (Exception ex) { errors.Add(ex); }
                Assert.Equal(0, u.UsedTokens); // neither report counted
                await u.CompleteAsync("done", ct);
            },
        };
        await using var host = await BudgetHostAsync(agent, tokens: 100);

        var task = (await host.Handler.SendMessageAsync(Req("spend"), Call()).WaitAsync(Wait)).RequireTask();

        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.IsType<ArgumentOutOfRangeException>(e));
        Assert.False(task.Metadata?.ContainsKey("a2a.usage") ?? false);
    }

    [Fact]
    public async Task Usage_saturates_instead_of_wrapping_without_a_budget()
    {
        var agent = new ScriptAgent
        {
            OnExecute = async (_, u, ct) =>
            {
                await u.ReportUsageAsync(long.MaxValue, decimal.MaxValue, ct);
                await u.ReportUsageAsync(10, 10m, ct);
                Assert.Equal(long.MaxValue, u.UsedTokens);
                Assert.Equal(decimal.MaxValue, u.UsedCostUnits);
                await u.CompleteAsync("done", ct);
            },
        };
        await using var host = await BudgetHostAsync(agent);

        var task = (await host.Handler.SendMessageAsync(Req("spend"), Call()).WaitAsync(Wait)).RequireTask();

        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal((long.MaxValue, decimal.MaxValue), Usage(task));
    }

    [Fact]
    public async Task A_saturated_total_still_fails_a_budget_and_a_follow_up_keeps_it_saturated()
    {
        var agent = new ScriptAgent
        {
            OnExecute = async (context, u, ct) =>
            {
                if (context.IsNewTask)
                {
                    await u.ReportUsageAsync(long.MaxValue - 5, cancellationToken: ct);
                    await u.RequireInputAsync("more?", ct);
                    return;
                }
                await u.ReportUsageAsync(long.MaxValue, cancellationToken: ct); // a wrapped sum would be small and pass
                await u.CompleteAsync("not reached", ct);
            },
        };
        await using var host = await BudgetHostAsync(agent, tokens: long.MaxValue - 1);

        var first = (await host.Handler.SendMessageAsync(Req("spend"), Call()).WaitAsync(Wait)).RequireTask();
        Assert.Equal(TaskState.InputRequired, first.Status.State);
        await WaitUnusedAsync(host);
        var second = (await host.Handler.SendMessageAsync(Req("again", taskId: first.Id), Call()).WaitAsync(Wait)).RequireTask();

        Assert.Equal(TaskState.Failed, second.Status.State);
        Assert.Equal(long.MaxValue, Usage(second).Tokens);
    }

    [Fact]
    public async Task No_budget_means_no_failure()
    {
        var agent = new ScriptAgent
        {
            OnExecute = async (_, u, ct) =>
            {
                await u.ReportUsageAsync(1_000_000_000_000, 1_000_000m, ct);
                await u.CompleteAsync("done", ct);
            },
        };
        // Zero and negative budgets are off too.
        await using var host = await StartAsync(agent, o =>
        {
            o.Limits.MaxTaskTokens = 0;
            o.Limits.MaxTaskCostUnits = -1m;
        });

        var task = (await host.Handler.SendMessageAsync(Req("spend"), Call()).WaitAsync(Wait)).RequireTask();

        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal(1_000_000_000_000, Usage(task).Tokens);
    }

    [Fact]
    public async Task A_report_after_a_terminal_state_is_ignored()
    {
        var agent = new ScriptAgent
        {
            OnExecute = async (_, u, ct) =>
            {
                await u.CompleteAsync("done", ct);
                await u.ReportUsageAsync(1_000_000, cancellationToken: ct); // would cross the budget
                Assert.Equal(0, u.UsedTokens);
            },
        };
        await using var host = await BudgetHostAsync(agent, tokens: 100);

        var task = (await host.Handler.SendMessageAsync(Req("spend"), Call()).WaitAsync(Wait)).RequireTask();
        await WaitUnusedAsync(host);

        Assert.Equal(TaskState.Completed, task.Status.State);
    }

    [Fact]
    public async Task A_CancelTask_run_counts_usage_but_has_no_budget()
    {
        var agent = new ScriptAgent
        {
            OnExecute = async (_, u, ct) =>
            {
                await u.ReportUsageAsync(60, cancellationToken: ct);
                await u.RequireInputAsync("more?", ct);
            },
            OnCancel = async (_, u, ct) =>
            {
                Assert.Equal(60, u.UsedTokens); // seeded from the stored task
                await u.ReportUsageAsync(1000, cancellationToken: ct); // far over the budget of 100
                await u.CancelAsync("stopped", ct);
            },
        };
        await using var host = await BudgetHostAsync(agent, tokens: 100);
        var task = (await host.Handler.SendMessageAsync(Req("spend"), Call()).WaitAsync(Wait)).RequireTask();
        await WaitUnusedAsync(host);

        var canceled = await host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = task.Id }, Call()).WaitAsync(Wait);

        Assert.Equal(TaskState.Canceled, canceled.Status.State);
        Assert.Equal(1060, Usage(canceled).Tokens);
    }

    [Fact]
    public async Task Concurrent_reports_from_parallel_tasks_in_one_agent_sum_correctly()
    {
        var agent = new ScriptAgent
        {
            OnExecute = async (_, u, ct) =>
            {
                await u.StartWorkAsync(cancellationToken: ct);
                await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
                {
                    for (var i = 0; i < 100; i++) await u.ReportUsageAsync(3, 0.5m, ct);
                }, ct)));
                Assert.Equal(2400, u.UsedTokens);
                Assert.Equal(400m, u.UsedCostUnits);
                await u.CompleteAsync("done", ct);
            },
        };
        await using var host = await BudgetHostAsync(agent);

        var task = (await host.Handler.SendMessageAsync(Req("spend"), Call()).WaitAsync(Wait)).RequireTask();

        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal((2400L, 400m), Usage(task));
    }

    [Fact]
    public async Task Usage_of_two_runs_of_one_task_adds_up_in_the_stored_task()
    {
        var firstRunning = NewSignal();
        var secondDone = NewSignal();
        var agent = new ScriptAgent
        {
            OnExecute = async (context, u, ct) =>
            {
                if (context.IsNewTask)
                {
                    await u.ReportUsageAsync(10, cancellationToken: ct);
                    await u.RequireInputAsync("more?", ct);
                    return;
                }
                if (context.UserText == "again one")
                {
                    await u.StartWorkAsync(cancellationToken: ct);
                    firstRunning.TrySetResult();
                    await secondDone.Task.WaitAsync(ct);
                    await u.ReportUsageAsync(5, cancellationToken: ct);
                    await u.CompleteAsync("done", ct);
                    return;
                }
                await u.ReportUsageAsync(7, cancellationToken: ct);
                await u.UpdateStatusAsync(TaskState.Working, null, new() { ["note"] = JsonSerializer.SerializeToElement(1) }, ct);
            },
        };
        await using var host = await StartAsync(agent, o => o.FollowUpPolicy = FollowUpPolicy.AllowSteering);

        var first = (await host.Handler.SendMessageAsync(Req("spend"), Call()).WaitAsync(Wait)).RequireTask();
        await WaitUnusedAsync(host);
        var runOne = host.Handler.SendMessageAsync(Req("again one", taskId: first.Id, immediately: true), Call()).WaitAsync(Wait);
        await firstRunning.Task.WaitAsync(Wait);
        await host.Handler.SendMessageAsync(Req("again two", taskId: first.Id, immediately: true), Call()).WaitAsync(Wait);

        // Run one may end the task only after run two's event is stored: an event that reaches a finished task is dropped, with its usage.
        using var timeout = new CancellationTokenSource(Wait);
        while (Usage(await host.Handler.GetTaskAsync(new GetTaskRequest { Id = first.Id }, Call(), timeout.Token)).Tokens != 10 + 7)
            await Task.Delay(5, timeout.Token);
        secondDone.TrySetResult();
        await runOne;
        await WaitUnusedAsync(host);

        var stored = await host.Handler.GetTaskAsync(new GetTaskRequest { Id = first.Id }, Call());
        Assert.Equal(10 + 7 + 5, Usage(stored).Tokens); // the stored total is what is summed: no run overwrote another
    }

    // ---------------------------------------------------------------- helpers

    private static HttpRequestMessage RawSend(string binding, string text, string messageId, string user = "alice")
    {
        var body = "{\"message\":{\"messageId\":\"" + messageId + "\",\"role\":\"ROLE_USER\",\"parts\":[{\"text\":\"" + text + "\"}]}}";
        var rpc = binding == ProtocolBindings.JsonRpc;
        var req = new HttpRequestMessage(HttpMethod.Post, rpc ? "/a2a" : "/a2a/message:send")
        {
            Content = new StringContent(
                rpc ? "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + A2AProtocol.Methods.SendMessage + "\",\"params\":" + body + "}" : body,
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        req.Headers.Add("X-Test-User", user);
        return req;
    }

    /// <summary>Delegates to another store and counts the renewals.</summary>
    private sealed class CountingIdempotencyStore : IdempotencyStore
    {
        private IdempotencyStore _inner = null!;
        private int _renews;

        public int Renews => Volatile.Read(ref _renews);

        public TaskCompletionSource Renewed { get; } = NewSignal();

        public CountingIdempotencyStore Wrap(IdempotencyStore inner)
        {
            _inner = inner;
            return this;
        }

        public override ValueTask<IdempotencyResult> TryBeginAsync(IdempotencyScope scope, byte[] requestHash, CancellationToken cancellationToken = default) =>
            _inner.TryBeginAsync(scope, requestHash, cancellationToken);

        public override ValueTask CompleteAsync(IdempotencyScope scope, Guid claimId, IdempotencyOutcome outcome, CancellationToken cancellationToken = default) =>
            _inner.CompleteAsync(scope, claimId, outcome, cancellationToken);

        public override ValueTask AbandonAsync(IdempotencyScope scope, Guid claimId, CancellationToken cancellationToken = default) =>
            _inner.AbandonAsync(scope, claimId, cancellationToken);

        public override ValueTask RenewAsync(IdempotencyScope scope, Guid claimId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _renews);
            Renewed.TrySetResult();
            return _inner.RenewAsync(scope, claimId, cancellationToken);
        }
    }
}

/// <summary>The execution metrics. The meter is process-wide, so these run alone (the telemetry collection) and use deltas.</summary>
[Collection(TelemetryCollection.Name)]
public sealed class ExecutionLimitsMetricsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private sealed class Counters : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly object _gate = new();
        private long _active;
        private long _queued;
        private long _budget;
        private readonly Dictionary<string, long> _rejected = new();

        public Counters()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == A2ATelemetry.ServerMeterName && (instrument.Name.StartsWith("a2a.server.executions.", StringComparison.Ordinal)
                    || instrument.Name == "a2a.server.tasks.budget_exceeded"))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                lock (_gate)
                {
                    switch (instrument.Name)
                    {
                        case "a2a.server.executions.active": _active += value; break;
                        case "a2a.server.executions.queued": _queued += value; break;
                        case "a2a.server.tasks.budget_exceeded": _budget += value; break;
                        case "a2a.server.executions.rejected":
                            var reason = "";
                            foreach (var tag in tags) if (tag.Key == "a2a.rejection.reason") reason = (string?)tag.Value ?? "";
                            _rejected[reason] = _rejected.GetValueOrDefault(reason) + value;
                            break;
                    }
                }
            });
            _listener.Start();
        }

        public long Active { get { lock (_gate) return _active; } }
        public long Queued { get { lock (_gate) return _queued; } }
        public long Budget { get { lock (_gate) return _budget; } }
        public long Rejected(string reason) { lock (_gate) return _rejected.GetValueOrDefault(reason); }

        public void Dispose() => _listener.Dispose();
    }

    private static ServerCallContext Call() => new()
    {
        Principal = "alice",
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "Test")),
    };

    private static SendMessageRequest Req(string text, bool immediately = false) => new()
    {
        Message = Message.FromUser(text),
        Configuration = immediately ? new SendMessageConfiguration { ReturnImmediately = true } : null,
    };

    private sealed class GateAgent : IAgentExecutor
    {
        public ConcurrentDictionary<string, TaskCompletionSource> Gates { get; } = new();
        public ConcurrentDictionary<string, TaskCompletionSource> Started { get; } = new();

        public TaskCompletionSource Gate(string text) => Gates.GetOrAdd(text, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        public TaskCompletionSource StartedSignal(string text) => Started.GetOrAdd(text, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
        {
            var text = context.UserText;
            if (text.StartsWith("block", StringComparison.Ordinal))
            {
                await updater.StartWorkAsync(cancellationToken: cancellationToken);
                StartedSignal(text).TrySetResult();
                await Gate(text).Task.WaitAsync(cancellationToken);
            }
            await updater.CompleteAsync("done", cancellationToken);
        }
    }

    [Fact]
    public async Task Queue_depth_active_and_rejections_are_counted_and_return_to_zero()
    {
        using var counters = new Counters();
        var agent = new GateAgent();
        await using var host = await TestAgentHost.StartAsync(o =>
        {
            o.Limits.MaxConcurrentExecutions = 1;
            o.Limits.MaxQueuedExecutions = 1;
            o.Limits.MaxExecutionQueueWait = TimeSpan.FromSeconds(30);
        }, services: s => s.AddSingleton<IAgentExecutor>(agent));
        var active0 = counters.Active;
        var queued0 = counters.Queued;
        var queueFull0 = counters.Rejected("queue_full");

        await host.Handler.SendMessageAsync(Req("block one", immediately: true), Call()).WaitAsync(Wait);
        await agent.StartedSignal("block one").Task.WaitAsync(Wait);
        Assert.Equal(active0 + 1, counters.Active);

        var queuedSend = host.Handler.SendMessageAsync(Req("quick two"), Call()).WaitAsync(Wait);
        await host.Engine.Admission.WhenAsync((_, queued) => queued == 1).WaitAsync(Wait);
        Assert.Equal(queued0 + 1, counters.Queued);

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Handler.SendMessageAsync(Req("quick three"), Call()).WaitAsync(Wait));
        Assert.Equal(A2AErrorCode.RateLimited, ex.Code);
        Assert.Equal(queueFull0 + 1, counters.Rejected("queue_full"));

        agent.Gate("block one").TrySetResult();
        await queuedSend;
        await host.Engine.Admission.WhenAsync((running, queued) => running == 0 && queued == 0).WaitAsync(Wait);
        Assert.Equal(active0, counters.Active);
        Assert.Equal(queued0, counters.Queued);
    }

    [Fact]
    public async Task A_queue_timeout_is_counted_by_reason()
    {
        using var counters = new Counters();
        var time = new FakeTimeProvider();
        var agent = new GateAgent();
        await using var host = await TestAgentHost.StartAsync(o =>
        {
            o.Limits.MaxConcurrentExecutions = 1;
            o.Limits.MaxExecutionQueueWait = TimeSpan.FromSeconds(15);
        }, services: s =>
        {
            s.AddSingleton<IAgentExecutor>(agent);
            s.AddSingleton<TimeProvider>(time);
        });
        var timeout0 = counters.Rejected("queue_timeout");
        var queued0 = counters.Queued;
        await host.Handler.SendMessageAsync(Req("block one", immediately: true), Call()).WaitAsync(Wait);
        await agent.StartedSignal("block one").Task.WaitAsync(Wait);
        var waiting = host.Handler.SendMessageAsync(Req("quick two"), Call()).WaitAsync(Wait);
        await host.Engine.Admission.WhenAsync((_, queued) => queued == 1).WaitAsync(Wait);

        time.Advance(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAsync<A2AException>(() => waiting);

        Assert.Equal(timeout0 + 1, counters.Rejected("queue_timeout"));
        Assert.Equal(queued0, counters.Queued);
        agent.Gate("block one").TrySetResult();
        await host.Engine.Admission.WhenAsync((running, queued) => running == 0 && queued == 0).WaitAsync(Wait);
    }

    [Fact]
    public async Task Exceeding_a_budget_is_counted()
    {
        using var counters = new Counters();
        await using var host = await TestAgentHost.StartAsync(o => o.Limits.MaxTaskTokens = 10, services: s =>
            s.AddSingleton<IAgentExecutor>(new DelegateAgentExecutor(async (_, u, ct) => await u.ReportUsageAsync(11, cancellationToken: ct))));
        var before = counters.Budget;

        var task = (await host.Handler.SendMessageAsync(Req("spend"), Call()).WaitAsync(Wait)).RequireTask();

        Assert.Equal(TaskState.Failed, task.Status.State);
        Assert.Equal(before + 1, counters.Budget);
    }
}
