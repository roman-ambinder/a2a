using System.Net;
using System.Text;
using System.Text.Json;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Tests;

public class AuthorizationTests
{
    private sealed class DenyGetTask : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperation op, object request, CancellationToken ct) =>
            ValueTask.FromResult(op != A2AOperation.GetTask);
    }

    private sealed class DenyList : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperation op, object request, CancellationToken ct) =>
            ValueTask.FromResult(op != A2AOperation.ListTasks);
    }

    private sealed class DenyAll : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperation op, object request, CancellationToken ct) =>
            ValueTask.FromResult(false);
    }

    private sealed class CaptureLogger : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Sink(this);
        public void Dispose() { }

        private sealed class Sink(CaptureLogger owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries) owner.Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private static Action<IServiceCollection> Deny<T>() where T : A2AAuthorizationHandler =>
        s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler, T>());

    [Fact]
    public async Task Startup_fails_without_authorization_or_explicit_anonymous()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestAgentHost.StartAsync(o => o.AllowAnonymous = false, requireAuth: false));
        Assert.Contains("RequireAuthorization", ex.Message);
    }

    [Fact]
    public async Task Explicit_anonymous_starts_and_warns()
    {
        var log = new CaptureLogger();
        await using var host = await TestAgentHost.StartAsync(o => o.AllowAnonymous = true,
            services: s => s.AddLogging(b => b.AddProvider(log)), requireAuth: false);
        var warnings = log.Entries.Where(e => e.Level == LogLevel.Warning &&
            e.Message == "A2A endpoints allow anonymous callers. All anonymous callers share one owner and can read each other's tasks.").ToList();
        Assert.Single(warnings);
        var reply = await host.Client(ProtocolBindings.JsonRpc, user: null).SendMessageAsync("hello");
        Assert.IsType<MessageResponse>(reply);
    }

    [Fact]
    public async Task MapA2A_without_AddA2AServerAspNetCore_throws()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddA2AServer(o => o.AgentCard = TestAgentHost.DefaultCard()).AddAgentExecutor<ScenarioAgent>();
        await using var app = builder.Build();
        var ex = Assert.Throws<InvalidOperationException>(() => app.MapA2A("/a2a"));
        Assert.Contains("AddA2AServerAspNetCore", ex.Message);
    }

    [Theory]
    [InlineData("POST", "/a2a", """{"jsonrpc":"2.0","id":1,"method":"GetTask","params":{"id":"x"}}""")]
    [InlineData("GET", "/a2a/tasks/x", null)]
    [InlineData("POST", "/a2a/message:stream", """{"message":{"messageId":"m","role":"ROLE_USER","parts":[{"text":"hello"}]}}""")]
    [InlineData("GET", "/a2a/extendedAgentCard", null)]
    [InlineData("POST", "/a2a/acme", """{"jsonrpc":"2.0","id":1,"method":"GetTask","params":{"id":"x"}}""")]
    [InlineData("GET", "/a2a/acme/tasks", null)]
    public async Task RequireAuthorization_covers_every_binding_endpoint(string method, string path, string? body)
    {
        await using var host = await TestAgentHost.StartAsync();
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        req.Headers.Add("A2A-Version", "1.0");
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await host.Http().SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Every_A2A_endpoint_has_authorization_metadata()
    {
        await using var host = await TestAgentHost.StartAsync();
        var endpoints = host.App.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .Where(e => e.DisplayName?.StartsWith("A2A ") == true && !e.DisplayName.StartsWith("A2A Agent Card")).ToList();
        Assert.True(endpoints.Count > 20);
        Assert.All(endpoints, e => Assert.NotNull(e.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()));
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task Handler_denial_on_task_op_looks_like_not_found(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(services: Deny<DenyGetTask>());
        var client = host.Client(binding, "alice");
        var task = (await client.SendMessageAsync("task")).RequireTask();
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync(new GetTaskRequest { Id = task.Id }));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task Handler_denial_on_list_is_permission_denied(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(services: Deny<DenyList>());
        var client = host.Client(binding, "alice");
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.ListTasksAsync(new ListTasksRequest()));
        Assert.Equal(A2AErrorCode.PermissionDenied, ex.Code);

        // On the wire: HTTP 403, and JSON-RPC carries the PERMISSION_DENIED reason.
        using var req = binding == ProtocolBindings.JsonRpc
            ? new HttpRequestMessage(HttpMethod.Post, "/a2a")
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ListTasks","params":{}}""", Encoding.UTF8, "application/json"),
            }
            : new HttpRequestMessage(HttpMethod.Get, "/a2a/tasks");
        req.Headers.Add("A2A-Version", "1.0");
        var resp = await host.Http("alice").SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("PERMISSION_DENIED", await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task Handler_denial_on_send_without_task_id_is_permission_denied(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(services: Deny<DenyAll>());
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding, "alice").SendMessageAsync("hello"));
        Assert.Equal(A2AErrorCode.PermissionDenied, ex.Code);
    }

    [Fact]
    public async Task Agent_card_endpoint_stays_anonymous()
    {
        await using var host = await TestAgentHost.StartAsync();
        var resp = await host.Http().GetAsync("/.well-known/agent-card.json");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Builder_registers_executor_card_and_handler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var card = TestAgentHost.DefaultCard();
        services.AddA2AServer().AddAgentExecutor<ScenarioAgent>().AddAgentCard(card).AddAuthorizationHandler<DenyAll>();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<ScenarioAgent>(scope.ServiceProvider.GetRequiredService<IAgentExecutor>());
        Assert.Same(card, provider.GetRequiredService<IOptions<A2AServerOptions>>().Value.AgentCard);
        Assert.IsType<DenyAll>(provider.GetRequiredService<A2AAuthorizationHandler>());
    }

    [Fact]
    public async Task Default_authorization_handler_allows_everything()
    {
        var services = new ServiceCollection();
        services.AddA2AServer().AddInMemoryStores();
        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<A2AAuthorizationHandler>();
        Assert.True(await handler.AuthorizeAsync(ServerCallContext.Anonymous(), A2AOperation.ListTasks, new object(), default));
    }

    [Fact]
    public async Task Tenant_route_value_scopes_tasks_on_a_tenant_pattern()
    {
        // MapA2A("/{tenant}/a2a"): no second tenant segment, the route value drives the tenant.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization();
        builder.Services.AddA2AServer(o => o.AgentCard = TestAgentHost.DefaultCard()).AddAgentExecutor<ScenarioAgent>();
        builder.Services.AddA2AServerAspNetCore();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseA2A();
        app.UseAuthorization();
        app.MapA2A("/{tenant}/a2a").RequireAuthorization();
        await app.StartAsync();
        try
        {
            var http = app.GetTestClient();
            http.DefaultRequestHeaders.Add("X-Test-User", "alice");
            http.DefaultRequestHeaders.Add("A2A-Version", "1.0");
            var create = await http.PostAsync("/acme/a2a/message:send", new StringContent(
                """{"message":{"messageId":"m","role":"ROLE_USER","parts":[{"text":"task"}]}}""", Encoding.UTF8, "application/json"));
            create.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            var id = doc.RootElement.GetProperty("task").GetProperty("id").GetString();
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/acme/a2a/tasks/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/other/a2a/tasks/{id}")).StatusCode);
            // No "/{tenant}/{tenant}/a2a" double segment exists.
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/acme/a2a/other/tasks/{id}")).StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Handler_rejects_anonymous_callers_when_anonymous_is_not_allowed()
    {
        // The startup check is bypassed here: the handler is used directly. The core backstop still refuses.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddA2AServer(o => o.AgentCard = TestAgentHost.DefaultCard()).AddAgentExecutor<ScenarioAgent>();
        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<A2ARequestHandler>();
        var ex = Assert.Throws<A2AException>(() => handler.ValidateCall(ServerCallContext.Anonymous()));
        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
    }

    [Fact]
    public async Task Handler_operations_reject_anonymous_callers_even_when_ValidateCall_is_skipped()
    {
        // Direct callers of the handler never go through ValidateCall. The per-operation authorization step must still refuse.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddA2AServer(o => o.AgentCard = TestAgentHost.DefaultCard()).AddAgentExecutor<ScenarioAgent>();
        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<A2ARequestHandler>();
        var ex = await Assert.ThrowsAsync<A2AException>(() => handler.GetTaskAsync(new GetTaskRequest { Id = "t1" }, ServerCallContext.Anonymous()));
        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
    }

    [Fact]
    public async Task Startup_style_hosting_without_authorization_is_refused_after_start()
    {
        // UseEndpoints runs inside GenericWebHostService.StartAsync, after the validator started with no endpoints.
        using var host = new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(s =>
            {
                s.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
                s.AddAuthorization();
                s.AddRouting();
                s.AddA2AServer(o => o.AgentCard = TestAgentHost.DefaultCard()).AddAgentExecutor<ScenarioAgent>();
                s.AddA2AServerAspNetCore();
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(e => e.MapA2A("/a2a")); // no RequireAuthorization()
            })).Build();
        await host.StartAsync();
        var lifetime = host.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);

        // And the backstop alone already rejects anonymous callers.
        using var req = new HttpRequestMessage(HttpMethod.Get, "/a2a/tasks");
        req.Headers.Add("A2A-Version", "1.0");
        var resp = await host.GetTestClient().SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        await host.StopAsync();
    }

    [Fact]
    public async Task Endpoint_with_RequireAuthorization_and_AllowAnonymous_fails_startup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestAgentHost.StartAsync(configureRoutes: g => g.AllowAnonymous()));
        Assert.Contains("RequireAuthorization", ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithA2AStartupCheck_applies_the_startup_check_and_the_rate_limit(bool requireAuth)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization();
        builder.Services.AddA2AServer(o => o.AgentCard = TestAgentHost.DefaultCard()).AddAgentExecutor<ScenarioAgent>();
        builder.Services.AddA2AServerAspNetCore();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseA2A();
        app.UseAuthorization();
        var custom = app.MapGet("/custom", () => "ok").WithDisplayName("custom").WithA2AStartupCheck(app, "Custom");
        if (requireAuth) custom.RequireAuthorization();

        if (!requireAuth)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
            Assert.Contains("no authorization policy", ex.Message);
            return;
        }
        await app.StartAsync();
        try
        {
            var endpoint = Assert.Single(app.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints,
                e => e.DisplayName == "A2A Custom custom");
            var limit = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>();
            Assert.Equal(A2ARateLimitingExtensions.PolicyName, limit?.PolicyName);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
