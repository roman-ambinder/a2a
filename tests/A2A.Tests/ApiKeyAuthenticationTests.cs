using System.Net;
using System.Text;
using A2A.Client;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Tests;

public class A2AApiKeyTests
{
    [Fact]
    public void Created_keys_are_well_formed_and_distinct()
    {
        var a = A2AApiKey.Create();
        var b = A2AApiKey.Create();
        Assert.True(A2AApiKey.IsWellFormed(a.Key));
        Assert.StartsWith("a2a_", a.Key, StringComparison.Ordinal);
        Assert.Equal(47, a.Key.Length);
        Assert.NotEqual(a.Key, b.Key);
        Assert.True(A2AApiKey.IsWellFormedHash(a.Hash));
        Assert.Equal(a.Hash, A2AApiKey.ComputeHash(a.Key));
        Assert.Equal(a.Hash, a.ToString()); // never the key
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("secret123")]
    [InlineData("a2a_short")]
    [InlineData("a2a_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")] // not base64url
    [InlineData("a2a_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")] // non-zero padding bits: a second spelling of a key
    [InlineData("xyz_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_keys_are_refused(string? key)
    {
        Assert.False(A2AApiKey.IsWellFormed(key));
        if (key is not null) Assert.Throws<ArgumentException>(() => A2AApiKey.ComputeHash(key));
    }

    [Fact]
    public void Hash_is_sha256_of_the_key() =>
        Assert.Equal("sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                Encoding.ASCII.GetBytes("a2a_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"))),
            A2AApiKey.ComputeHash("a2a_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));

    [Theory]
    [InlineData("sha256:abc")]
    [InlineData("SHA256:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("sha256:000000000000000000000000000000000000000000000000000000000000000G")]
    [InlineData("sha256:00000000000000000000000000000000000000000000000000000000000000AA")]
    public void Malformed_hashes_are_refused(string hash) => Assert.False(A2AApiKey.IsWellFormedHash(hash));
}

public class ApiKeyAuthenticationTests
{
    private static readonly A2AApiKey Billing = A2AApiKey.Create();
    private static readonly A2AApiKey Billing2 = A2AApiKey.Create(); // the same caller, a rotated key
    private static readonly A2AApiKey Reports = A2AApiKey.Create();
    private static readonly A2AApiKey AcmeOnly = A2AApiKey.Create();

    private static AgentCard Card() => AgentCard.Create("Keyed", "Needs a key");

    private static async Task<WebApplication> StartAsync(Action<A2AApiKeyOptions>? keys = null,
        Action<WebApplication>? map = null, Action<IServiceCollection>? services = null, IConfiguration? configuration = null,
        Action<A2AServerBuilder>? server = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        services?.Invoke(builder.Services);
        void Configure(A2AServerBuilder a)
        {
            a.AddAgentExecutor<ScenarioAgent>().AddAgentCard(Card())
                .AddApiKeyAuthentication(keys ?? (o => o
                    .Add("billing", Billing.Hash).Add("billing", Billing2.Hash)
                    .Add("reports", Reports.Hash)
                    .Add("acme-client", AcmeOnly.Hash, "acme")));
            server?.Invoke(a);
        }
        if (configuration is null) builder.Services.AddA2A(Configure);
        else builder.Services.AddA2A(configuration, Configure);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        if (map is null) app.MapA2A("/a2a"); // no RequireAuthorization: the API key protects it
        else map(app);
        await app.StartAsync();
        return app;
    }

    private static HttpClient Http(WebApplication app, string? key)
    {
        var http = app.GetTestClient();
        if (key is not null) http.DefaultRequestHeaders.Add("X-Api-Key", key);
        return http;
    }

    private static A2AClient Client(WebApplication app, string? key, string path = "/a2a") =>
        A2AClientFactory.CreateForEndpoint(new Uri("http://localhost" + path), Http(app, key));

    private static async Task<HttpStatusCode> RawSend(HttpClient http)
    {
        using var content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{"message":{"messageId":"m","role":"ROLE_USER","parts":[{"text":"hello"}]}}}""",
            Encoding.UTF8, "application/json");
        content.Headers.Add("A2A-Version", "1.0");
        using var response = await http.PostAsync("/a2a", content);
        return response.StatusCode;
    }

    [Fact]
    public async Task A_known_key_gets_in_without_RequireAuthorization()
    {
        await using var app = await StartAsync();
        Assert.Equal("hi there", await Client(app, Billing.Key).AskAsync("hello"));
        var chunks = new List<string>();
        await foreach (var c in Client(app, Billing.Key).StreamTextAsync("task")) chunks.Add(c); // SSE too
        Assert.Equal(["part1", "part2"], chunks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("secret123")]
    [InlineData("a2a_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // well formed, not registered
    public async Task Missing_malformed_or_unknown_keys_are_401(string? key)
    {
        await using var app = await StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await RawSend(Http(app, key)));
    }

    [Fact]
    public async Task Two_keys_in_one_request_are_401()
    {
        await using var app = await StartAsync();
        var http = app.GetTestClient();
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", [Billing.Key, Reports.Key]);
        Assert.Equal(HttpStatusCode.Unauthorized, await RawSend(http));
    }

    [Fact]
    public async Task A_key_in_the_query_string_is_not_read()
    {
        await using var app = await StartAsync();
        var http = app.GetTestClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await http.PostAsync($"/a2a?X-Api-Key={Billing.Key}&api_key={Billing.Key}", content);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Each_caller_owns_its_tasks_and_rotated_keys_share_the_owner()
    {
        await using var app = await StartAsync();
        var task = (await Client(app, Billing.Key).SendMessageAsync("task")).RequireTask();
        Assert.Equal(task.Id, (await Client(app, Billing2.Key).GetTaskAsync(task.Id)).Id); // same caller, other key
        var ex = await Assert.ThrowsAsync<A2AException>(() => Client(app, Reports.Key).GetTaskAsync(task.Id));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
    }

    [Fact]
    public async Task Owner_id_is_prefixed_so_other_schemes_cannot_collide()
    {
        var owner = new TaskCompletionSource<string>();
        await using var app = await StartAsync(services: s => s.AddSingleton<IPrincipalResolver>(new Spy(owner)));
        await Client(app, Billing.Key).AskAsync("hello");
        Assert.Equal("apikey:billing", await owner.Task);
    }

    [Fact]
    public async Task Another_scheme_cannot_claim_an_api_key_owner()
    {
        // A token whose sub is literally "apikey:billing" must not become the owner of the API-key caller billing.
        await using var app = await StartAsync(
            services: s => s.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null),
            map: a => a.MapA2A("/a2a").RequireAuthorization(p => p
                .AddAuthenticationSchemes(A2AApiKeyOptions.SchemeName, TestAuthHandler.SchemeName).RequireAuthenticatedUser()));
        var task = (await Client(app, Billing.Key).SendMessageAsync("task")).RequireTask();

        var impostor = app.GetTestClient();
        impostor.DefaultRequestHeaders.Add("X-Test-User", "apikey:billing");
        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            A2AClientFactory.CreateForEndpoint(new Uri("http://localhost/a2a"), impostor).GetTaskAsync(task.Id));
        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);

        // Any other id from that scheme still works, and the API-key caller still owns its task.
        var other = app.GetTestClient();
        other.DefaultRequestHeaders.Add("X-Test-User", "billing");
        Assert.Equal("hi there", await A2AClientFactory.CreateForEndpoint(new Uri("http://localhost/a2a"), other).AskAsync("hello"));
        Assert.Equal(task.Id, (await Client(app, Billing.Key).GetTaskAsync(task.Id)).Id);
    }

    private sealed class Spy(TaskCompletionSource<string> seen) : IPrincipalResolver
    {
        public string? Resolve(System.Security.Claims.ClaimsPrincipal user)
        {
            var id = new DefaultPrincipalResolver().Resolve(user);
            if (id is not null) seen.TrySetResult(id);
            return id;
        }
    }

    [Fact]
    public async Task A_key_limited_to_a_tenant_is_refused_elsewhere_and_audited()
    {
        var audit = new ListSink();
        await using var app = await StartAsync(server: a => a.AddAuditSink(audit));
        var acme = Client(app, AcmeOnly.Key, "/a2a/acme");
        Assert.Equal("hi there", await acme.AskAsync("hello"));

        // A call that names no task: PermissionDenied, saying the tenant is what is missing.
        var ex = await Assert.ThrowsAsync<A2AException>(() => Client(app, AcmeOnly.Key, "/a2a/globex").AskAsync("hello"));
        Assert.Equal(A2AErrorCode.PermissionDenied, ex.Code);
        Assert.Equal("tenant", ex.Metadata!["permission"]);
        ex = await Assert.ThrowsAsync<A2AException>(() => Client(app, AcmeOnly.Key).AskAsync("hello")); // default tenant ""
        Assert.Equal(A2AErrorCode.PermissionDenied, ex.Code);

        // A call that names a task looks like a missing task, as for every denial.
        var task = (await acme.SendMessageAsync("task")).RequireTask();
        ex = await Assert.ThrowsAsync<A2AException>(() => Client(app, AcmeOnly.Key, "/a2a/globex").GetTaskAsync(task.Id));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);

        Assert.Equal("hi there", await Client(app, Billing.Key, "/a2a/globex").AskAsync("hello")); // an unlimited key

        // Every refusal is in the audit trail, with its true reason.
        await WaitFor(() => audit.Events.Count(e => e.Reason == "TenantNotAllowed") >= 3);
        var denied = audit.Events.Where(e => e.Reason == "TenantNotAllowed").ToList();
        Assert.All(denied, e => Assert.Equal(("apikey:acme-client", A2A.Server.Audit.A2AAuditOutcome.Denied), (e.Actor, e.Outcome)));
        Assert.Contains(denied, e => e.Tenant == "globex" && e.Operation == "GetTask" && e.TaskId == task.Id);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    private sealed class ListSink : A2A.Server.Audit.A2AAuditSink
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<A2A.Server.Audit.A2AAuditEvent> _events = new();
        public IReadOnlyCollection<A2A.Server.Audit.A2AAuditEvent> Events => _events;

        public override ValueTask WriteAsync(A2A.Server.Audit.A2AAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            _events.Enqueue(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task The_card_declares_the_header_and_the_sdk_client_sends_the_key()
    {
        await using var app = await StartAsync(o => o.Add("billing", Billing.Hash),
            services: s => s.ConfigureA2AHttp(h => h.PublicBaseUrl = new Uri("http://localhost")));
        var card = app.Services.GetRequiredService<IAgentCardProvider>().Get();
        var scheme = Assert.Single(card.SecuritySchemes!);
        Assert.Equal("apiKey", scheme.Key);
        Assert.Equal("X-Api-Key", Assert.IsType<ApiKeyScheme>(scheme.Value).Value.Name);
        Assert.Contains(card.SecurityRequirements!, r => r.Schemes.ContainsKey("apiKey"));

        // The client reads the header from the card and sends the key there.
        var client = await A2AClientFactory.CreateAsync(new Uri("http://localhost/"),
            [new A2A.Client.Credentials.ApiKeyCredentialProvider(Billing.Key)], app.GetTestServer().CreateHandler());
        Assert.Equal("hi there", await client.AskAsync("hello"));
    }

    [Fact]
    public async Task ConnectAsync_sends_the_key_the_card_asks_for()
    {
        await using var app = await StartAsync(o => o.Add("billing", Billing.Hash),
            services: s => s.ConfigureA2AHttp(h => h.PublicBaseUrl = new Uri("http://localhost")));
        var agent = await A2A.Client.A2AClient.ConnectAsync(new Uri("http://localhost/"), o =>
        {
            o.Handler = app.GetTestServer().CreateHandler();
            o.Credentials.Add(new A2A.Client.Credentials.ApiKeyCredentialProvider(Billing.Key));
        });
        Assert.Equal("hi there", await agent.AskAsync("hello"));

        var withoutKey = await A2A.Client.A2AClient.ConnectAsync(new Uri("http://localhost/"),
            o => o.Handler = app.GetTestServer().CreateHandler());
        await Assert.ThrowsAsync<A2AException>(() => withoutKey.AskAsync("hello"));
    }

    private static List<string?> A2APolicies(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints)
        .Where(e => e.DisplayName?.StartsWith("A2A ", StringComparison.Ordinal) == true)
        .SelectMany(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy)).Distinct().ToList();

    [Fact]
    public async Task Endpoints_with_their_own_authorization_keep_it()
    {
        await using var app = await StartAsync(map: a => a.MapA2A("/a2a").RequireAuthorization("custom"),
            services: s => s.AddAuthorization(o => o.AddPolicy("custom", p => p.RequireAuthenticatedUser())));
        Assert.Equal(["custom"], A2APolicies(app));
    }

    [Fact]
    public async Task Authorization_on_an_outer_group_is_respected()
    {
        await using var app = await StartAsync(map: a => a.MapGroup("/api").RequireAuthorization("custom").MapA2A("/a2a"),
            services: s => s.AddAuthorization(o => o.AddPolicy("custom", p => p.RequireAuthenticatedUser())));
        Assert.Equal(["custom"], A2APolicies(app));
    }

    [Fact]
    public async Task Endpoints_without_authorization_get_the_api_key_policy()
    {
        await using var app = await StartAsync();
        Assert.Equal([A2ACardAuthenticationExtensions.DefaultPolicyName], A2APolicies(app));
    }

    [Fact]
    public async Task The_artifact_download_route_gets_the_api_key_policy_too()
    {
        await using var app = await StartAsync(map: a =>
        {
            a.MapA2A("/a2a");
            a.MapA2AArtifacts("/a2a");
        });
        var artifactRoutes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.Contains("artifacts", StringComparison.Ordinal) == true).ToList();
        Assert.NotEmpty(artifactRoutes);
        Assert.All(artifactRoutes, e => Assert.Contains(e.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            a => a.Policy == A2ACardAuthenticationExtensions.DefaultPolicyName));
    }

    [Fact]
    public async Task A_custom_store_is_used()
    {
        await using var app = await StartAsync(_ => { },
            services: s => s.AddSingleton<IApiKeyStore>(new OneKeyStore(Reports.Hash, "from-store")));
        Assert.Equal("hi there", await Client(app, Reports.Key).AskAsync("hello"));
        Assert.Equal(HttpStatusCode.Unauthorized, await RawSend(Http(app, Billing.Key)));
    }

    private sealed class OneKeyStore(string hash, string caller) : IApiKeyStore
    {
        public ValueTask<ApiKeyCaller?> FindAsync(string keyHash, CancellationToken cancellationToken) =>
            new(keyHash == hash ? new ApiKeyCaller(caller) : null);
    }

    [Fact]
    public async Task A_key_where_a_hash_belongs_fails_at_startup()
    {
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => StartAsync(o => o.Add("billing", Billing.Key)));
        Assert.Contains("holds a key, not a hash", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_hash_twice_fails_at_startup()
    {
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            StartAsync(o => o.Add("a", Billing.Hash).Add("b", Billing.Hash)));
        Assert.Contains("same hash", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keys_come_from_configuration_and_follow_reloads()
    {
        var root = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["A2A:ApiKeys:Keys:0:CallerId"] = "billing", ["A2A:ApiKeys:Keys:0:Hash"] = Billing.Hash
        }).Build();
        await using var app = await StartAsync(_ => { }, configuration: root.GetSection("A2A"));
        Assert.Equal("hi there", await Client(app, Billing.Key).AskAsync("hello"));
        Assert.Equal(HttpStatusCode.Unauthorized, await RawSend(Http(app, Reports.Key)));

        // Rotate: replace the billing key with the reports key, without a restart.
        root["A2A:ApiKeys:Keys:0:Hash"] = Reports.Hash;
        root.Reload();
        Assert.Equal("hi there", await Client(app, Reports.Key).AskAsync("hello"));
        Assert.Equal(HttpStatusCode.Unauthorized, await RawSend(Http(app, Billing.Key)));
    }

    [Fact]
    public async Task A_broken_reload_keeps_the_old_keys()
    {
        var root = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["A2A:ApiKeys:Keys:0:CallerId"] = "billing", ["A2A:ApiKeys:Keys:0:Hash"] = Billing.Hash
        }).Build();
        await using var app = await StartAsync(_ => { }, configuration: root.GetSection("A2A"));
        root["A2A:ApiKeys:Keys:0:Hash"] = "not a hash";
        root.Reload();
        Assert.Equal("hi there", await Client(app, Billing.Key).AskAsync("hello"));
    }

    [Fact]
    public async Task A_plain_key_in_configuration_is_refused()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["A2A:ApiKeys:Keys:0:CallerId"] = "billing", ["A2A:ApiKeys:Keys:0:Key"] = Billing.Key
        }).Build().GetSection("A2A");
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => StartAsync(_ => { }, configuration: config));
        Assert.Contains("A2A:ApiKeys:Keys:0:Key", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Billing.Key, ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>The tenant claim is not tied to API keys: any scheme that sets it limits the caller the same way.</summary>
public class TenantClaimTests
{
    private static ServerCallContext Call(string tenant, params string[] allowed) => new()
    {
        Principal = "p", Tenant = tenant,
        User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            allowed.Select(t => new System.Security.Claims.Claim(A2AClaimTypes.Tenant, t)), "Bearer"))
    };

    [Theory]
    [InlineData("acme", new string[0], true)]          // no claim: every tenant
    [InlineData("acme", new[] { "acme" }, true)]
    [InlineData("acme", new[] { "globex", "acme" }, true)]
    [InlineData("globex", new[] { "acme" }, false)]
    [InlineData("", new[] { "acme" }, false)]           // the default tenant is a tenant too
    [InlineData("ACME", new[] { "acme" }, false)]       // ordinal, like tenants everywhere else
    public void Claim_limits_the_tenants(string tenant, string[] allowed, bool expected) =>
        Assert.Equal(expected, A2A.Server.Authorization.OperationAuthorization.TenantAllowed(Call(tenant, allowed)));
}
