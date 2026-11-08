using System.Net;
using System.Text.Json;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Pipeline;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace A2A.Tests;

public class SurfaceTests
{
    private const string CardPath = "/.well-known/agent-card.json";

    private static WebApplicationBuilder NewBuilder(bool authorization = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        if (authorization) builder.Services.AddAuthorization(o => o.AddPolicy("agents", p => p.RequireAuthenticatedUser()));
        builder.Services.AddA2A(a => a.AddAgentExecutor<ScenarioAgent>().AddAgentCard(TestAgentHost.DefaultCard()));
        return builder;
    }

    private static async Task<WebApplication> StartAsync(WebApplicationBuilder builder, Action<WebApplication> map)
    {
        var app = builder.Build();
        app.UseAuthentication();
        app.UseA2A();
        app.UseAuthorization();
        map(app);
        await app.StartAsync();
        return app;
    }

    private static int EndpointCount(WebApplication app, string text) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints)
            .Count(e => (e as RouteEndpoint)?.RoutePattern.RawText == text);

    [Fact]
    public async Task AddA2A_registers_core_and_aspnetcore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddA2A(a => a.AddAgentExecutor<ScenarioAgent>());
        await using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IA2ARequestHandler>());
        Assert.NotNull(provider.GetRequiredService<A2AEndpointRegistry>());
        Assert.IsType<RouteTenantResolver>(provider.GetRequiredService<ITenantResolver>());
    }

    [Fact]
    public async Task MapA2A_maps_card_by_default()
    {
        await using var app = await StartAsync(NewBuilder(), a => a.MapA2A("/a2a").RequireAuthorization());
        var resp = await app.GetTestClient().GetAsync(CardPath);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task MapA2A_card_can_be_disabled()
    {
        await using var app = await StartAsync(NewBuilder(), a => a.MapA2A("/a2a", o => o.MapAgentCard = false).RequireAuthorization());
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetTestClient().GetAsync(CardPath)).StatusCode);
    }

    [Fact]
    public async Task MapA2A_with_policy_requires_authorization()
    {
        await using var app = await StartAsync(NewBuilder(), a => a.MapA2A("/a2a", o => o.AuthorizationPolicy = "agents"));
        var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("A2A-Version", "1.0");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/a2a/tasks")).StatusCode);
        http.DefaultRequestHeaders.Add("X-Test-User", "alice");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/a2a/tasks")).StatusCode);
    }

    [Fact]
    public async Task Card_stays_anonymous_when_group_requires_authorization()
    {
        await using var app = await StartAsync(NewBuilder(), a => a.MapA2A("/a2a", o => o.AuthorizationPolicy = "agents"));
        Assert.Equal(HttpStatusCode.OK, (await app.GetTestClient().GetAsync(CardPath)).StatusCode);
    }

    [Fact]
    public async Task MapA2A_twice_maps_the_card_once()
    {
        await using var app = await StartAsync(NewBuilder(), a =>
        {
            a.MapA2A("/one").RequireAuthorization();
            a.MapA2A("/two").RequireAuthorization();
        });
        Assert.Equal(1, EndpointCount(app, CardPath));
        Assert.Equal(HttpStatusCode.OK, (await app.GetTestClient().GetAsync(CardPath)).StatusCode);
    }

    [Fact]
    public async Task MapAgentCard_after_MapA2A_is_a_no_op()
    {
        await using var app = await StartAsync(NewBuilder(), a =>
        {
            a.MapA2A("/a2a").RequireAuthorization();
            a.MapAgentCard().RequireAuthorization(); // ignored: nothing is mapped
        });
        Assert.Equal(1, EndpointCount(app, CardPath));
        Assert.Equal(HttpStatusCode.OK, (await app.GetTestClient().GetAsync(CardPath)).StatusCode);
    }

    [Fact]
    public async Task MapA2A_after_MapAgentCard_does_not_map_the_card_again()
    {
        await using var app = await StartAsync(NewBuilder(), a =>
        {
            a.MapAgentCard();
            a.MapA2A("/a2a").RequireAuthorization();
        });
        Assert.Equal(1, EndpointCount(app, CardPath));
    }

    [Fact]
    public async Task MapA2A_on_a_group_does_not_map_the_card()
    {
        await using var app = await StartAsync(NewBuilder(), a => a.MapGroup("/api").MapA2A("/a2a").RequireAuthorization());
        var http = app.GetTestClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api" + CardPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(CardPath)).StatusCode);

        await using var withRoot = await StartAsync(NewBuilder(), a =>
        {
            a.MapGroup("/api").MapA2A("/a2a").RequireAuthorization();
            a.MapAgentCard();
        });
        Assert.Equal(HttpStatusCode.OK, (await withRoot.GetTestClient().GetAsync(CardPath)).StatusCode);
        // The root card mapping also serves each tenant's card at /{tenant}/.well-known/agent-card.json (one agent serves every
        // tenant here), so "/api" is the card of tenant "api", not a card mapped by the group.
        Assert.Equal(1, EndpointCount(withRoot, CardPath));
    }

    [Fact]
    public async Task MapAgentCard_on_a_group_throws()
    {
        var app = NewBuilder().Build();
        var ex = Assert.Throws<InvalidOperationException>(() => app.MapGroup("/api").MapAgentCard());
        Assert.Equal("Map the Agent Card on the application root (app.MapAgentCard()); the /.well-known path is relative to the origin.", ex.Message);
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Startup_error_names_the_fix()
    {
        var noAuth = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(NewBuilder(), a => a.MapA2A("/a2a")));
        Assert.Equal("A2A endpoints have no authorization policy. Call app.MapA2A(...).RequireAuthorization() or MapA2A(pattern, o => o.AuthorizationPolicy = \"...\"), or set A2AServerOptions.AllowAnonymous = true.", noAuth.Message);

        var builder = NewBuilder();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization(); // no UseA2A()
        app.MapA2A("/a2a").RequireAuthorization();
        var noLimiter = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.Equal("Call app.UseA2A() before app.UseAuthorization(), or set A2ARateLimitOptions.Enabled = false (services.AddA2ARateLimiting(o => o.Enabled = false)).", noLimiter.Message);
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Extension_policy_that_is_not_registered_fails_startup()
    {
        var builder = NewBuilder();
        builder.Services.AddA2AServerExtension("ext.op", "nope");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(builder, a => a.MapA2A("/a2a").RequireAuthorization()));
        Assert.Equal("Extension operation 'ext.op' names authorization policy 'nope', which is not registered.", ex.Message);
    }

    [Fact]
    public async Task Extension_policy_without_authorization_service_fails_startup()
    {
        var builder = NewBuilder(authorization: false);
        builder.Services.AddA2AServerExtension("ext.op", "agents");
        var app = builder.Build(); // no UseAuthorization(): it needs the authorization services itself
        app.MapA2A("/a2a", o => o.MapAgentCard = false);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        await app.DisposeAsync();
        Assert.Equal("Extension operation 'ext.op' names authorization policy 'agents', but no IAuthorizationService is registered. Call services.AddAuthorization().", ex.Message);
    }

    [Fact]
    public async Task Extension_policy_that_is_registered_starts()
    {
        var builder = NewBuilder();
        builder.Services.AddA2AServerExtension("ext.op", "agents");
        await using var app = await StartAsync(builder, a => a.MapA2A("/a2a").RequireAuthorization());
    }
}

internal static class SurfaceTestExtensions
{
    public static IServiceCollection AddA2AServerExtension(this IServiceCollection services, string method, string policy)
    {
        services.AddSingleton(new A2AExtensionOperation(method, "https://example.com/ext",
            (_, p, _) => ValueTask.FromResult(p), policy));
        return services;
    }
}
