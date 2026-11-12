using System.Collections.Concurrent;
using A2A;
using A2A.Client;
using A2A.Server;
using A2A.Server.Agents;
using A2A.Server.AspNetCore;
using A2A.Server.Audit;
using A2A.Server.Authorization;

// One host, two tenants ("sales" and "support"), each with its own Agent Card and agent. Two demo users call it:
// alice works in sales and may translate; bob works in support and may only summarize. The server checks the tenant
// and the skill of every call, and writes each refusal to the audit trail, which the sample reads back at the end.
// No real secrets, no external calls: the server listens on 127.0.0.1 only.

const string baseUrl = "http://127.0.0.1:5340"; // the cards below list this address too

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(baseUrl);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
// The refusals below are also security events (a Warning each). Hidden here to keep the output short.
builder.Logging.AddFilter("A2A.Server.Security", LogLevel.Error);

// Callers authenticate with an API key in the X-Api-Key header. The keys are made when the sample starts (see DemoUsers);
// the server keeps only their hashes. Each key may call only its own tenant: a call to another one is refused and
// audited before any rule of the host runs.

// The audit trail goes to a list in memory, so the sample can read it back. A real host uses a durable sink.
var audit = new MemoryAuditSink();

builder.Services.AddScoped<SalesAgent>();
builder.Services.AddScoped<SupportAgent>();
builder.Services.AddA2AServer(o => o.SkillAuthorization.Mode = SkillAuthorizationMode.Required)
    .AddTenantCardProvider<TenantCards>()
    .AddAgentExecutorResolver<TenantAgents>()
    .AddApiKeyAuthentication(o => o
        .Add("alice", DemoUsers.Alice.Hash, "sales")
        .Add("bob", DemoUsers.Bob.Hash, "support"))
    .AddAuthorizationHandler<TenantRules>()
    .AddAuditSink(audit);
builder.Services.AddA2AServerAspNetCore();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
#region Map
app.MapAgentCard();   // /.well-known/agent-card.json and /{tenant}/.well-known/agent-card.json
app.MapA2A("/{tenant}/a2a"); // no policy here: the API key protects the A2A endpoints
#endregion
await app.StartAsync();
Console.WriteLine($"Agent listening on {baseUrl}/{{tenant}}/a2a");

var failures = new List<string>();
var clients = new List<HttpClient>();

// 1. bob creates a task in his own tenant.
var bobInSupport = await ConnectAsync(DemoUsers.Bob.Key, "support");
var created = await SendAsync(bobInSupport, "summarize", "Summarize ticket 42.");
var bobTask = (created as TaskResponse)?.Task;
Console.WriteLine($"bob    support  summarize -> task {bobTask?.Id} {bobTask?.Status.State}");
if (bobTask?.Status.State != TaskState.Completed) failures.Add("bob's summarize did not complete a task");
var taskId = bobTask?.Id ?? "missing";

// 2. alice asks for bob's task in her own tenant. Tasks are stored by tenant and owner, so it is not there.
var aliceInSales = await ConnectAsync(DemoUsers.Alice.Key, "sales");
Expect("alice  sales    GetTask", await ErrorOfAsync(() => aliceInSales.GetTaskAsync(taskId)), A2AErrorCode.TaskNotFound);

// 3. alice asks for it in bob's tenant. Her key may only call "sales", so the server refuses. A refused task call looks
//    like a missing task, so the answer does not even confirm that the task exists.
var aliceInSupport = await ConnectAsync(DemoUsers.Alice.Key, "support");
Expect("alice  support  GetTask", await ErrorOfAsync(() => aliceInSupport.GetTaskAsync(taskId)), A2AErrorCode.TaskNotFound);

// 4. bob asks for a skill he may not use. The server refuses before the agent runs.
Expect("bob    support  translate", await ErrorOfAsync(() => SendAsync(bobInSupport, "translate", "Bonjour")),
    A2AErrorCode.PermissionDenied);

// 5. alice may translate, so the rule is not simply "deny everything".
var translated = await SendAsync(aliceInSales, "translate", "Bonjour");
var aliceTask = (translated as TaskResponse)?.Task;
Console.WriteLine($"alice  sales    translate -> {aliceTask?.Status.Message?.GetText()}");
if (aliceTask?.Status.State != TaskState.Completed) failures.Add("alice's translate did not complete");

await app.StopAsync();
foreach (var http in clients) http.Dispose();

// 6. Read the audit trail back.
Console.WriteLine();
Console.WriteLine("--- audit trail");
foreach (var e in audit.Events)
    Console.WriteLine($"{e.Action} {e.Outcome} actor={e.Actor} tenant={e.Tenant} op={e.Operation} task={e.TaskId} reason={e.Reason}");

if (!audit.Events.Any(e => e is { Action: A2AAuditAction.TaskCreated, Outcome: A2AAuditOutcome.Succeeded, Actor: "apikey:bob" }
                           && e.TaskId == taskId && e.Tenant == "support"))
    failures.Add("the audit trail has no TaskCreated row for bob's task");
if (!audit.Events.Any(e => e is { Action: A2AAuditAction.AccessDenied, Outcome: A2AAuditOutcome.Denied, Actor: "apikey:alice" }
                           && e.Tenant == "support" && e.Operation == nameof(A2AOperation.GetTask) && e.TaskId == taskId))
    failures.Add("the audit trail has no AccessDenied row for alice's GetTask in support");
if (!audit.Events.Any(e => e is { Action: A2AAuditAction.AccessDenied, Outcome: A2AAuditOutcome.Denied, Actor: "apikey:bob" }
                           && e.Tenant == "support" && e.Operation == nameof(A2AOperation.SendMessage)))
    failures.Add("the audit trail has no AccessDenied row for bob's translate");

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
    return 1;
}

Console.WriteLine("OK: tenant sales could not see a support task, an unauthorized skill was refused, and the audit trail recorded both.");
return 0;

void Expect(string what, A2AErrorCode? actual, A2AErrorCode expected)
{
    Console.WriteLine($"{what} -> {actual?.ToString() ?? "allowed"}");
    if (actual != expected) failures.Add($"{what}: expected {expected}, got {actual?.ToString() ?? "no error"}");
}

// A client for one tenant: the tenant is part of the URL, and the tenant's card lists its endpoint.
// Pass the full card URL: for a base URL the resolver appends /.well-known/agent-card.json to the origin.
async Task<A2AClient> ConnectAsync(string apiKey, string tenant)
{
    var http = new HttpClient();
    clients.Add(http);
    http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    return await A2AClientFactory.CreateAsync(new Uri($"{baseUrl}/{tenant}/.well-known/agent-card.json"), http);
}

// The skill goes in metadata["skillId"] of the message. With SkillAuthorizationMode.Required every message names one.
static Task<SendMessageResponse> SendAsync(A2AClient client, string skill, string text)
{
    return client.SendMessageAsync(Message.FromUser(text), options: new A2ARequestOptions { Skill = skill });
}

static async Task<A2AErrorCode?> ErrorOfAsync(Func<Task> call)
{
    try
    {
        await call();
        return null;
    }
    catch (A2AException ex)
    {
        return ex.Code;
    }
}

/// <summary>
///     Demo users. Their keys are made at startup, so there is no key in this file; a real host gives each caller its key
///     once (<c>a2a keys new</c>) and stores only the hash. The task owner of an API-key caller is <c>apikey:</c> and its id.
/// </summary>
internal static class DemoUsers
{
    public static readonly A2AApiKey Alice = A2AApiKey.Create();
    public static readonly A2AApiKey Bob = A2AApiKey.Create();

    /// <summary>Callers that may use the "translate" skill.</summary>
    public static readonly IReadOnlySet<string> Premium = new HashSet<string>(StringComparer.Ordinal) { "alice" };
}

/// <summary>
///     The host's own rules, which run after the tenant check of the keys. With skill authorization on, this handler
///     decides who may use which skill. Operations: the base class allows the built-in ones and denies extensions.
/// </summary>
internal sealed class TenantRules : A2AAuthorizationHandler
{

    public override ValueTask<bool> AuthorizeSkillAsync(ServerCallContext call, string skillId,
        CancellationToken cancellationToken)
    {
        var allowed = skillId switch
        {
            "summarize" => true,
            "translate" => call.User?.FindFirst(A2AClaimTypes.ApiKeyCaller)?.Value is { } caller
                           && DemoUsers.Premium.Contains(caller),
            _ => false // deny what you do not know
        };
        return ValueTask.FromResult(allowed);
    }
}

// A2ACardSecurity holds the schemes the host declares (here the API key), so each tenant's card lists them too.
public sealed class TenantCards(A2ACardSecurity security) : TenantCardProvider
{
    private readonly ConcurrentDictionary<string, AgentCard> _cards = new(StringComparer.Ordinal);

    public override ValueTask<AgentCard?> GetCardAsync(AgentRequestScope scope, CancellationToken cancellationToken)
    {
        if (scope.Tenant is not ("sales" or "support")) return new((AgentCard?)null);   // no agent for this tenant
        // The same instance on every call, so the server completes and encodes the card once.
        return new(_cards.GetOrAdd(scope.Tenant, Build));
    }

    private AgentCard Build(string tenant) => security.Apply(new AgentCard
    {
        Name = $"{tenant} agent",
        Description = $"The {tenant} agent.",
        Version = "1.0.0",
        // A card for a {tenant} route lists its own interface.
        SupportedInterfaces =
        [
            new AgentInterface { Url = $"http://127.0.0.1:5340/{tenant}/a2a", ProtocolBinding = ProtocolBindings.JsonRpc },
        ],
        Capabilities = new AgentCapabilities(),
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills =
        [
            new AgentSkill { Id = "summarize", Name = "Summarize", Description = "Summarizes a text", Tags = [tenant] },
            new AgentSkill { Id = "translate", Name = "Translate", Description = "Translates a text", Tags = [tenant] },
        ],
    });
}

public sealed class TenantAgents : AgentExecutorResolver
{
    public override IAgentExecutor? Resolve(string tenant, IServiceProvider services) => tenant switch
    {
        "sales" => services.GetRequiredService<SalesAgent>(),
        "support" => services.GetRequiredService<SupportAgent>(),
        _ => null,   // no agent: the message is refused before anything is stored
    };
}

/// <summary>The sales tenant's agent. It runs only after the server allowed the skill the message named.</summary>
public sealed class SalesAgent : IAgentExecutor
{
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
        updater.CompleteAsync($"Sales ran {context.AuthorizedSkillId}.", cancellationToken).AsTask();
}

/// <summary>The support tenant's agent.</summary>
public sealed class SupportAgent : IAgentExecutor
{
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
        updater.CompleteAsync($"Support ran {context.AuthorizedSkillId}.", cancellationToken).AsTask();
}

/// <summary>Keeps every audit event in memory. Safe for many threads, append only.</summary>
internal sealed class MemoryAuditSink : A2AAuditSink
{
    private readonly ConcurrentQueue<A2AAuditEvent> _events = new();

    public IReadOnlyCollection<A2AAuditEvent> Events => _events;

    public override ValueTask WriteAsync(A2AAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        _events.Enqueue(auditEvent);
        return ValueTask.CompletedTask;
    }
}
