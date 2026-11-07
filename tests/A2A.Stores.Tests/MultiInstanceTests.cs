using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Coordination;
using A2A.Server.Postgres;
using A2A.Server.Redis;
using A2A.Server.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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
    /// Review Focus 1: instance A dies mid-execution (its Redis connection drops, then the host goes away without
    /// unregistering). Its lease stops being renewed, and instance B sees it expire.
    /// </summary>
    [SkippableFact]
    public async Task Execution_of_a_dead_instance_is_reported_expired_on_another()
    {
        var schema = await db.NewSchemaAsync();
        var redis = await redisFixture.ConnectAsync();
        await using var _ = redis;
        var redisA = await redisFixture.ConnectAsync(); // A's own connection, cut to simulate its death
        var prefix = $"mi:{Guid.NewGuid():N}:";
        var a = await StartAsync(schema, redisA, prefix);
        await using var b = await StartAsync(schema, redis, prefix);

        var text = $"observe {Guid.NewGuid():N}";
        await Client(a).SendMessageAsync(Message.FromUser(text), new SendMessageConfiguration { ReturnImmediately = true });
        var taskId = await GatedAgent.Started(text).Task.WaitAsync(Timeout);
        var key = new TaskKey("", "alice", taskId);
        var coordinator = b.Services.GetRequiredService<ExecutionCoordinator>();
        Assert.True(await coordinator.IsRunningAsync(key));

        await redisA.CloseAsync(allowCommandsToComplete: false);
        try { await a.DisposeAsync(); }
        catch (Exception) { /* a dying instance may fail to stop cleanly */ }
        redisA.Dispose();

        await EventuallyAsync(async () => (await coordinator.ListExpiredAsync(100)).Any(e => e.Task == key), "B lists A's execution as expired");
        Assert.False(await coordinator.IsRunningAsync(key));
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
        await GatedAgent.Started(text).Task.WaitAsync(Timeout);
        await Task.Delay(claimTimeout * 2.5); // without heartbeats the claim would now count as abandoned

        var retry = Client(b).SendMessageAsync(message);
        await Task.WhenAny(retry, Task.Delay(TimeSpan.FromMilliseconds(500)));
        GatedAgent.Gate(text).TrySetResult();

        Assert.Equal("echo", Assert.IsType<MessageResponse>(await first.WaitAsync(Timeout)).Message.GetText());
        Assert.Equal("echo", Assert.IsType<MessageResponse>(await retry.WaitAsync(Timeout)).Message.GetText());
        Assert.Equal(1, GatedAgent.Runs(text));
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
        Action<PostgresStoreOptions>? store = null)
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
            .AddPostgresStores(db.DataSource, o =>
            {
                o.Schema = schema.Schema;
                o.CreateSchemaOnStartup = false;
                store?.Invoke(o);
            })
            .AddRedisEventBus(redis, o => o.ChannelPrefix = prefix);
        builder.Services.AddA2AServerAspNetCore();
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
    /// instance), both executions add artifacts at the same time and the follow-up completes the task.</item>
    /// <item><c>reply</c>: waits for its gate, then replies directly ("echo") without a task.</item>
    /// <item>anything else: starts work, waits for its gate, then completes.</item>
    /// </list>
    /// </summary>
    private sealed class GatedAgent : IAgentExecutor
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

        /// <summary>
        /// Ten artifacts, 20 ms apart: the two executions overlap for about 200 ms. Paced so that a writer does not lose the
        /// version race more often than the handler's conflict retries allow (back-to-back writers on two instances can).
        /// </summary>
        private static async Task AddPacedArtifactsAsync(TaskUpdater updater, string name, CancellationToken ct)
        {
            for (var i = 0; i < 10; i++)
            {
                await updater.AddArtifactAsync([Part.FromText($"{name} {i}")], cancellationToken: ct);
                await Task.Delay(20, ct);
            }
        }

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
                    await AddPacedArtifactsAsync(updater, "first", ct);
                    break;
                case "interleave":
                    var original = text[..^" more".Length];
                    await updater.StartWorkAsync("continuing", ct);
                    Gate(original).TrySetResult();
                    await AddPacedArtifactsAsync(updater, "second", ct);
                    await updater.CompleteAsync("done", ct);
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
