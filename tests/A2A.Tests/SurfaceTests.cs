using System.Net;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Pipeline;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace A2A.Tests;

public class SurfaceTests
{
    private const string CardPath = "/.well-known/agent-card.json";

    private static WebApplicationBuilder NewBuilder(bool authorization = true, Action<A2AServerBuilder>? a2a = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        if (authorization) builder.Services.AddAuthorization(o => o.AddPolicy("agents", p => p.RequireAuthenticatedUser()));
        var server = builder.Services.AddA2A(a => a.AddAgentExecutor<ScenarioAgent>().AddAgentCard(TestAgentHost.DefaultCard()));
        a2a?.Invoke(server);
        return builder;
    }

    private static async Task<WebApplication> StartAsync(WebApplicationBuilder builder, Action<WebApplication> map)
    {
        var app = builder.Build();
        app.UseAuthentication();
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
    public async Task AddA2A_returns_the_builder_and_every_setting_goes_on_it()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var a2a = services.AddA2A()
            .AddAgentExecutor<ScenarioAgent>()
            .Configure(o => o.Limits.MaxRequestBodyBytes = 1234)
            .Configure(o => o.AllowAnonymous = true) // several calls add up
            .ConfigureHttp(h => h.PublicBaseUrl = new Uri("https://agent.example.com"))
            .ConfigureRateLimiting(r => r.TokenLimit = 7)
            .AddRetention(r => r.BatchSize = 42)
            .AddDataProtectionPushSecrets();
        Assert.Same(services, a2a.Services);
        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<A2AServerOptions>>().Value;
        Assert.Equal(1234, server.Limits.MaxRequestBodyBytes);
        Assert.True(server.AllowAnonymous);
        Assert.Equal(new Uri("https://agent.example.com"),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<A2AHttpOptions>>().Value.PublicBaseUrl);
        Assert.Equal(7, provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<A2ARateLimitOptions>>().Value.TokenLimit);
        Assert.Equal(42, provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<A2A.Server.Retention.A2ARetentionOptions>>().Value.BatchSize);
        Assert.NotNull(provider.GetRequiredService<A2A.Server.Retention.A2ARetentionService>());
        Assert.IsType<DataProtectionPushSecretProtector>(provider.GetRequiredService<A2A.Server.Push.IPushSecretProtector>());
    }

    [Fact]
    public void Builder_methods_reject_null()
    {
        var a2a = new ServiceCollection().AddA2A();
        Assert.Throws<ArgumentNullException>(() => a2a.Configure(null!));
        Assert.Throws<ArgumentNullException>(() => a2a.ConfigureHttp(null!));
        Assert.Throws<ArgumentNullException>(() => a2a.ConfigureRateLimiting(null!));
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
    public async Task RequirePolicy_on_the_builder_protects_every_A2A_endpoint()
    {
        await using var app = await StartAsync(NewBuilder(a2a: a => a.RequirePolicy("agents")), a => a.MapA2A("/a2a"));
        var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("A2A-Version", "1.0");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/a2a/tasks")).StatusCode);
        http.DefaultRequestHeaders.Add("X-Test-User", "alice");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/a2a/tasks")).StatusCode);
    }

    [Fact]
    public async Task Card_stays_anonymous_when_the_builder_requires_a_policy()
    {
        await using var app = await StartAsync(NewBuilder(a2a: a => a.RequirePolicy("agents")), a => a.MapA2A("/a2a"));
        Assert.Equal(HttpStatusCode.OK, (await app.GetTestClient().GetAsync(CardPath)).StatusCode);
    }

    [Fact]
    public async Task Card_stays_anonymous_when_group_requires_authorization()
    {
        await using var app = await StartAsync(NewBuilder(), a => a.MapA2A("/a2a").RequireAuthorization("agents"));
        Assert.Equal(HttpStatusCode.OK, (await app.GetTestClient().GetAsync(CardPath)).StatusCode);
    }

    [Fact]
    public async Task Endpoint_authorization_wins_over_the_builder_policy()
    {
        // The group's own policy replaces the builder's default, as for the card schemes.
        var builder = NewBuilder(a2a: a => a.RequirePolicy("admins"));
        builder.Services.AddAuthorization(o => o.AddPolicy("admins", p => p.RequireRole("admin"))); // alice is no admin
        await using var app = await StartAsync(builder, a => a.MapA2A("/a2a").RequireAuthorization("agents"));
        var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("A2A-Version", "1.0");
        http.DefaultRequestHeaders.Add("X-Test-User", "alice");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/a2a/tasks")).StatusCode);
    }

    [Fact]
    public async Task RequirePolicy_with_an_unregistered_policy_fails_startup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartAsync(NewBuilder(a2a: a => a.RequirePolicy("nope")), a => a.MapA2A("/a2a")));
        Assert.Contains("RequirePolicy(\"nope\")", ex.Message, StringComparison.Ordinal);
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
        Assert.Equal(A2AStartupValidator.NoPolicyMessage, noAuth.Message);
        Assert.Contains("RequirePolicy", noAuth.Message, StringComparison.Ordinal);
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
