using System.Net;
using System.Text.Encodings.Web;
using System.Text;
using A2A.Client;
using A2A.Server;
using A2A.Server.Agents;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Tests;

/// <summary>RequireAgentCardSchemes: requests must be authenticated the way the card says (spec §7.4, §13.3).</summary>
public class CardAuthenticationTests
{
    /// <summary>An ASP.NET Core scheme that never authenticates anyone.</summary>
    private sealed class NobodyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Nobody";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    private static Task<TestAgentHost> StartAsync(Dictionary<string, string> map, List<SecurityRequirement>? requirements) =>
        TestAgentHost.StartAsync(
            configure: o => o.AgentCard.SecurityRequirements = requirements,
            services: s => s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, NobodyHandler>(NobodyHandler.SchemeName, null),
            configureRoutes: a2a => a2a.RequireAgentCardSchemes(map));

    private static List<SecurityRequirement> Require(params string[] schemes) =>
        [new SecurityRequirement { Schemes = schemes.ToDictionary(s => s, _ => new StringList()) }];

    private static async Task<HttpStatusCode> ListTasksAsync(TestAgentHost host, string? user)
    {
        using var http = host.Http(user);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/a2a/tasks");
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        using var resp = await http.SendAsync(req);
        return resp.StatusCode;
    }

    [Fact]
    public async Task A_request_proved_by_the_mapped_scheme_passes()
    {
        await using var host = await StartAsync(new() { ["bearer"] = TestAuthHandler.SchemeName }, Require("bearer"));
        Assert.Equal(HttpStatusCode.OK, await ListTasksAsync(host, "alice"));
    }

    [Fact]
    public async Task An_authenticated_caller_without_the_card_scheme_is_refused()
    {
        // Alice is authenticated by the default scheme, but the card scheme maps to one that did not authenticate her.
        await using var host = await StartAsync(new() { ["bearer"] = NobodyHandler.SchemeName }, Require("bearer"));
        Assert.Equal(HttpStatusCode.Forbidden, await ListTasksAsync(host, "alice"));
    }

    [Fact]
    public async Task A_card_scheme_without_a_mapping_can_never_be_met()
    {
        await using var host = await StartAsync(new() { ["other"] = TestAuthHandler.SchemeName }, Require("bearer"));
        Assert.Equal(HttpStatusCode.Forbidden, await ListTasksAsync(host, "alice"));
    }

    [Fact]
    public async Task Any_one_requirement_is_enough_and_all_its_schemes_are_needed()
    {
        var map = new Dictionary<string, string> { ["bearer"] = TestAuthHandler.SchemeName, ["apiKey"] = NobodyHandler.SchemeName };
        List<SecurityRequirement> both = [.. Require("bearer", "apiKey")];
        List<SecurityRequirement> either = [.. Require("apiKey"), .. Require("bearer")];

        await using (var host = await StartAsync(map, both))
            Assert.Equal(HttpStatusCode.Forbidden, await ListTasksAsync(host, "alice"));
        await using (var host = await StartAsync(map, either))
            Assert.Equal(HttpStatusCode.OK, await ListTasksAsync(host, "alice"));
    }

    [Fact]
    public async Task A_card_without_requirements_adds_no_check()
    {
        await using var host = await StartAsync(new() { ["bearer"] = NobodyHandler.SchemeName }, null);
        Assert.Equal(HttpStatusCode.OK, await ListTasksAsync(host, "alice"));
    }

    [Fact]
    public async Task The_public_agent_card_stays_anonymous()
    {
        await using var host = await StartAsync(new() { ["bearer"] = NobodyHandler.SchemeName }, Require("bearer"));
        using var http = host.Http();
        using var resp = await http.GetAsync("/.well-known/agent-card.json");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    /// <summary>
    ///     Tenant <c>secure</c> requires the card scheme <c>strong</c>, which nobody proves; <c>open</c> requires nothing;
    ///     the default tenant has a card that requires nothing only when <paramref name="defaultHasCard" />; any other
    ///     tenant has no agent.
    /// </summary>
    private sealed class TenantCards(bool defaultHasCard) : TenantCardProvider
    {
        public override ValueTask<AgentCard?> GetCardAsync(AgentRequestScope scope, CancellationToken cancellationToken)
        {
            if (scope.Tenant is not ("secure" or "open") && !(scope.Tenant == "" && defaultHasCard))
                return ValueTask.FromResult<AgentCard?>(null);
            var card = TestAgentHost.DefaultCard();
            if (scope.Tenant == "secure")
            {
                card.SecuritySchemes = new() { ["strong"] = SecurityScheme.ApiKey("X-Strong") };
                card.SecurityRequirements = Require("strong");
            }
            return ValueTask.FromResult<AgentCard?>(card);
        }
    }

    private static Task<TestAgentHost> StartTenantsAsync(bool defaultHasCard)
    {
        var map = new Dictionary<string, string> { ["strong"] = NobodyHandler.SchemeName };
        return TestAgentHost.StartAsync(
            services: s =>
            {
                s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, NobodyHandler>(NobodyHandler.SchemeName, null);
                s.Replace(ServiceDescriptor.Singleton<TenantCardProvider>(new TenantCards(defaultHasCard)));
            },
            configureRoutes: a2a => a2a.RequireAgentCardSchemes(map),
            grpc: true, configureGrpcRoutes: g => g.RequireAgentCardSchemes(map));
    }

    private static async Task<(HttpStatusCode Status, string Body)> RawListTasksAsync(TestAgentHost host, string path, string? bodyTenant)
    {
        using var http = host.Http("alice");
        var tenant = bodyTenant is null ? "" : $"\"tenant\":\"{bodyTenant}\"";
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ListTasks\",\"params\":{{{tenant}}}}}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        using var resp = await http.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_body_tenant_is_checked_against_its_own_card(bool defaultHasCard)
    {
        await using var host = await StartTenantsAsync(defaultHasCard);
        // The route tenant is checked before the A2A layer runs.
        Assert.Equal(HttpStatusCode.Forbidden, (await RawListTasksAsync(host, "/a2a/secure", null)).Status);
        // The same tenant from the body (the route names none) must not get around the check.
        var (status, body) = await RawListTasksAsync(host, "/a2a", "secure");
        Assert.DoesNotContain("\"result\"", body, StringComparison.Ordinal);
        Assert.Contains("\"error\"", body, StringComparison.Ordinal);
        Assert.NotEqual(HttpStatusCode.OK, status);
        // A tenant whose card requires nothing still works from the body.
        (status, body) = await RawListTasksAsync(host, "/a2a", "open");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"result\"", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc, true)]
    [InlineData(ProtocolBindings.JsonRpc, false)]
    [InlineData(ProtocolBindings.Grpc, true)]
    [InlineData(ProtocolBindings.Grpc, false)]
    public async Task Every_binding_checks_the_tenant_the_call_runs_in(string binding, bool defaultHasCard)
    {
        await using var host = await StartTenantsAsync(defaultHasCard);
        var client = host.Client(binding);
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.ListTasksAsync(new ListTasksRequest { Tenant = "secure" }));
        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        await client.ListTasksAsync(new ListTasksRequest { Tenant = "open" });
    }

    [Fact]
    public void An_empty_or_blank_mapping_is_refused()
    {
        var builder = new FakeConventions();
        Assert.Throws<ArgumentException>(() => builder.RequireAgentCardSchemes(new Dictionary<string, string>()));
        Assert.Throws<ArgumentException>(() => builder.RequireAgentCardSchemes(new Dictionary<string, string> { ["bearer"] = " " }));
    }

    private sealed class FakeConventions : Microsoft.AspNetCore.Builder.IEndpointConventionBuilder
    {
        public void Add(Action<Microsoft.AspNetCore.Builder.EndpointBuilder> convention) { }
    }
}
