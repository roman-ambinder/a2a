using Microsoft.AspNetCore.Http;
using A2A.Client;
using A2A.Client.Credentials;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace A2A.Tests.Credentials;

public class CredentialDiTests
{
    private static Dictionary<string, SecurityScheme> Key() => new() { ["key"] = SecurityScheme.ApiKey("X-Key") };
    private static List<SecurityRequirement> KeyReq() => [Cards.Req(("key", []))];

    /// <summary>An origin that serves a card with the given schemes and answers /a2a with a task.</summary>
    private static Task<TestOrigin> Serve(Dictionary<string, SecurityScheme> schemes, List<SecurityRequirement> requirements,
        Func<HttpContext, Task<bool>>? extra = null) =>
        TestOrigin.StartAsync(async (context, origin) =>
        {
            if (extra is not null && await extra(context)) return;
            if (context.Request.Path == "/.well-known/agent-card.json")
                await TestOrigin.WriteCard(context, TestOrigin.Card(origin.Url, schemes, requirements));
            else if (context.Request.Path == "/a2a")
                await TestOrigin.WriteJson(context, TestOrigin.RpcOk);
            else context.Response.StatusCode = 404;
        });

    private static ServiceProvider Build(TestOrigin origin, Action<IHttpClientBuilder> configure, Action<A2ANamedClientOptions>? options = null)
    {
        var services = new ServiceCollection();
        var builder = services.AddA2AClient("agent", o =>
        {
            o.AgentUrl = origin.Url;
            o.Client.MaxRetries = 0;
            options?.Invoke(o);
        });
        configure(builder);
        return services.BuildServiceProvider();
    }

    private static async Task<A2AClient> Get(ServiceProvider sp) =>
        await sp.GetRequiredService<IA2AClientFactory>().GetClientAsync("agent");

    [Fact]
    public async Task OAuth_end_to_end_card_uncredentialed_call_bearer_and_token_reused()
    {
        var tokens = 0;
        await using var origin = await TestOrigin.StartAsync(async (context, o) =>
        {
            if (context.Request.Path == "/token")
            {
                Interlocked.Increment(ref tokens);
                await TestOrigin.WriteJson(context, """{"access_token":"tok1","token_type":"Bearer","expires_in":3600}""");
            }
            else if (context.Request.Path == "/.well-known/agent-card.json")
            {
                var s = new Dictionary<string, SecurityScheme>
                {
                    ["oauth"] = new OAuth2Scheme(new OAuth2SecurityScheme { Flows = new OAuthFlows { ClientCredentials = new ClientCredentialsOAuthFlow { TokenUrl = o.Uri("/token").ToString() } } }),
                };
                await TestOrigin.WriteCard(context, TestOrigin.Card(o.Url, s, [Cards.Req(("oauth", []))]));
            }
            else await TestOrigin.WriteJson(context, TestOrigin.RpcOk);
        });
        using var sp = Build(origin, b => b.AddOAuth2ClientCredentials(o => { o.ClientId = "id"; o.ClientSecret = "secret"; }));

        var client = await Get(sp);
        await client.SendMessageAsync("hi");
        await client.SendMessageAsync("again");

        Assert.Null(origin.At("/.well-known/agent-card.json").Single().Header("Authorization"));
        Assert.Equal(1, tokens);
        var calls = origin.At("/a2a").ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal("Bearer tok1", c.Header("Authorization")));
    }

    private sealed class CustomProvider : IA2ACredentialProvider
    {
        public bool CanHandle(A2ACredentialContext context) => context.SchemeName == "key";

        public ValueTask ApplyAsync(HttpRequestMessage request, A2ACredentialContext context, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation("X-Custom", "custom-value");
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Custom_provider_is_used_through_AddCredentialProvider()
    {
        await using var origin = await Serve(Key(), KeyReq());
        using var sp = Build(origin, b => b.AddCredentialProvider(_ => new CustomProvider()));

        await (await Get(sp)).SendMessageAsync("hi");

        Assert.Equal("custom-value", origin.At("/a2a").Single().Header("X-Custom"));
        Assert.Null(origin.At("/.well-known/agent-card.json").Single().Header("X-Custom"));
    }

    [Fact]
    public async Task AddApiKey_fixed_key()
    {
        await using var origin = await Serve(Key(), KeyReq());
        using var sp = Build(origin, b => b.AddApiKey("k-123"));

        await (await Get(sp)).SendMessageAsync("hi");

        Assert.Equal("k-123", origin.At("/a2a").Single().Header("X-Key"));
        Assert.Null(origin.At("/.well-known/agent-card.json").Single().Header("X-Key"));
    }

    [Fact]
    public async Task AddApiKey_delegate_is_called_per_request_with_service_provider()
    {
        await using var origin = await Serve(Key(), KeyReq());
        var n = 0;
        using var sp = Build(origin, b => b.AddApiKey((services, ct) =>
        {
            Assert.NotNull(services.GetService<IA2AClientFactory>());
            return ValueTask.FromResult("k-" + Interlocked.Increment(ref n));
        }));

        var client = await Get(sp);
        await client.SendMessageAsync("a");
        await client.SendMessageAsync("b");

        Assert.Equal(new string?[] { "k-1", "k-2" }, origin.At("/a2a").Select(r => r.Header("X-Key")).ToArray());
    }

    private sealed class RedirectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("never sent");
    }

    [Fact]
    public async Task Redirecting_primary_handler_with_credentials_fails_at_GetClientAsync()
    {
        await using var origin = await Serve(Key(), KeyReq());
        using var sp = Build(origin, b => b.AddApiKey("k")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = true }));

        await Assert.ThrowsAsync<ArgumentException>(() => Get(sp));
    }

    private sealed class MarkerHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation("X-Marker", "m");
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task Default_headers_and_user_handlers_still_apply_on_the_authenticated_client()
    {
        await using var origin = await Serve(Key(), KeyReq());
        using var sp = Build(origin, b => b.AddApiKey("k")
            .ConfigureHttpClient(c => c.DefaultRequestHeaders.TryAddWithoutValidation("X-Default", "d"))
            .AddHttpMessageHandler(() => new MarkerHandler()));

        await (await Get(sp)).SendMessageAsync("hi");

        var call = origin.At("/a2a").Single();
        Assert.Equal("d", call.Header("X-Default"));
        Assert.Equal("m", call.Header("X-Marker"));
        Assert.Equal("k", call.Header("X-Key"));
    }

    [Fact]
    public void Builder_not_from_AddA2AClient_throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("plain");

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddApiKey("k"));
        Assert.Contains("AddA2AClient", ex.Message);
        Assert.Throws<InvalidOperationException>(() => builder.AddCredentialProvider(_ => new CustomProvider()));
        Assert.Throws<InvalidOperationException>(() => builder.AddOAuth2ClientCredentials(o => { }));
        Assert.Throws<InvalidOperationException>(() => builder.AddMutualTls(_ => throw new NotSupportedException()));
    }

    [Fact]
    public async Task AddMutualTls_without_AgentUrl_throws()
    {
        var services = new ServiceCollection();
        services.AddA2AClient("agent", o => { }).AddMutualTls(_ => throw new NotSupportedException());
        using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<System.Net.Http.IHttpMessageHandlerFactory>();
        Assert.Throws<InvalidOperationException>(() => factory.CreateHandler("A2A:agent"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task No_providers_behaviour_is_unchanged()
    {
        await using var origin = await Serve(Key(), KeyReq());
        using var sp = Build(origin, _ => { });

        var client = await Get(sp);
        await client.SendMessageAsync("hi");

        Assert.Null(origin.At("/a2a").Single().Header("X-Key"));
        Assert.Same(client, await Get(sp));
    }

    [Fact]
    public async Task AddBearerToken_sends_on_the_agent_origin()
    {
        await using var origin = await Serve(new(), []);
        using var sp = Build(origin, b => b.AddBearerToken((_, _) => ValueTask.FromResult("bt")));

        await (await Get(sp)).SendMessageAsync("hi");

        Assert.Equal("Bearer bt", origin.At("/a2a").Single().Header("Authorization"));
    }

    [Fact]
    public async Task AddBearerToken_refuses_off_origin_without_calling_the_token_provider()
    {
        await using var origin = await Serve(new(), []);
        await using var other = await Serve(new(), []);
        var calls = 0;
        using var sp = Build(origin, b => b.AddBearerToken((_, _) => { calls++; return ValueTask.FromResult("bt"); }));
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("A2A:agent");

        var ex = await Assert.ThrowsAsync<A2AException>(() => http.GetAsync(other.Uri("/x")));

        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
        Assert.Equal(0, calls);
        Assert.Empty(other.Requests);
    }

    [Fact]
    public async Task AddBearerToken_on_a_plain_builder_is_unguarded()
    {
        await using var other = await Serve(new(), []);
        var services = new ServiceCollection();
        services.AddHttpClient("plain").AddBearerToken((_, _) => ValueTask.FromResult("bt"));
        using var sp = services.BuildServiceProvider();

        await sp.GetRequiredService<IHttpClientFactory>().CreateClient("plain").GetAsync(other.Uri("/x"));

        Assert.Equal("Bearer bt", other.At("/x").Single().Header("Authorization"));
    }
}
