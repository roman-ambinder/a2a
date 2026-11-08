using System.Net;
using System.Reflection;
using System.Text;
using A2A.Client;
using A2A.Client.Delegation;
using A2A.Client.Transports;
using A2A.Delegation;
using A2A.Json;
using A2A.Server;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests;

public class TokenExchangeServerTests
{
    public static TheoryData<string> Bindings => BindingTests.Bindings;

    private sealed class RecordingExchanger(string issued = "exchanged-token") : ITokenExchanger
    {
        public List<TokenExchangeRequest> Requests { get; } = [];

        public ValueTask<TokenExchangeResult> ExchangeAsync(TokenExchangeRequest request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            return new(new TokenExchangeResult { AccessToken = issued });
        }
    }

    /// <summary>Runs <paramref name="body"/> for every message and completes the task.</summary>
    private sealed class ProbeAgent(Func<AgentRequestContext, CancellationToken, Task> body) : IAgentExecutor
    {
        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
        {
            await body(context, cancellationToken);
            await updater.CompleteAsync("done", cancellationToken);
        }
    }

    private static Task<TestAgentHost> StartAsync(IAgentExecutor agent, ITokenExchanger? exchanger) =>
        TestAgentHost.StartAsync(services: s =>
        {
            s.Replace(ServiceDescriptor.Singleton(agent));
            if (exchanger is not null) s.AddSingleton(exchanger);
        });

    private static A2ARequestOptions Bearer(string value) =>
        new() { Headers = { ["Authorization"] = value } };

    [Theory, MemberData(nameof(Bindings))]
    public async Task Without_an_exchanger_the_token_is_not_captured(string binding)
    {
        bool? can = null;
        Type? error = null;
        var agent = new ProbeAgent(async (ctx, ct) =>
        {
            can = ctx.CanExchangeToken;
            try { await ctx.ExchangeTokenAsync("api://b", null, ct); }
            catch (Exception e) { error = e.GetType(); }
        });
        await using var host = await StartAsync(agent, null);
        await host.Client(binding).SendMessageAsync(Message.FromUser("hi"), options: Bearer("Bearer secret-1"));
        await host.WaitIdleAsync();
        Assert.False(can);
        Assert.Equal(typeof(InvalidOperationException), error);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Exchanger_receives_the_inbound_token_and_the_target(string binding)
    {
        var exchanger = new RecordingExchanger();
        bool? can = null;
        var agent = new ProbeAgent(async (ctx, ct) =>
        {
            can = ctx.CanExchangeToken;
            await ctx.ExchangeTokenAsync("api://b", ["read", "write"], ct);
        });
        await using var host = await StartAsync(agent, exchanger);
        await host.Client(binding).SendMessageAsync(Message.FromUser("hi"), options: Bearer("bearer  caller-token "));
        await host.WaitIdleAsync();
        Assert.True(can);
        var request = Assert.Single(exchanger.Requests);
        Assert.Equal("caller-token", request.SubjectToken);
        Assert.Equal("api://b", request.Audience);
        Assert.Equal(["read", "write"], request.Scopes);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Empty_audience_is_refused(string binding)
    {
        var exchanger = new RecordingExchanger();
        Exception? error = null;
        var agent = new ProbeAgent(async (ctx, ct) =>
        {
            try { await ctx.ExchangeTokenAsync("", null, ct); }
            catch (Exception e) { error = e; }
        });
        await using var host = await StartAsync(agent, exchanger);
        await host.Client(binding).SendMessageAsync(Message.FromUser("hi"), options: Bearer("Bearer t"));
        await host.WaitIdleAsync();
        Assert.IsType<ArgumentException>(error);
        Assert.Empty(exchanger.Requests);
    }

    [Theory]
    [InlineData("Basic abc")]
    [InlineData("Bearer")]
    [InlineData("Bearer   ")]
    [InlineData("Bearerx abc")]
    public async Task Nothing_is_captured_for(string header)
    {
        bool? can = null;
        var agent = new ProbeAgent((ctx, _) => { can = ctx.CanExchangeToken; return Task.CompletedTask; });
        await using var host = await StartAsync(agent, new RecordingExchanger());
        await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync(Message.FromUser("hi"), options: Bearer(header));
        await host.WaitIdleAsync();
        Assert.False(can);
    }

    [Fact]
    public async Task Nothing_is_captured_for_an_unauthenticated_caller()
    {
        bool? can = null;
        var agent = new ProbeAgent((ctx, _) => { can = ctx.CanExchangeToken; return Task.CompletedTask; });
        await using var host = await TestAgentHost.StartAsync(o => o.AllowAnonymous = true, requireAuth: false, services: s =>
        {
            s.Replace(ServiceDescriptor.Singleton<IAgentExecutor>(agent));
            s.AddSingleton<ITokenExchanger>(new RecordingExchanger());
        });
        await host.Client(ProtocolBindings.JsonRpc, user: null).SendMessageAsync(Message.FromUser("hi"), options: Bearer("Bearer t"));
        await host.WaitIdleAsync();
        Assert.False(can);
    }

    [Fact]
    public void No_public_getter_returns_the_inbound_token()
    {
        var property = typeof(ServerCallContext).GetProperty("InboundBearerToken")!;
        Assert.Null(property.GetGetMethod(nonPublic: false));
        var names = typeof(AgentRequestContext).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);
        Assert.DoesNotContain("InboundBearerToken", names);
        Assert.DoesNotContain("SubjectToken", names);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Inbound_token_is_not_stored(string binding)
    {
        const string secret = "unique-inbound-secret-7f3a9c";
        var agent = new ProbeAgent(async (ctx, ct) => await ctx.ExchangeTokenAsync("api://b", null, ct));
        await using var host = await StartAsync(agent, new RecordingExchanger());
        var task = (await host.Client(binding).SendMessageAsync(Message.FromUser("hi"), options: Bearer("Bearer " + secret))).RequireTask();
        await host.WaitIdleAsync();

        var stored = await host.Client(binding).GetTaskAsync(task.Id);
        Assert.DoesNotContain(secret, A2AJson.Serialize(stored), StringComparison.Ordinal);

        var events = host.App.Services.GetRequiredService<EventStore>();
        var count = 0;
        await foreach (var e in events.ReadAfterAsync(new TaskKey("", "alice", task.Id), 0))
        {
            count++;
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(e.Json.Span), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, A2AJson.Serialize(e.Event), StringComparison.Ordinal);
        }
        Assert.True(count > 0);
    }

    private static A2AClient Downstream(RecordingHandler handler)
    {
        var options = new A2AClientOptions { MaxRetries = 0 };
        return new A2AClient(new JsonRpcTransport(new HttpClient(handler), new Uri("http://downstream.test/a2a"), options), options);
    }

    private static async Task CallDownstreamAsync(A2AClient client, A2ARequestOptions? options, CancellationToken ct)
    {
        try { await client.SendMessageAsync(Message.FromUser("next"), options: options, cancellationToken: ct); }
        catch (Exception) { /* the stub answers 400: only the request it saw matters */ }
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Intermediary_does_not_forward_the_callers_token(string binding)
    {
        var downstream = new RecordingHandler { Status = _ => HttpStatusCode.BadRequest };
        var agent = new ProbeAgent((ctx, ct) => CallDownstreamAsync(Downstream(downstream), null, ct));
        await using var host = await StartAsync(agent, new RecordingExchanger());
        await host.Client(binding).SendMessageAsync(Message.FromUser("hi"), options: Bearer("Bearer caller-secret"));
        await host.WaitIdleAsync();
        var call = Assert.Single(downstream.Calls);
        Assert.Null(call.Request.Headers.Authorization);
        Assert.False(call.Request.Headers.Contains("Authorization"));
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Exchanged_token_is_what_reaches_the_next_agent(string binding)
    {
        var downstream = new RecordingHandler { Status = _ => HttpStatusCode.BadRequest };
        var agent = new ProbeAgent(async (ctx, ct) =>
        {
            var result = await ctx.ExchangeTokenAsync("api://b", null, ct);
            await CallDownstreamAsync(Downstream(downstream), new A2ARequestOptions().UseBearerToken(result), ct);
        });
        await using var host = await StartAsync(agent, new RecordingExchanger("exchanged-xyz"));
        await host.Client(binding).SendMessageAsync(Message.FromUser("hi"), options: Bearer("Bearer caller-secret"));
        await host.WaitIdleAsync();
        var call = Assert.Single(downstream.Calls);
        Assert.Equal("Bearer exchanged-xyz", call.Request.Headers.Authorization!.ToString());
        Assert.DoesNotContain("caller-secret", call.Request.Headers.Authorization.ToString());
    }
}
