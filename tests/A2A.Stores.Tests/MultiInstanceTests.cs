using System.Collections.Concurrent;
using System.Security.Claims;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Threading.Channels;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Coordination;
using A2A.Server.Postgres;
using A2A.Server.Redis;
using A2A.Server.Retention;
using A2A.Server.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace A2A.Stores.Tests;

/// <summary>Two server instances that share PostgreSQL stores and the Redis bus behave like one server.</summary>
[Collection(ContainersCollection.Name)]
public sealed class MultiInstanceTests(PostgresFixture db, RedisFixture redisFixture)
{
    private const string BaseUrl = "http://localhost/a2a";

    [SkippableFact]
    public async Task Task_started_on_one_instance_streams_and_reads_on_another()
    {
        var schema = await db.NewSchemaAsync();
        var redis = await redisFixture.ConnectAsync();
        await using var _ = redis;
        var prefix = $"mi:{Guid.NewGuid():N}:";

        await using var a = await StartAsync(schema, redis, prefix);
        await using var b = await StartAsync(schema, redis, prefix);

        var gate = GatedAgent.Gate("job-1");
        var started = await Client(a).SendMessageAsync(Message.FromUser("job-1"), new SendMessageConfiguration { ReturnImmediately = true });
        var taskId = Assert.IsType<TaskResponse>(started).Task.Id;

        // Instance B reads the task from the shared store and streams its live events from instance A.
        var fromB = Client(b);
        Assert.Equal(taskId, (await fromB.GetTaskAsync(taskId)).Id);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var events = new List<StreamResponse>();
        var stream = Task.Run(async () =>
        {
            await foreach (var e in fromB.SubscribeToTaskAsync(taskId, cancellationToken: cts.Token))
            {
                lock (events) events.Add(e);
                if (events.Count == 1) gate.TrySetResult(); // B is subscribed: let the agent on A finish
            }
        });
        await stream.WaitAsync(cts.Token);

        Assert.IsType<TaskEvent>(events[0]); // the snapshot
        var final = Assert.IsType<StatusUpdateEvent>(events[^1]);
        Assert.Equal(TaskState.Completed, final.StatusUpdate.Status.State);
        Assert.Equal(TaskState.Completed, (await fromB.GetTaskAsync(taskId)).Status.State);

        // ListTasks on either instance sees it.
        var listed = await Client(a).ListTasksAsync(new ListTasksRequest());
        Assert.Contains(listed.Tasks, t => t.Id == taskId);
    }


    [SkippableFact]
    public async Task CancelTask_on_one_instance_cancels_the_execution_on_another()
    {
        var schema = await db.NewSchemaAsync();
        var redis = await redisFixture.ConnectAsync();
        await using var _ = redis;
        var prefix = $"mi:{Guid.NewGuid():N}:";
        await using var a = await StartAsync(schema, redis, prefix);
        await using var b = await StartAsync(schema, redis, prefix);

        var text = $"observe {Guid.NewGuid():N}";
        await Client(a).SendMessageAsync(Message.FromUser(text), new SendMessageConfiguration { ReturnImmediately = true });
        var taskId = await GatedAgent.Started(text).Task.WaitAsync(Timeout);

        var canceled = await Client(b).CancelTaskAsync(taskId);

        Assert.Equal(TaskState.Canceled, canceled.Status.State);
        await GatedAgent.Cancelled(text).Task.WaitAsync(Timeout); // the agent on A saw its token fire
        Assert.Equal(TaskState.Canceled, (await Client(a).GetTaskAsync(taskId)).Status.State);
    }

    [SkippableFact]
    public async Task Blocking_retry_on_one_instance_returns_when_the_execution_on_another_finishes()
    {
        var schema = await db.NewSchemaAsync();
        var redis = await redisFixture.ConnectAsync();
        await using var _ = redis;
        var prefix = $"mi:{Guid.NewGuid():N}:";
        await using var a = await StartAsync(schema, redis, prefix);
        await using var b = await StartAsync(schema, redis, prefix);

        var text = $"job {Guid.NewGuid():N}";
        var message = Message.FromUser(text); // the same message id: B's send is a retry of A's
        var first = Client(a).SendMessageAsync(message);
        var taskId = await GatedAgent.Started(text).Task.WaitAsync(Timeout);

        var retry = Client(b).SendMessageAsync(message);
        await Task.WhenAny(retry, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.False(retry.IsCompleted, "B returned while the agent on A was still working.");

        GatedAgent.Gate(text).TrySetResult();
        var fromB = Assert.IsType<TaskResponse>(await retry.WaitAsync(Timeout)).Task;
        Assert.Equal(taskId, fromB.Id);
        Assert.Equal(TaskState.Completed, fromB.Status.State);
        Assert.Equal(TaskState.Completed, Assert.IsType<TaskResponse>(await first.WaitAsync(Timeout)).Task.Status.State);
    }

    /// <summary>
    /// Review Focus 2: a follow-up on B runs while the first execution still writes on A. Subscribers on both instances get
    /// the same events in the same order.
    /// </summary>
    [SkippableFact]
    public async Task Follow_up_on_one_instance_during_an_execution_on_another_streams_the_same_events_everywhere()
    {
        var schema = await db.NewSchemaAsync();
        var redis = await redisFixture.ConnectAsync();
        await using var _ = redis;
        var prefix = $"mi:{Guid.NewGuid():N}:";
        await using var a = await StartAsync(schema, redis, prefix);
        await using var b = await StartAsync(schema, redis, prefix);

        var text = $"interleave {Guid.NewGuid():N}";
        await Client(a).SendMessageAsync(Message.FromUser(text), new SendMessageConfiguration { ReturnImmediately = true });
        var taskId = await GatedAgent.Started(text).Task.WaitAsync(Timeout);
        await EventuallyAsync(async () => (await Client(a).GetTaskAsync(taskId)).Status.State == TaskState.InputRequired, "the task asks for input");

        using var cts = new CancellationTokenSource(Timeout);
        var onA = Collect(Client(a), taskId, cts.Token);
        var onB = Collect(Client(b), taskId, cts.Token);
        await Task.WhenAll(onA.Subscribed, onB.Subscribed);

        // The follow-up runs on B; its agent lets A's execution go on writing at the same time.
        var followUp = new Message { MessageId = Message.NewId(), Role = Role.User, TaskId = taskId, Parts = [Part.FromText(text + " more")] };
        var reply = Assert.IsType<TaskResponse>(await Client(b).SendMessageAsync(followUp)).Task;
        Assert.True(reply.Status.State == TaskState.Completed, $"{reply.Status.State}: {reply.Status.Message?.GetText()}");

        var eventsA = await onA.Events;
        var eventsB = await onB.Events;
        Assert.Contains(eventsA, e => e.Contains("first 0", StringComparison.Ordinal)); // both executions wrote
        Assert.Contains(eventsA, e => e.Contains("second 0", StringComparison.Ordinal));
        Assert.Equal(eventsA, eventsB);
        Assert.Contains("COMPLETED", eventsA[^1], StringComparison.Ordinal); // the stream ended on the final event
    }

    /// <summary>
    /// Review Focus 1, end to end: instance A dies mid-execution (its Redis connection drops, then the host goes away without
    /// unregistering). Its lease stops being renewed, and instance B sees it expire. B's retention sweep then fails the task:
    /// a subscriber on B gets FAILED with "The agent stopped unexpectedly.", and the PostgreSQL row is FAILED.
    /// </summary>
    [SkippableFact]
    public async Task Execution_of_a_dead_instance_is_reported_expired_on_another_and_failed_by_its_sweep()
    {
        var schema = await db.NewSchemaAsync();
        var redis = await redisFixture.ConnectAsync();
        await using var _ = redis;
        var redisA = await redisFixture.ConnectAsync(); // A's own connections, cut to simulate its death
        var postgresA = db.NewDataSource();
        var prefix = $"mi:{Guid.NewGuid():N}:";
        var a = await StartAsync(schema, redisA, prefix, dataSource: postgresA);
        await using var b = await StartAsync(schema, redis, prefix);

        var text = $"observe {Guid.NewGuid():N}";
        await Client(a).SendMessageAsync(Message.FromUser(text), new SendMessageConfiguration { ReturnImmediately = true });
        var taskId = await GatedAgent.Started(text).Task.WaitAsync(Timeout);
        var key = new TaskKey("", "alice", taskId);
        var coordinator = b.Services.GetRequiredService<ExecutionCoordinator>();
        Assert.True(await coordinator.IsRunningAsync(key));
        using var cts = new CancellationTokenSource(Timeout);
        var onB = Collect(Client(b), taskId, cts.Token);
        await onB.Subscribed;

        await redisA.CloseAsync(allowCommandsToComplete: false);
        await postgresA.DisposeAsync(); // a killed instance writes nothing more (no CANCELED from its shutdown)
        try { await a.DisposeAsync(); }
        catch (Exception) { /* a dying instance may fail to stop cleanly */ }
        redisA.Dispose();

        await EventuallyAsync(async () => (await coordinator.ListExpiredAsync(100)).Any(e => e.Task == key), "B lists A's execution as expired");
        Assert.False(await coordinator.IsRunningAsync(key));
        Assert.Equal(TaskState.Working, (await b.Services.GetRequiredService<TaskStore>().GetAsync(key))!.Task.Status.State);

        // B's retention sweep, run directly. It may wait one more sweep while A's last write is younger than the lease TTL.
        var sweeper = ActivatorUtilities.CreateInstance<A2ARetentionService>(b.Services, Options.Create(new A2ARetentionOptions()));
        var tasks = b.Services.GetRequiredService<TaskStore>();
        await EventuallyAsync(async () =>
        {
            await sweeper.RunOnceAsync(CancellationToken.None);
            return (await tasks.GetAsync(key))?.Task.Status.State == TaskState.Failed;
        }, "B's sweep failed the task");

        var events = await onB.Events.WaitAsync(Timeout); // the stream ends with the final event
        var final = Assert.IsType<StatusUpdateEvent>(A2A.Json.A2AJson.Deserialize<StreamResponse>(events[^1]));
        Assert.Equal(TaskState.Failed, final.StatusUpdate.Status.State);
        Assert.Equal("The agent stopped unexpectedly.", final.StatusUpdate.Status.Message!.GetText());
        var row = await tasks.GetAsync(key); // PostgreSQL, through B's task store
        Assert.IsType<PostgresTaskStore>(tasks);
        Assert.Equal(TaskState.Failed, row!.Task.Status.State);
        Assert.Equal("The agent stopped unexpectedly.", row.Task.Status.Message!.GetText());
        Assert.DoesNotContain(await coordinator.ListExpiredAsync(100), e => e.Task == key); // the swept lease is gone
    }

    /// <summary>
    /// The heartbeat renews the idempotency claim of a running request: a direct reply that takes longer than the claim
    /// timeout keeps its key, so a retry on another instance waits for that reply instead of running the agent again.
    /// </summary>
    [SkippableFact]
    public async Task Long_direct_reply_keeps_its_idempotency_claim()
    {
        var schema = await db.NewSchemaAsync();
        var redis = await redisFixture.ConnectAsync();
        await using var _ = redis;
        var prefix = $"mi:{Guid.NewGuid():N}:";
        var claimTimeout = TimeSpan.FromSeconds(1);
        void Store(PostgresStoreOptions o)
        {
            o.IdempotencyClaimTimeout = claimTimeout;
            o.IdempotencyPollInterval = TimeSpan.FromMilliseconds(50);
        }
        await using var a = await StartAsync(schema, redis, prefix, store: Store);
        await using var b = await StartAsync(schema, redis, prefix, store: Store);

        var text = $"reply {Guid.NewGuid():N}";
        var message = Message.FromUser(text);
        var first = Client(a).SendMessageAsync(message);
        var taskId = await GatedAgent.Started(text).Task.WaitAsync(Timeout);
        await Task.Delay(claimTimeout * 2.5); // without heartbeats the claim would now count as abandoned
        // Past its TTL too: only the heartbeat keeps the execution's lease alive, seen from the other instance.
        Assert.True(await b.Services.GetRequiredService<ExecutionCoordinator>().IsRunningAsync(new TaskKey("", "alice", taskId)));

        var retry = Client(b).SendMessageAsync(message);
        await Task.WhenAny(retry, Task.Delay(TimeSpan.FromMilliseconds(500)));
        GatedAgent.Gate(text).TrySetResult();

        Assert.Equal("echo", Assert.IsType<MessageResponse>(await first.WaitAsync(Timeout)).Message.GetText());
        Assert.Equal("echo", Assert.IsType<MessageResponse>(await retry.WaitAsync(Timeout)).Message.GetText());
        Assert.Equal(1, GatedAgent.Runs(text));
    }

    /// <summary>
    /// Review ruling R2: an instance ignores the echo of its own cancel request. A follow-up started on A right after A's
    /// CancelTask is not cancelled by it, and a CancelTask from B still stops that follow-up on A.
    /// </summary>
    [SkippableFact]
    public async Task Own_cancel_echo_does_not_cancel_a_later_follow_up_but_another_instance_still_can()
    {
        var schema = await db.NewSchemaAsync();
        var redis = await redisFixture.ConnectAsync();
        await using var _ = redis;
        var prefix = $"mi:{Guid.NewGuid():N}:";
        await using var a = await StartAsync(schema, redis, prefix, services: s => s.Replace(ServiceDescriptor.Singleton<ExecutionCoordinator>(
            sp => new HoldingCoordinator(sp.GetRequiredService<RedisExecutionCoordinator>()))));
        await using var b = await StartAsync(schema, redis, prefix);
        var holding = Assert.IsType<HoldingCoordinator>(a.Services.GetRequiredService<ExecutionCoordinator>());

        var text = $"refuse {Guid.NewGuid():N}";
        var task = Assert.IsType<TaskResponse>(await Client(a).SendMessageAsync(Message.FromUser(text))).Task;
        Assert.Equal(TaskState.InputRequired, task.Status.State);
        var key = new TaskKey("", "alice", task.Id);

        // The agent refuses to cancel, so the task stays open for a follow-up; A has sent its cancel request.
        var refused = await Assert.ThrowsAsync<A2AException>(() => Client(a).CancelTaskAsync(task.Id));
        Assert.Equal(A2AErrorCode.TaskNotCancelable, refused.Code);

        var more = text + " more";
        var followUp = new Message { MessageId = Message.NewId(), Role = Role.User, TaskId = task.Id, Parts = [Part.FromText(more)] };
        await Client(a).SendMessageAsync(followUp, new SendMessageConfiguration { ReturnImmediately = true });
        await GatedAgent.Started(more).Task.WaitAsync(Timeout);

        // A request B sends now reaches A after A's own one would have: once A has B's, A's echo is not coming.
        var marker = new TaskKey("", "alice", "marker-" + Guid.NewGuid().ToString("N"));
        await b.Services.GetRequiredService<ExecutionCoordinator>().RequestCancelAsync(marker);
        await EventuallyAsync(() => Task.FromResult(holding.Seen.Contains(marker)), "A received B's cancel request");
        Assert.DoesNotContain(key, holding.Seen);

        holding.Release();
        Assert.False(GatedAgent.Cancelled(more).Task.IsCompleted);

        var canceled = await Client(b).CancelTaskAsync(task.Id);
        await GatedAgent.Cancelled(more).Task.WaitAsync(Timeout);
        Assert.Equal(TaskState.Canceled, canceled.Status.State);
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting until {what}.");
            await Task.Delay(50);
        }
    }

    /// <summary>Subscribes to a task and collects its events (as JSON) until the stream ends.</summary>
    private static (Task Subscribed, Task<List<string>> Events) Collect(A2AClient client, string taskId, CancellationToken ct)
    {
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = Task.Run(async () =>
        {
            var list = new List<string>();
            await foreach (var e in client.SubscribeToTaskAsync(taskId, cancellationToken: ct))
            {
                list.Add(A2A.Json.A2AJson.Serialize(e));
                subscribed.TrySetResult(); // the first event (the snapshot) comes after the subscription is live
            }
            return list;
        }, ct);
        return (subscribed.Task, events);
    }

    private async Task<WebApplication> StartAsync(PostgresStoreOptions schema, IConnectionMultiplexer redis, string prefix,
        Action<PostgresStoreOptions>? store = null, Action<IServiceCollection>? services = null, Npgsql.NpgsqlDataSource? dataSource = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(HeaderAuth.SchemeName).AddScheme<AuthenticationSchemeOptions, HeaderAuth>(HeaderAuth.SchemeName, null);
        builder.Services.AddAuthorization();
        builder.Services.AddA2AServer(o =>
            {
                o.AgentCard = Card();
                // Short leases, so a dead instance shows quickly; frequent checks, so waits across instances end quickly.
                o.Coordination.LeaseTtl = TimeSpan.FromSeconds(2);
                o.Coordination.Heartbeat = TimeSpan.FromMilliseconds(250);
                o.Coordination.RunningPollInterval = TimeSpan.FromMilliseconds(100);
            })
            .AddAgentExecutor<GatedAgent>()
            .AddPostgresStores(dataSource ?? db.DataSource, o =>
            {
                o.Schema = schema.Schema;
                o.CreateSchemaOnStartup = false;
                store?.Invoke(o);
            })
            .AddRedisEventBus(redis, o => o.ChannelPrefix = prefix);
        builder.Services.AddA2AServerAspNetCore();
        services?.Invoke(builder.Services);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseA2A();
        app.UseAuthorization();
        app.MapA2A("/a2a").RequireAuthorization();
        await app.StartAsync();
        return app;
    }

    private static A2AClient Client(WebApplication app)
    {
        var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Test-User", "alice");
        var options = new A2AClientOptions();
        return new A2AClient(new JsonRpcTransport(http, new Uri(BaseUrl), options), options, Card());
    }

    private static AgentCard Card() => new()
    {
        Name = "Gated Agent",
        Description = "Test agent",
        Version = "1.0.0",
        SupportedInterfaces = [new AgentInterface { Url = BaseUrl, ProtocolBinding = ProtocolBindings.JsonRpc }],
        Capabilities = new AgentCapabilities { Streaming = true },
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "gate", Name = "Gate", Description = "Waits for the test", Tags = ["test"] }],
    };

    /// <summary>
    /// The first word of the message picks the behaviour; the whole text keys the signals.
    /// <list type="bullet">
    /// <item><c>observe</c>: starts work, then waits until cancelled and reports it.</item>
    /// <item><c>interleave</c>: starts work and asks for input, then keeps running: once the follow-up arrives (on any
    /// instance), the first execution adds artifacts back to back; as soon as its first one is stored, the follow-up adds
    /// its own back to back too, then completes the task.</item>
    /// <item><c>reply</c>: waits for its gate, then replies directly ("echo") without a task.</item>
    /// <item><c>refuse</c>: asks for input and returns; refuses CancelTask. A follow-up starts work and waits until cancelled.</item>
    /// <item>anything else: starts work, waits for its gate, then completes.</item>
    /// </list>
    /// </summary>
    private sealed class GatedAgent(TaskStore tasks) : IAgentExecutor
    {
        private static readonly ConcurrentDictionary<string, TaskCompletionSource> Gates = new();
        private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> StartedSignals = new();
        private static readonly ConcurrentDictionary<string, TaskCompletionSource> CancelledSignals = new();
        private static readonly ConcurrentDictionary<string, int> RunCounts = new();

        public static TaskCompletionSource Gate(string text) =>
            Gates.GetOrAdd(text, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        /// <summary>Completes with the task id once the agent for <paramref name="text"/> has started.</summary>
        public static TaskCompletionSource<string> Started(string text) =>
            StartedSignals.GetOrAdd(text, _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));

        /// <summary>Completes once the agent for <paramref name="text"/> saw its cancellation token fire.</summary>
        public static TaskCompletionSource Cancelled(string text) =>
            CancelledSignals.GetOrAdd(text, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        /// <summary>How many executions started for <paramref name="text"/>.</summary>
        public static int Runs(string text) => RunCounts.GetValueOrDefault(text);

        /// <summary>Ten artifacts, back to back: on two instances the writers race for every task version.</summary>
        private static async Task AddArtifactsAsync(TaskUpdater updater, string name, CancellationToken ct)
        {
            for (var i = 0; i < 10; i++) await updater.AddArtifactAsync([Part.FromText($"{name} {i}")], cancellationToken: ct);
        }

        /// <summary>Cancels, except a <c>refuse</c> task, which stays as it is.</summary>
        public Task CancelAsync(AgentTask task, TaskUpdater updater, CancellationToken ct) =>
            task.History?.FirstOrDefault()?.GetText().StartsWith("refuse ", StringComparison.Ordinal) == true
                ? Task.CompletedTask
                : updater.CancelAsync(cancellationToken: ct).AsTask();

        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            var text = context.UserText;
            RunCounts.AddOrUpdate(text, 1, (_, n) => n + 1);
            switch (text.Split(' ')[0])
            {
                case "observe":
                    await updater.StartWorkAsync("working", ct);
                    Started(text).TrySetResult(updater.TaskId);
                    try
                    {
                        await Task.Delay(System.Threading.Timeout.Infinite, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        Cancelled(text).TrySetResult();
                        throw;
                    }
                    break;
                case "interleave" when context.Task is null:
                    await updater.StartWorkAsync("working", ct);
                    await updater.RequireInputAsync("more?", ct);
                    Started(text).TrySetResult(updater.TaskId);
                    await Gate(text).Task.WaitAsync(ct);
                    await AddArtifactsAsync(updater, "first", ct);
                    break;
                case "interleave":
                    var original = text[..^" more".Length];
                    await updater.StartWorkAsync("continuing", ct);
                    Gate(original).TrySetResult();
                    // Once the first execution's writes are landing, both write back to back and race for every version.
                    var key = new TaskKey(context.Call.Tenant, context.Call.Principal, context.TaskId);
                    while ((await tasks.GetAsync(key, ct))?.Task.Artifacts?.Any(a => a.Parts.Any(p => p is TextPart { Text: "first 0" })) != true)
                        await Task.Delay(5, ct);
                    await AddArtifactsAsync(updater, "second", ct);
                    await updater.CompleteAsync("done", ct);
                    break;
                case "refuse" when context.Task is null:
                    await updater.RequireInputAsync("more?", ct);
                    break;
                case "refuse":
                    await updater.StartWorkAsync("working", ct);
                    Started(text).TrySetResult(updater.TaskId);
                    try
                    {
                        await Task.Delay(System.Threading.Timeout.Infinite, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        Cancelled(text).TrySetResult();
                        throw;
                    }
                    break;
                case "reply":
                    Started(text).TrySetResult(updater.TaskId);
                    await Gate(text).Task.WaitAsync(ct);
                    await updater.ReplyAsync("echo", ct);
                    break;
                default:
                    await updater.StartWorkAsync("working", ct);
                    Started(text).TrySetResult(updater.TaskId);
                    await Gate(context.UserText).Task.WaitAsync(ct);
                    await updater.CompleteAsync("done", ct);
                    break;
            }
        }
    }

    /// <summary>
    /// Wraps a coordinator and holds the cancel requests it reads until <see cref="Release"/>, recording each one on arrival.
    /// </summary>
    private sealed class HoldingCoordinator(ExecutionCoordinator inner) : ExecutionCoordinator
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<TaskKey> _seen = new();

        public IReadOnlyCollection<TaskKey> Seen => _seen;

        public void Release() => _release.TrySetResult();

        public override async IAsyncEnumerable<TaskKey> CancelRequestsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var requests = inner.CancelRequestsAsync(ct).GetAsyncEnumerator(ct);
            var next = requests.MoveNextAsync(); // registers the inner reader now
            var held = Channel.CreateUnbounded<TaskKey>();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await next)
                    {
                        _seen.Enqueue(requests.Current);
                        held.Writer.TryWrite(requests.Current);
                        next = requests.MoveNextAsync();
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    held.Writer.TryComplete();
                    await requests.DisposeAsync();
                }
            }, CancellationToken.None);
            await _release.Task.WaitAsync(ct);
            await foreach (var task in held.Reader.ReadAllAsync(ct)) yield return task;
        }

        public override ValueTask RegisterAsync(TaskKey task, string executionId, CancellationToken ct = default) => inner.RegisterAsync(task, executionId, ct);
        public override ValueTask<bool> HeartbeatAsync(TaskKey task, string executionId, CancellationToken ct = default) => inner.HeartbeatAsync(task, executionId, ct);
        public override ValueTask UnregisterAsync(TaskKey task, string executionId, CancellationToken ct = default) => inner.UnregisterAsync(task, executionId, ct);
        public override ValueTask<bool> IsRunningAsync(TaskKey task, CancellationToken ct = default) => inner.IsRunningAsync(task, ct);
        public override ValueTask RequestCancelAsync(TaskKey task, CancellationToken ct = default) => inner.RequestCancelAsync(task, ct);
        public override ValueTask<IReadOnlyList<(TaskKey Task, string ExecutionId)>> ListExpiredAsync(int max, CancellationToken ct = default) => inner.ListExpiredAsync(max, ct);
        public override ValueTask<bool> TryAcquireSweepLeaseAsync(string name, TimeSpan duration, CancellationToken ct = default) => inner.TryAcquireSweepLeaseAsync(name, duration, ct);
    }

    private sealed class HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers["X-Test-User"].ToString();
            if (string.IsNullOrEmpty(user)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("sub", user), new Claim(ClaimTypes.Name, user)], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
