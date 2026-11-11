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
            .UseDevelopmentDefaults());
        var app = builder.Build();
        app.UseA2A();
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
    public async Task Other_environments_refuse_to_start(string environment)
    {
        // The options are first read while the endpoints are mapped, so the failure comes before StartAsync.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var app = Build(environment);
            await app.StartAsync();
        });
        Assert.Contains("UseDevelopmentDefaults()", ex.Message, StringComparison.Ordinal);
        Assert.Contains(environment, ex.Message, StringComparison.Ordinal);
    }
}
