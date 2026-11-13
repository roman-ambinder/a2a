using System.Net;
using System.Text;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace A2A.Tests;

/// <summary>
///     The startup authorization check reads the endpoints as the app serves them: with the conventions of every route
///     group around <c>MapA2A</c>, not the group's inner view.
/// </summary>
public class StartupCheckGroupTests
{
    private static readonly A2AApiKey Key = A2AApiKey.Create();

    private static WebApplication Build(Action<A2AServerBuilder>? a2a = null, Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy("custom", p => p.RequireAuthenticatedUser());
            o.AddPolicy("member", p => p.RequireAuthenticatedUser().RequireClaim("sub", "alice"));
        });
        services?.Invoke(builder.Services);
        builder.Services.AddA2A(a =>
        {
            a.AddAgentExecutor<ScenarioAgent>().AddAgentCard(TestAgentHost.DefaultCard());
            a2a?.Invoke(a);
        });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    private static async Task<HttpStatusCode> Send(WebApplication app, string path, string? user = null,
        string? apiKey = null)
    {
        var http = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{"message":{"messageId":"m","role":"ROLE_USER","parts":[{"text":"hello"}]}}}""",
            Encoding.UTF8, "application/json");
        request.Content.Headers.Add("A2A-Version", "1.0");
        if (user is not null) request.Headers.Add("X-Test-User", user);
        if (apiKey is not null) request.Headers.Add("X-Api-Key", apiKey);
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>The authorization policies on the served A2A endpoints (not the card), in order, without repeats.</summary>
    private static List<string?> Policies(WebApplication app) =>
        app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(e => e.DisplayName?.StartsWith("A2A ", StringComparison.Ordinal) == true &&
                        !e.DisplayName.StartsWith("A2A Agent Card", StringComparison.Ordinal))
            .SelectMany(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(d => d.Policy)).Distinct().ToList();

    [Fact]
    public async Task Group_RequireAuthorization_without_a_builder_policy_starts_and_enforces_it()
    {
        await using var app = Build();
        app.MapGroup("/g").RequireAuthorization().MapA2A("/a2a");
        await app.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await Send(app, "/g/a2a"));
        Assert.Equal(HttpStatusCode.OK, await Send(app, "/g/a2a", user: "alice"));
        await app.StopAsync();
    }

    [Fact]
    public async Task Group_RequireAuthorization_with_a_named_policy_starts_and_enforces_it()
    {
        await using var app = Build();
        app.MapGroup("/g").RequireAuthorization("member").MapA2A("/a2a");
        await app.StartAsync();
        Assert.Equal(["member"], Policies(app));
        Assert.Equal(HttpStatusCode.Unauthorized, await Send(app, "/g/a2a"));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(app, "/g/a2a", user: "bob"));
        Assert.Equal(HttpStatusCode.OK, await Send(app, "/g/a2a", user: "alice"));
        await app.StopAsync();
    }

    [Fact]
    public async Task Authorization_on_an_outer_group_of_nested_groups_is_seen()
    {
        await using var app = Build();
        app.MapGroup("/o").RequireAuthorization().MapGroup("/i").MapGroup("/j").MapA2A("/a2a");
        await app.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await Send(app, "/o/i/j/a2a"));
        Assert.Equal(HttpStatusCode.OK, await Send(app, "/o/i/j/a2a", user: "alice"));
        await app.StopAsync();
    }

    [Fact]
    public async Task A_group_without_authorization_and_no_builder_policy_still_fails_startup()
    {
        await using var app = Build();
        app.MapGroup("/o").MapGroup("/i").MapA2A("/a2a");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.StartsWith(A2AStartupValidator.NoPolicyMessage, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Builder_RequirePolicy_applies_inside_an_outer_group()
    {
        await using var app = Build(a => a.RequirePolicy("member"));
        app.MapGroup("/o").MapGroup("/i").MapA2A("/a2a");
        await app.StartAsync();
        Assert.Equal(["member"], Policies(app));
        Assert.Equal(HttpStatusCode.Unauthorized, await Send(app, "/o/i/a2a"));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(app, "/o/i/a2a", user: "bob"));
        Assert.Equal(HttpStatusCode.OK, await Send(app, "/o/i/a2a", user: "alice"));
        await app.StopAsync();
    }

    [Fact]
    public async Task Outer_group_authorization_is_not_overridden_or_doubled_by_the_builder_policy()
    {
        // As with RequireAuthorization() on the endpoints: authorization of their own (here from an outer group) wins.
        await using var app = Build(a => a.RequirePolicy("member"));
        app.MapGroup("/o").RequireAuthorization("custom").MapGroup("/i").MapA2A("/a2a");
        await app.StartAsync();
        Assert.Equal(["custom"], Policies(app));
        Assert.Equal(HttpStatusCode.Unauthorized, await Send(app, "/o/i/a2a"));
        Assert.Equal(HttpStatusCode.OK, await Send(app, "/o/i/a2a", user: "bob"));
        await app.StopAsync();
    }

    [Fact]
    public async Task Group_AllowAnonymous_next_to_a_builder_policy_fails_startup()
    {
        // The same answer as AllowAnonymous() on the endpoints: the builder policy is skipped, so the endpoints would be open
        // to the routing layer and every caller would reach the handler's own anonymous refusal instead.
        await using var app = Build(a => a.AddApiKeyAuthentication(o => o.Add("billing", Key.Hash)));
        app.MapGroup("/g").AllowAnonymous().MapA2A("/a2a");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.StartsWith(A2AStartupValidator.NoPolicyMessage, ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(A2AStartupValidator.AllowAnonymousHint, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllowAnonymous_on_an_outer_group_of_nested_groups_fails_startup()
    {
        await using var app = Build(a => a.RequirePolicy("member"));
        app.MapGroup("/o").AllowAnonymous().MapGroup("/i").MapA2A("/a2a");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.StartsWith(A2AStartupValidator.NoPolicyMessage, ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(A2AStartupValidator.AllowAnonymousHint, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Group_AllowAnonymous_with_RequireAuthorization_on_the_endpoints_fails_startup()
    {
        await using var app = Build();
        app.MapGroup("/g").AllowAnonymous().MapA2A("/a2a").RequireAuthorization();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.StartsWith(A2AStartupValidator.NoPolicyMessage, ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(A2AStartupValidator.AllowAnonymousHint, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Group_AllowAnonymous_starts_when_anonymous_callers_are_allowed()
    {
        await using var app = Build(a => a.Configure(o => o.AllowAnonymous = true));
        app.MapGroup("/g").AllowAnonymous().MapA2A("/a2a");
        await app.StartAsync();
        Assert.Equal(HttpStatusCode.OK, await Send(app, "/g/a2a"));
        await app.StopAsync();
    }

    [Fact]
    public async Task The_api_key_policy_reaches_endpoints_inside_a_group_and_is_enforced()
    {
        await using var app = Build(a => a.AddApiKeyAuthentication(o => o.Add("billing", Key.Hash)));
        app.MapGroup("/g").MapA2A("/a2a");
        await app.StartAsync();
        Assert.Equal([A2ACardAuthenticationExtensions.DefaultPolicyName], Policies(app));
        Assert.Equal(HttpStatusCode.Unauthorized, await Send(app, "/g/a2a"));
        Assert.Equal(HttpStatusCode.OK, await Send(app, "/g/a2a", apiKey: Key.Key));
        await app.StopAsync();
    }

    [Fact]
    public async Task The_artifact_route_inside_a_secured_group_starts()
    {
        await using var app = Build();
        var group = app.MapGroup("/g").RequireAuthorization();
        group.MapA2A("/a2a");
        group.MapA2AArtifacts("/a2a");
        await app.StartAsync();
        await app.StopAsync();
    }
}
