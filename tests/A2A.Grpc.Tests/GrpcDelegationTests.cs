using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Client;
using A2A.Delegation;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Grpc.Tests;

/// <summary>Delegation identity over gRPC: the same answers the HTTP bindings give (see A2A.Tests for the HTTP side).</summary>
public class GrpcDelegationTests
{
    private sealed class RecordingExchanger : ITokenExchanger
    {
        public List<TokenExchangeRequest> Requests { get; } = [];

        public ValueTask<TokenExchangeResult> ExchangeAsync(TokenExchangeRequest request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            return new(new TokenExchangeResult { AccessToken = "exchanged" });
        }
    }

    private static string Hops(int n) =>
        $$"""{"origin":{"sub":"user:alice"},"actors":[{{string.Join(",", Enumerable.Range(1, n).Select(i => $$"""{"sub":"agent:{{i}}"}"""))}}]}""";

    private static Message Msg(string chainJson)
    {
        using var doc = JsonDocument.Parse(chainJson);
        return new Message
        {
            MessageId = Guid.NewGuid().ToString(),
            Role = Role.User,
            Parts = [Part.FromText("work")],
            Metadata = new() { [ActorChain.MetadataKey] = doc.RootElement.Clone() },
        };
    }

    [Fact]
    public async Task Chain_over_the_limit_is_refused_and_the_agent_does_not_run()
    {
        var runs = 0;
        await using var host = await GrpcHost.StartAsync(server: b => b.AddAgentExecutor((_, updater, ct) =>
        {
            Interlocked.Increment(ref runs);
            return updater.CompleteAsync("done", ct).AsTask();
        }));
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client().SendMessageAsync(Msg(Hops(9))));
        Assert.Equal(A2AErrorCode.InvalidRequest, ex.Code);
        Assert.Equal(0, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task Well_formed_chain_is_echoed_on_the_task()
    {
        await using var host = await GrpcHost.StartAsync(server: b => b.AddAgentExecutor((_, updater, ct) => updater.CompleteAsync("done", ct).AsTask()));
        const string chain = """{"origin":{"sub":"user:alice"},"actors":[{"sub":"agent:a"}]}""";
        var client = host.Client();
        var task = Assert.IsType<TaskResponse>(await client.SendMessageAsync(Msg(chain))).Task;
        var stored = await client.GetTaskAsync(task.Id);
        // gRPC carries the chain as a protobuf Struct, so the spacing differs: compare the JSON values.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(chain), JsonNode.Parse(stored.Metadata![ActorChain.MetadataKey].GetRawText())));
    }

    [Fact]
    public async Task Exchanger_receives_the_callers_bearer_token()
    {
        var exchanger = new RecordingExchanger();
        await using var host = await GrpcHost.StartAsync(services: s => s.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, HeaderBearerHandler>(HeaderBearerHandler.SchemeName, null),
            server: b => b
            .AddTokenExchanger(exchanger)
            .AddBearerAuthentication(HeaderBearerHandler.SchemeName)
            .AddAgentExecutor(async (ctx, updater, ct) =>
            {
                await ctx.ExchangeTokenAsync("api://b", ["read"], ct);
                await updater.CompleteAsync("done", ct);
            }));
        var options = new A2ARequestOptions { Headers = { ["Authorization"] = "Bearer grpc-caller-token" } };
        await host.Client().SendMessageAsync(Message.FromUser("hi"), options: options);
        var request = Assert.Single(exchanger.Requests);
        Assert.Equal("grpc-caller-token", request.SubjectToken);
        Assert.Equal("api://b", request.Audience);
        Assert.Equal(["read"], request.Scopes);
    }

    [Fact]
    public async Task A_bearer_header_no_bearer_scheme_validated_is_not_kept()
    {
        var exchanger = new RecordingExchanger();
        bool? can = null;
        await using var host = await GrpcHost.StartAsync(server: b => b
            .AddTokenExchanger(exchanger)
            .AddAgentExecutor(async (ctx, updater, ct) =>
            {
                can = ctx.CanExchangeToken;
                await updater.CompleteAsync("done", ct);
            }));
        var options = new A2ARequestOptions { Headers = { ["Authorization"] = "Bearer unchecked-token" } };
        await host.Client().SendMessageAsync(Message.FromUser("hi"), options: options);
        Assert.False(can);
        Assert.Empty(exchanger.Requests);
    }

    /// <summary>
    ///     One call whose caller the host's test scheme (<c>Test</c>, not a bearer scheme) authenticates, with
    ///     <paramref name="trusted" /> in <see cref="A2AHttpOptions.TrustedBearerSchemes" />.
    /// </summary>
    private static async Task<(bool? Can, List<TokenExchangeRequest> Requests)> ExchangeWithTrustedAsync(params string[] trusted)
    {
        var exchanger = new RecordingExchanger();
        bool? can = null;
        await using var host = await GrpcHost.StartAsync(
            services: s => s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, NoResultHandler>(NoResultHandler.SchemeName, null),
            server: b => b
                .AddTokenExchanger(exchanger)
                .ConfigureHttp(o => o.TrustedBearerSchemes.UnionWith(trusted))
                .AddAgentExecutor(async (ctx, updater, ct) =>
                {
                    can = ctx.CanExchangeToken;
                    if (can == true) await ctx.ExchangeTokenAsync("api://b", null, ct);
                    await updater.CompleteAsync("done", ct);
                }));
        var options = new A2ARequestOptions { Headers = { ["Authorization"] = "Bearer gateway-checked-token" } };
        await host.Client().SendMessageAsync(Message.FromUser("hi"), options: options);
        return (can, exchanger.Requests);
    }

    [Fact]
    public async Task A_trusted_scheme_that_authenticated_the_caller_has_its_bearer_header_exchanged()
    {
        var (can, requests) = await ExchangeWithTrustedAsync("Test");
        Assert.True(can);
        Assert.Equal("gateway-checked-token", Assert.Single(requests).SubjectToken);
    }

    [Fact]
    public async Task Without_trusted_schemes_a_non_bearer_scheme_does_not_forward()
    {
        var (can, requests) = await ExchangeWithTrustedAsync();
        Assert.False(can);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task A_trusted_scheme_that_did_not_authenticate_the_caller_does_not_forward()
    {
        var (can, requests) = await ExchangeWithTrustedAsync(NoResultHandler.SchemeName);
        Assert.False(can);
        Assert.Empty(requests);
    }

    /// <summary>A scheme that never authenticates anyone.</summary>
    private sealed class NoResultHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Other";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    /// <summary>A bearer scheme: any <c>Authorization: Bearer</c> token is valid, its subject is <c>x-test-user</c>.</summary>
    private sealed class HeaderBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestBearer";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(AuthenticateResult.NoResult());
            var id = new ClaimsIdentity([new Claim("sub", Request.Headers["x-test-user"].ToString())], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), SchemeName)));
        }
    }
}
