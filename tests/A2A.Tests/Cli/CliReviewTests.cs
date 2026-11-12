using System.Text.Json;
using A2A.Cli;
using A2A.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using static A2A.Tests.Cli.CliTestSupport;

namespace A2A.Tests.Cli;

/// <summary>Endpoint checks, card fetch errors, Ctrl+C on standard input and error text of the tool.</summary>
public sealed class CliReviewTests
{
    // A public IP literal, so the endpoint check needs no DNS. The test server answers for any host.
    private const string PublicAgent = "https://93.184.215.14/";

    private static Action<WebApplication> ServeCard(AgentCard card, Action? onInternal = null) => app =>
    {
        var json = A2AJson.Serialize(card);
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/internal")) onInternal?.Invoke();
            if (ctx.Request.Path != "/.well-known/agent-card.json") { await next(ctx); return; }
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(json);
        });
    };

    private static AgentCard CardNaming(string url)
    {
        var card = TestAgentHost.DefaultCard();
        card.SupportedInterfaces =
        [
            new AgentInterface { Url = url, ProtocolBinding = ProtocolBindings.JsonRpc },
            new AgentInterface { Url = url, ProtocolBinding = ProtocolBindings.HttpJson },
        ];
        return card;
    }

    [Fact]
    public async Task A_public_card_that_names_a_private_endpoint_is_refused_before_any_call()
    {
        var hits = 0;
        await using var host = await StartAsync(pipeline: ServeCard(CardNaming("http://10.1.2.3:8080/internal"), () => Interlocked.Increment(ref hits)));

        var run = await RunAsync(host, ["send", "--agent", PublicAgent, "hello"], addAgent: false);

        Assert.True(run.Code == ExitCodes.Protocol, run.Shown);
        Assert.Contains("endpoint policy refused", run.Error);
        Assert.Contains("10.1.2.3", run.Error);
        Assert.Equal("", run.Out);
        Assert.Equal(0, hits);

        var json = await RunAsync(host, ["send", "--agent", PublicAgent, "--json", "hello"], addAgent: false);
        using var doc = JsonDocument.Parse(json.Error);
        Assert.Equal("INVALID_AGENT_RESPONSE", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, hits);
    }

    [Fact]
    public async Task A_private_card_may_still_name_a_private_endpoint()
    {
        // The card comes from loopback, so it may name loopback endpoints (the default policy, as for A2AClient.ConnectAsync).
        await using var host = await StartAsync();
        var run = await Run(host, "send", "hello");
        Assert.True(run.Code == 0, run.Shown);
    }

    [Fact]
    public async Task Card_command_does_not_judge_endpoints()
    {
        await using var host = await StartAsync(pipeline: ServeCard(CardNaming("http://10.1.2.3:8080/internal")));
        var run = await RunAsync(host, ["card", "--agent", PublicAgent], addAgent: false);
        Assert.True(run.Code == 0, run.Shown);
    }

    private static Action<WebApplication> CardStatus(int status) => app =>
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path != "/.well-known/agent-card.json") { await next(ctx); return; }
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsync("no card here");
        });

    [Theory]
    [InlineData("card")]
    [InlineData("send")]
    public async Task A_missing_agent_card_is_a_connection_error_not_task_not_found(string command)
    {
        await using var host = await StartAsync(pipeline: CardStatus(404));
        string[] args = command == "send" ? [command, "hello"] : [command];

        var run = await RunAsync(host, args);
        Assert.True(run.Code == ExitCodes.Network, run.Shown);
        Assert.Contains("Agent Card", run.Error);
        Assert.Contains("HTTP 404", run.Error);

        var json = await RunAsync(host, [.. args, "--json"]);
        Assert.Equal(ExitCodes.Network, json.Code);
        using var doc = JsonDocument.Parse(json.Error);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal(ExitCodes.Network, error.GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, error.GetProperty("code").ValueKind);
    }

    [Fact]
    public async Task A_server_error_on_the_agent_card_is_a_connection_error()
    {
        await using var host = await StartAsync(pipeline: CardStatus(503));
        var run = await Run(host, "card");
        Assert.True(run.Code == ExitCodes.Network, run.Shown);
    }

    [Fact]
    public async Task A_401_on_the_agent_card_is_still_an_auth_error()
    {
        await using var host = await StartAsync(pipeline: CardStatus(401));
        var run = await Run(host, "card");
        Assert.True(run.Code == ExitCodes.Auth, run.Shown);
    }

    [Fact]
    public async Task Get_unknown_task_still_exits_5()
    {
        await using var host = await StartAsync();
        var run = await Run(host, "get", "no-such-task");
        Assert.True(run.Code == ExitCodes.NotFound, run.Shown);
    }
}
