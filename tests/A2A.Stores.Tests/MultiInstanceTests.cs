using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Postgres;
using A2A.Server.Redis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

    private async Task<WebApplication> StartAsync(PostgresStoreOptions schema, StackExchange.Redis.IConnectionMultiplexer redis, string prefix)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(HeaderAuth.SchemeName).AddScheme<AuthenticationSchemeOptions, HeaderAuth>(HeaderAuth.SchemeName, null);
        builder.Services.AddAuthorization();
        builder.Services.AddA2AServer(o => o.AgentCard = Card())
            .AddAgentExecutor<GatedAgent>()
            .AddPostgresStores(db.DataSource, o => { o.Schema = schema.Schema; o.CreateSchemaOnStartup = false; })
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

    /// <summary>Starts work, waits until the test opens its gate, then completes.</summary>
    private sealed class GatedAgent : IAgentExecutor
    {
        private static readonly ConcurrentDictionary<string, TaskCompletionSource> Gates = new();

        public static TaskCompletionSource Gate(string text) =>
            Gates.GetOrAdd(text, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            await updater.StartWorkAsync("working", ct);
            await Gate(context.UserText).Task.WaitAsync(ct);
            await updater.CompleteAsync("done", ct);
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
