using System.Net;
using System.Security.Claims;
using System.Reflection;
using System.Text;
using A2A.Client;
using A2A.Client.Delegation;
using A2A.Client.Transports;
using A2A.Delegation;
using A2A.Json;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;

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

    /// <param name="bearerScheme">
    ///     Registers <see cref="TestBearerHandler" /> as the bearer scheme (<c>AddBearerAuthentication</c>). The caller is
    ///     still authenticated by the test scheme (<c>X-Test-User</c>).
    /// </param>
    /// <param name="trustedBearerSchemes">Names put in <see cref="A2AHttpOptions.TrustedBearerSchemes" />.</param>
    private static Task<TestAgentHost> StartAsync(IAgentExecutor agent, ITokenExchanger? exchanger, bool bearerScheme = true,
        string[]? trustedBearerSchemes = null, Action<IServiceCollection>? services = null) =>
        TestAgentHost.StartAsync(configureHttp: o => o.TrustedBearerSchemes.UnionWith(trustedBearerSchemes ?? []),
            services: s =>
        {
            services?.Invoke(s);
            s.Replace(ServiceDescriptor.Singleton(agent));
            if (exchanger is not null) s.AddSingleton(exchanger);
            if (!bearerScheme) return;
            s.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestBearerHandler>(TestBearerHandler.SchemeName, null);
            s.AddA2AServer().AddBearerAuthentication(TestBearerHandler.SchemeName);
        });

    private static async Task<bool?> CanExchangeAsync(string binding, bool bearerScheme, string header, string? tokenSub = null)
    {
        bool? can = null;
        var agent = new ProbeAgent((ctx, _) => { can = ctx.CanExchangeToken; return Task.CompletedTask; });
        await using var host = await StartAsync(agent, new RecordingExchanger(), bearerScheme);
        var options = Bearer(header);
        if (tokenSub is not null) options.Headers["X-Token-Sub"] = tokenSub;
        await host.Client(binding).SendMessageAsync(Message.FromUser("hi"), options: options);
        await host.WaitIdleAsync();
        return can;
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_token_no_bearer_scheme_validated_is_not_kept(string binding)
    {
        // The caller is authenticated by another scheme (like an API key); nothing checked the Authorization header.
        Assert.False(await CanExchangeAsync(binding, bearerScheme: false, "Bearer unchecked-token"));
    }

    /// <summary>
    ///     Runs one call whose caller the test scheme (<c>X-Test-User</c>, not a bearer scheme) authenticates, with no
    ///     bearer scheme registered, and returns what the exchanger received.
    /// </summary>
    private static async Task<(bool? Can, List<TokenExchangeRequest> Requests)> ExchangeWithTrustedAsync(string binding,
        string[]? trusted, string header = "Bearer gateway-checked-token", Action<IServiceCollection>? services = null,
        string? tokenSub = null)
    {
        bool? can = null;
        var exchanger = new RecordingExchanger();
        var agent = new ProbeAgent(async (ctx, ct) =>
        {
            can = ctx.CanExchangeToken;
            if (can == true) await ctx.ExchangeTokenAsync("api://b", null, ct);
        });
        await using var host = await StartAsync(agent, exchanger, bearerScheme: false, trusted, services);
        var options = Bearer(header);
        if (tokenSub is not null) options.Headers["X-Token-Sub"] = tokenSub;
        await host.Client(binding).SendMessageAsync(Message.FromUser("hi"), options: options);
        await host.WaitIdleAsync();
        return (can, exchanger.Requests);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_trusted_scheme_that_authenticated_the_caller_has_its_bearer_header_exchanged(string binding)
    {
        // The test scheme stands for an API key or gateway scheme that validated the token upstream.
        var (can, requests) = await ExchangeWithTrustedAsync(binding, [TestAuthHandler.SchemeName]);
        Assert.True(can);
        Assert.Equal("gateway-checked-token", Assert.Single(requests).SubjectToken);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Without_trusted_schemes_a_non_bearer_scheme_does_not_forward(string binding)
    {
        var (can, requests) = await ExchangeWithTrustedAsync(binding, null);
        Assert.False(can);
        Assert.Empty(requests);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_trusted_scheme_that_did_not_authenticate_the_caller_does_not_forward(string binding)
    {
        // "Other" is listed, but the caller was authenticated by the test scheme, which is not.
        var (can, requests) = await ExchangeWithTrustedAsync(binding, [NoResultHandler.SchemeName],
            services: s => s.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,
                NoResultHandler>(NoResultHandler.SchemeName, null));
        Assert.False(can);
        Assert.Empty(requests);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_trusted_scheme_whose_user_is_not_the_caller_does_not_forward(string binding)
    {
        // "Gateway" is listed and succeeds, but for Bob; Alice is the caller.
        static void Gateway(IServiceCollection s) => s.AddAuthentication()
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestBearerHandler>("Gateway", null);
        var (can, requests) = await ExchangeWithTrustedAsync(binding, ["Gateway"], services: Gateway, tokenSub: "bob");
        Assert.False(can);
        Assert.Empty(requests);
        (can, requests) = await ExchangeWithTrustedAsync(binding, ["Gateway"], services: Gateway, tokenSub: "alice");
        Assert.True(can);
        Assert.Single(requests);
    }

    [Theory]
    [InlineData("Basic abc")]
    [InlineData("Bearer a b")]
    [InlineData("Bearer a,b")]
    public async Task A_trusted_scheme_still_needs_one_well_formed_bearer_header(string header)
    {
        var (can, requests) = await ExchangeWithTrustedAsync(ProtocolBindings.JsonRpc, [TestAuthHandler.SchemeName], header);
        Assert.False(can);
        Assert.Empty(requests);
    }

    [Fact]
    public void A_trusted_scheme_does_not_forward_two_Authorization_headers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        services.Configure<A2AHttpOptions>(o => o.TrustedBearerSchemes.Add(TestAuthHandler.SchemeName));
        services.AddSingleton<ITenantResolver, RouteTenantResolver>();
        services.AddSingleton<IPrincipalResolver, DefaultPrincipalResolver>();
        services.AddSingleton<ITokenExchanger>(new RecordingExchanger());
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "test"));
        http.Request.Headers["X-Test-User"] = "alice";
        http.Request.Headers.Authorization = new StringValues(["Bearer aaa", "Bearer bbb"]);
        Assert.Null(HttpPlumbing.CreateCallContext(http, null).InboundBearerToken);

        http.Request.Headers.Authorization = "Bearer aaa";
        Assert.Equal("aaa", HttpPlumbing.CreateCallContext(http, null).InboundBearerToken);
    }

    [Fact]
    public async Task A_trusted_scheme_that_is_not_registered_stops_the_start()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
            new ProbeAgent((_, _) => Task.CompletedTask), new RecordingExchanger(), bearerScheme: false, ["Missing"]));
        Assert.Contains("'Missing'", error.Message, StringComparison.Ordinal);
        Assert.Contains("TrustedBearerSchemes", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A scheme that never authenticates anyone.</summary>
    private sealed class NoResultHandler(Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Other";

        protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_token_the_bearer_scheme_rejected_is_not_kept(string binding)
    {
        Assert.False(await CanExchangeAsync(binding, bearerScheme: true, "Bearer rejected-token"));
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_valid_token_of_another_subject_is_not_kept(string binding)
    {
        // Alice is the caller; the token the bearer scheme validated is Bob's.
        Assert.False(await CanExchangeAsync(binding, bearerScheme: true, "Bearer bobs-token", tokenSub: "bob"));
        Assert.True(await CanExchangeAsync(binding, bearerScheme: true, "Bearer alices-token", tokenSub: "alice"));
    }

    private static A2ARequestOptions Bearer(string value) =>
        new() { Headers = { ["Authorization"] = value } };

    [Theory, MemberData(nameof(Bindings))]
    public async Task Without_an_exchanger_the_agent_cannot_exchange(string binding)
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

    private static readonly Microsoft.IdentityModel.Tokens.SymmetricSecurityKey JwtKey = new(new byte[32]);

    private static string Jwt(string sub, Microsoft.IdentityModel.Tokens.SecurityKey key) =>
        new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", sub)]),
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256),
        });

    [Theory]
    [InlineData(true, true)]   // the endpoint's policy authenticates with the JWT scheme: the usual JWT host
    [InlineData(false, true)]  // another scheme authenticated the caller; the JWT scheme validates the header on demand
    [InlineData(true, false)]  // a token the JWT scheme rejects (wrong key) is never kept
    [InlineData(false, false)]
    public async Task An_AddJwtBearer_scheme_validates_the_token_it_keeps(bool jwtPolicy, bool validToken)
    {
        bool? can = null;
        var agent = new ProbeAgent((ctx, _) => { can = ctx.CanExchangeToken; return Task.CompletedTask; });
        await using var host = await TestAgentHost.StartAsync(
            services: s =>
            {
                s.Replace(ServiceDescriptor.Singleton<IAgentExecutor>(agent));
                s.AddSingleton<ITokenExchanger>(new RecordingExchanger());
                s.AddAuthentication().AddJwtBearer(o =>
                {
                    o.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                    {
                        IssuerSigningKey = JwtKey, ValidateIssuer = false, ValidateAudience = false, ValidateLifetime = false,
                    };
                });
            },
            configureRoutes: a2a =>
            {
                if (jwtPolicy) a2a.RequireAuthorization(p => p.AddAuthenticationSchemes("Bearer").RequireAuthenticatedUser());
            });
        var key = validToken ? JwtKey : new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Enumerable.Repeat((byte)7, 32).ToArray());
        try
        {
            await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync(Message.FromUser("hi"), options: Bearer("Bearer " + Jwt("alice", key)));
        }
        catch (A2AException) when (jwtPolicy && !validToken)
        {
            return; // the endpoint refused the call: nothing ran, nothing was kept
        }
        await host.WaitIdleAsync();
        Assert.Equal(validToken, can);
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
    [InlineData("Bearer a b")]
    [InlineData("Bearer a,b")]
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
    public void Nothing_is_captured_for_two_Authorization_headers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestBearerHandler>(TestBearerHandler.SchemeName, null);
        services.AddA2AServer().AddBearerAuthentication(TestBearerHandler.SchemeName);
        services.AddSingleton<ITenantResolver, RouteTenantResolver>();
        services.AddSingleton<IPrincipalResolver, DefaultPrincipalResolver>();
        services.AddSingleton<ITokenExchanger>(new RecordingExchanger());
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice")], "test"));
        http.Request.Headers["X-Test-User"] = "alice";
        http.Request.Headers.Authorization = new StringValues(["Bearer aaa", "Bearer bbb"]);
        Assert.Null(HttpPlumbing.CreateCallContext(http, null).InboundBearerToken);

        http.Request.Headers.Authorization = "Bearer aaa";
        Assert.Equal("aaa", HttpPlumbing.CreateCallContext(http, null).InboundBearerToken);
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
            await CallDownstreamAsync(Downstream(downstream), new A2ARequestOptions().UseBearerToken(result, new Uri("http://downstream.test/a2a")), ct);
        });
        await using var host = await StartAsync(agent, new RecordingExchanger("exchanged-xyz"));
        await host.Client(binding).SendMessageAsync(Message.FromUser("hi"), options: Bearer("Bearer caller-secret"));
        await host.WaitIdleAsync();
        var call = Assert.Single(downstream.Calls);
        Assert.Equal("Bearer exchanged-xyz", call.Request.Headers.Authorization!.ToString());
        Assert.DoesNotContain("caller-secret", call.Request.Headers.Authorization.ToString());
    }
}
