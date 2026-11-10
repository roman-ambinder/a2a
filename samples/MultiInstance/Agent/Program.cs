using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using A2A;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Postgres;
using A2A.Server.Redis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

// One agent app, started twice (see docker-compose.yml). Both instances share Postgres (tasks, events, outbox, idempotency)
// and Redis (live events, cancel requests, "is this task running" leases), so a task started on one instance can be read,
// followed and cancelled through the other.
var builder = WebApplication.CreateBuilder(args);
var publicUrl = builder.Configuration["PublicUrl"] ?? "http://localhost:8080";
var instance = builder.Configuration["Instance"] ?? Environment.MachineName;
var apiKey = builder.Configuration["ApiKey"];
if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException("Set ApiKey (callers send it in the X-Api-Key header).");
var postgres = builder.Configuration.GetConnectionString("a2a") ??
               throw new InvalidOperationException("Set ConnectionStrings:a2a.");
var redis = builder.Configuration.GetConnectionString("redis") ??
            throw new InvalidOperationException("Set ConnectionStrings:redis.");

builder.Services.AddAuthentication(ApiKeyAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>(ApiKeyAuthHandler.SchemeName, null);
builder.Services.AddAuthorization();
builder.Services.AddSingleton(new ApiKey(apiKey));
builder.Services.AddSingleton(new InstanceName(instance));

builder.Services.AddA2AServer(o =>
    {
        o.AgentCard = new AgentCard
        {
            Name = "Multi-instance agent",
            Description = "A slow task (30 one-second steps) that any instance of the deployment can follow or cancel.",
            Version = "1.0.0",
            SupportedInterfaces =
            [
                new AgentInterface { Url = publicUrl, ProtocolBinding = ProtocolBindings.JsonRpc },
                new AgentInterface { Url = publicUrl, ProtocolBinding = ProtocolBindings.HttpJson }
            ],
            Capabilities = new AgentCapabilities { Streaming = true },
            DefaultInputModes = ["text/plain"],
            DefaultOutputModes = ["text/plain"],
            Skills = [new AgentSkill { Id = "slow", Name = "Slow task", Description = "Counts to 30", Tags = ["demo"] }]
        };
    }).AddAgentExecutor<SlowAgent>()
    .AddPostgresStores(postgres) // tasks, event log, push configs, push outbox, idempotency
    .AddRedisEventBus(redis); // live events and cancel requests across instances (also the execution coordinator)
builder.Services
    .AddA2ARetention(); // opt-in: purges, interrupted-state timeout and recovery of tasks whose instance died
builder.Services.AddA2AServerAspNetCore();

var app = builder.Build();
app.Use(async (http, next) =>
{
    // Lets the client see which instance answered.
    http.Response.Headers["X-Instance"] = instance;
    await next();
});
app.UseAuthentication();
app.UseA2A();
app.UseAuthorization();
app.MapAgentCard();
app.MapA2A().RequireAuthorization();
app.Run();

internal sealed record ApiKey(string Value);

internal sealed record InstanceName(string Value);

/// <summary>Accepts requests whose <c>X-Api-Key</c> header matches the configured key. Every caller is the same user.</summary>
internal sealed class ApiKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKey key)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var supplied = Request.Headers["X-Api-Key"].ToString();
        if (supplied.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
        var ok = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied),
            Encoding.UTF8.GetBytes(key.Value));
        if (!ok) return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "demo-client")], SchemeName);
        return Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>Counts to 30, one step per second, and reports the instance that runs it. Stops when cancelled.</summary>
internal sealed class SlowAgent(InstanceName instance) : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater,
        CancellationToken cancellationToken)
    {
        await updater.StartWorkAsync($"Running on {instance.Value}", cancellationToken);
        string? artifactId = null;
        for (var i = 1; i <= 30; i++)
        {
            await Task.Delay(1000, cancellationToken);
            artifactId = await updater.AddArtifactAsync([Part.FromText($"step {i} (on {instance.Value})\n")],
                artifactId, "steps",
                append: i > 1, lastChunk: i == 30, cancellationToken: cancellationToken);
        }

        await updater.CompleteAsync(cancellationToken: cancellationToken);
    }
}