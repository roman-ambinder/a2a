using System.Net;
using System.Net.Http.Json;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace A2A.Tests;

public class DevelopmentDefaultsTests
{
    private static WebApplication Build(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddA2A(a => a.AddAgentExecutor<ScenarioAgent>().AddAgentCard(TestAgentHost.DefaultCard())
            .ConfigureForDevelopment());
        var app = builder.Build();
        app.MapA2A("/a2a"); // no RequireAuthorization: development lets anonymous callers in
        return app;
    }

    [Fact]
    public async Task Development_accepts_anonymous_calls_without_the_version_header()
    {
        await using var app = Build(Environments.Development);
        await app.StartAsync();
        using var http = app.GetTestClient();
        var body = new
        {
            jsonrpc = "2.0", id = 1, method = "SendMessage",
            @params = new { message = new { messageId = "m1", role = "ROLE_USER", parts = new[] { new { text = "hello" } } } }
        };
        using var response = await http.PostAsJsonAsync("/a2a", body); // no A2A-Version header, no credentials
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("hi there", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Other_environments_get_nothing_and_need_authorization(string environment)
    {
        // Without authorization of its own, the app refuses to start, and says why the call did not apply.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var app = Build(environment);
            await app.StartAsync();
        });
        Assert.StartsWith(A2AStartupValidator.NoPolicyMessage, ex.Message, StringComparison.Ordinal);
        Assert.Contains("ConfigureForDevelopment() has no effect here", ex.Message, StringComparison.Ordinal);
        Assert.Contains(environment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_environments_keep_their_own_authorization()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization();
        builder.Services.AddA2A(a => a.AddAgentExecutor<ScenarioAgent>().AddAgentCard(TestAgentHost.DefaultCard())
            .ConfigureForDevelopment());
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapA2A("/a2a").RequireAuthorization();
        await app.StartAsync();
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<A2AServerOptions>>().Value;
        Assert.False(options.AllowAnonymous);
        Assert.False(options.TreatMissingVersionAsCurrent);
        Assert.NotEqual(true, options.IncludeExceptionDetails); // null: the Development-only default
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("A2A-Version", "1.0");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/a2a/tasks")).StatusCode);
    }
}
