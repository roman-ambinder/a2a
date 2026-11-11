using System.Net;
using System.Text;
using A2A.Client;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace A2A.Tests;

/// <summary>
///     AddCardAuthentication / AddBearerAuthentication: the card declares the scheme, endpoints without authorization
///     require one of the declared schemes, and a scheme that is not registered fails at startup. The test scheme stands
///     in for JWT bearer: it authenticates the X-Test-User header.
/// </summary>
public class BearerCardAuthenticationTests
{
    private static async Task<WebApplication> StartAsync(Action<A2AServerBuilder> configure, bool registerTestScheme = true,
        Action<WebApplication>? map = null, Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (registerTestScheme)
            builder.Services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        services?.Invoke(builder.Services);
        builder.Services.AddA2A(a =>
        {
            a.AddAgentExecutor<ScenarioAgent>().AddAgentCard(AgentCard.Create("Card", "Declares its schemes"));
            configure(a);
        });
        builder.Services.ConfigureA2AHttp(h => h.PublicBaseUrl = new Uri("http://localhost"));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseA2A();
        app.UseAuthorization();
        if (map is null) app.MapA2A("/a2a"); // no RequireAuthorization: the declared schemes protect it
        else map(app);
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpStatusCode> Send(WebApplication app, Action<HttpClient>? authenticate = null)
    {
        var http = app.GetTestClient();
        authenticate?.Invoke(http);
        using var content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{"message":{"messageId":"m","role":"ROLE_USER","parts":[{"text":"hello"}]}}}""",
            Encoding.UTF8, "application/json");
        content.Headers.Add("A2A-Version", "1.0");
        using var response = await http.PostAsync("/a2a", content);
        return response.StatusCode;
    }

    private static void AsUser(HttpClient http) => http.DefaultRequestHeaders.Add("X-Test-User", "alice");

    [Fact]
    public async Task Bearer_is_declared_on_the_card_with_its_scopes()
    {
        await using var app = await StartAsync(a => a.AddBearerAuthentication(TestAuthHandler.SchemeName, scopes: ["agent.invoke"]));
        var card = app.Services.GetRequiredService<IAgentCardProvider>().Get();
        var bearer = Assert.IsType<HttpAuthScheme>(card.SecuritySchemes!["bearer"]).Value;
        Assert.Equal(("Bearer", "JWT"), (bearer.Scheme, bearer.BearerFormat));
        var requirement = Assert.Single(card.SecurityRequirements!);
        Assert.Equal(["agent.invoke"], requirement.Schemes["bearer"].List);
    }

    [Fact]
    public async Task Endpoints_without_authorization_require_the_declared_scheme()
    {
        await using var app = await StartAsync(a => a.AddBearerAuthentication(TestAuthHandler.SchemeName));
        Assert.Equal(HttpStatusCode.Unauthorized, await Send(app));
        Assert.Equal(HttpStatusCode.OK, await Send(app, AsUser));
    }

    [Fact]
    public async Task With_api_keys_too_either_scheme_gets_in_and_both_are_on_the_card()
    {
        var key = A2AApiKey.Create();
        await using var app = await StartAsync(a => a
            .AddBearerAuthentication(TestAuthHandler.SchemeName)
            .AddApiKeyAuthentication(o => o.Add("billing", key.Hash)));
        Assert.Equal(HttpStatusCode.Unauthorized, await Send(app));
        Assert.Equal(HttpStatusCode.OK, await Send(app, AsUser));
        Assert.Equal(HttpStatusCode.OK, await Send(app, h => h.DefaultRequestHeaders.Add("X-Api-Key", key.Key)));

        var card = app.Services.GetRequiredService<IAgentCardProvider>().Get();
        Assert.Equal(["apiKey", "bearer"], card.SecuritySchemes!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(2, card.SecurityRequirements!.Count); // two alternatives, not one requirement for both
    }

    [Fact]
    public async Task An_endpoint_with_its_own_policy_keeps_it()
    {
        await using var app = await StartAsync(a => a.AddBearerAuthentication(TestAuthHandler.SchemeName),
            map: a => a.MapA2A("/a2a").RequireAuthorization("admins"),
            services: s => s.AddAuthorization(o => o.AddPolicy("admins", p => p
                .AddAuthenticationSchemes(TestAuthHandler.SchemeName).RequireClaim("role", "admin"))));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(app, AsUser)); // authenticated, but not an admin
    }

    [Fact]
    public async Task A_scheme_that_is_not_registered_fails_at_startup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartAsync(a => a.AddBearerAuthentication(), registerTestScheme: false));
        Assert.Contains("'Bearer'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddJwtBearer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_card_scheme_name_is_declared_once()
    {
        var services = new ServiceCollection();
        var builder = services.AddA2AServer();
        builder.AddBearerAuthentication();
        Assert.Throws<ArgumentException>(() => builder.AddCardAuthentication("bearer", SecurityScheme.Bearer(), "Other"));
    }

    [Fact]
    public async Task A_scheme_the_card_already_has_is_kept()
    {
        var own = SecurityScheme.Bearer("opaque", "the host's own");
        await using var app = await StartAsync(a =>
        {
            a.AddAgentCard(new AgentCard
            {
                Name = "Card", Description = "d", Version = "1",
                SecuritySchemes = new() { ["bearer"] = own },
                SecurityRequirements = [new SecurityRequirement { Schemes = { ["bearer"] = new StringList() } }]
            });
            a.AddBearerAuthentication(TestAuthHandler.SchemeName);
        });
        var card = app.Services.GetRequiredService<IAgentCardProvider>().Get();
        Assert.Equal("opaque", Assert.IsType<HttpAuthScheme>(card.SecuritySchemes!["bearer"]).Value.BearerFormat);
        Assert.Single(card.SecurityRequirements!);
    }
}
