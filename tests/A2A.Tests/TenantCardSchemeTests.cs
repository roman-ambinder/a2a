using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Agents;
using A2A.Server.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using A2A.Client;
using A2A.Server.Grpc;
using Microsoft.AspNetCore.Builder;

namespace A2A.Tests;

/// <summary>
/// RequireAgentCardSchemes with one card per tenant: the card it checks is the card of the tenant the call runs in, also
/// when the tenant comes from the request body (JSON-RPC <c>params.tenant</c>) or from an upload route, not from the
/// <c>{tenant}</c> route value. Before, a body tenant was checked against the default tenant's card (or none). gRPC
/// carries the tenant only in the message, so a gRPC call is checked against the card of the tenant it names.
/// </summary>
public class TenantCardSchemeTests
{
    /// <summary>A scheme that never authenticates anyone: a card scheme mapped to it can never be met.</summary>
    private sealed class NobodyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Nobody";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    /// <summary>"acme" requires the "locked" scheme (never met); "open" requires nothing; the default tenant has no card.</summary>
    private sealed class Cards : TenantCardProvider
    {
        public override ValueTask<AgentCard?> GetCardAsync(AgentRequestScope scope, CancellationToken cancellationToken)
        {
            if (scope.Tenant is not ("acme" or "open")) return ValueTask.FromResult<AgentCard?>(null);
            var card = TestAgentHost.DefaultCard();
            card.Name = scope.Tenant;
            card.SecuritySchemes = new() { ["locked"] = SecurityScheme.Bearer() };
            card.SecurityRequirements = scope.Tenant == "acme"
                ? [new SecurityRequirement { Schemes = new() { ["locked"] = new StringList() } }]
                : null;
            return ValueTask.FromResult<AgentCard?>(card);
        }
    }

    private static Task<TestAgentHost> StartAsync() =>
        TestAgentHost.StartAsync(
            o =>
            {
                o.ArtifactStorage.PublicBaseUrl = new Uri(TestAgentHost.BaseUrl);
                o.ArtifactStorage.Uploads.Enabled = true;
            },
            services: s =>
            {
                s.AddA2AServer().AddTenantCardProvider<Cards>();
                s.AddSingleton<ArtifactObjectStore>(new InMemoryArtifactObjectStore());
                s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, NobodyHandler>(NobodyHandler.SchemeName, null);
            },
            configureRoutes: a2a => a2a.RequireAgentCardSchemes(new Dictionary<string, string> { ["locked"] = NobodyHandler.SchemeName }),
            configurePipeline: app => app.MapA2AUploads("/a2a")
                .RequireAgentCardSchemes(new Dictionary<string, string> { ["locked"] = NobodyHandler.SchemeName }));

    private static async Task<(HttpStatusCode Status, string Body)> ListTasksAsync(TestAgentHost host, string path, string? bodyTenant)
    {
        using var http = host.Http("alice");
        var tenant = bodyTenant is null ? "" : $",\"tenant\":\"{bodyTenant}\"";
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent($$$"""{"jsonrpc":"2.0","id":1,"method":"ListTasks","params":{"pageSize":10{{{tenant}}}}}""",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        using var resp = await http.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    private static bool Succeeded((HttpStatusCode Status, string Body) r) =>
        r.Status == HttpStatusCode.OK && r.Body.Contains("\"result\"", StringComparison.Ordinal);

    [Fact]
    public async Task A_route_tenant_whose_card_scheme_is_not_met_is_refused()
    {
        await using var host = await StartAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await ListTasksAsync(host, "/a2a/acme", null)).Status);
    }

    [Fact]
    public async Task A_body_tenant_is_not_checked_against_the_default_tenants_card()
    {
        await using var host = await StartAsync();

        var r = await ListTasksAsync(host, "/a2a", "acme");

        // The request must not run in tenant acme without proving acme's scheme.
        Assert.False(Succeeded(r), $"{(int)r.Status} {r.Body}");
    }

    [Fact]
    public async Task A_body_tenant_that_agrees_with_the_route_tenant_still_works()
    {
        await using var host = await StartAsync();

        var r = await ListTasksAsync(host, "/a2a/open", "open");

        Assert.True(Succeeded(r), $"{(int)r.Status} {r.Body}");
    }

    [Fact]
    public async Task A_body_tenant_whose_card_scheme_is_met_runs_when_the_route_names_no_tenant()
    {
        await using var host = await StartAsync();

        var r = await ListTasksAsync(host, "/a2a", "open");

        Assert.True(Succeeded(r), $"{(int)r.Status} {r.Body}");
    }

    [Fact]
    public async Task A_body_tenant_that_differs_from_the_route_tenant_is_refused()
    {
        await using var host = await StartAsync();

        var r = await ListTasksAsync(host, "/a2a/open", "acme");

        Assert.False(Succeeded(r), $"{(int)r.Status} {r.Body}");
    }

    [Fact]
    public async Task An_upload_is_checked_against_the_card_of_its_route_tenant()
    {
        await using var host = await StartAsync();
        using var http = host.Http("alice");
        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/pdf");

        using var locked = await http.PostAsync("/a2a/uploads/acme", content);
        using var open = await http.PostAsync("/a2a/uploads/open", new ByteArrayContent([1, 2, 3])
        {
            Headers = { ContentType = MediaTypeHeaderValue.Parse("application/pdf") },
        });

        Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
        Assert.True(open.IsSuccessStatusCode, $"{(int)open.StatusCode} {await open.Content.ReadAsStringAsync()}");
    }

    /// <summary>The same agent on gRPC, whose calls always carry the tenant in the message (the route has none).</summary>
    private static Task<TestAgentHost> StartGrpcAsync() =>
        TestAgentHost.StartAsync(
            services: s =>
            {
                s.AddA2AServer().AddTenantCardProvider<Cards>();
                s.AddA2AGrpc();
                s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, NobodyHandler>(NobodyHandler.SchemeName, null);
            },
            configurePipeline: app => app.MapA2AGrpc().RequireAuthorization()
                .RequireAgentCardSchemes(new Dictionary<string, string> { ["locked"] = NobodyHandler.SchemeName }));

    [Fact]
    public async Task A_grpc_call_runs_in_a_tenant_whose_card_scheme_is_met()
    {
        await using var host = await StartGrpcAsync();
        var client = host.Client(ProtocolBindings.Grpc);

        var page = await client.ListTasksAsync(new ListTasksRequest { Tenant = "open", PageSize = 10 });

        Assert.NotNull(page);
    }

    [Fact]
    public async Task A_grpc_call_to_a_tenant_whose_card_scheme_is_not_met_is_refused()
    {
        await using var host = await StartGrpcAsync();
        var client = host.Client(ProtocolBindings.Grpc);

        await Assert.ThrowsAnyAsync<A2AException>(() => client.ListTasksAsync(new ListTasksRequest { Tenant = "acme", PageSize = 10 }));
    }
}
