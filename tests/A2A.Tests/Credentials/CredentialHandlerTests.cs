using System.Net;
using System.Text;
using A2A.Client;
using A2A.Client.Credentials;
using static A2A.Tests.Credentials.Cards;

namespace A2A.Tests.Credentials;

public class CredentialHandlerTests
{
    private static A2ACredentialBinding Credentialed(RecordingProvider provider) =>
        A2ACredentialBinding.Create(Resolved(Card(schemes: TwoSchemes(), requirements: [Req(("key", []))])),
            Card().SupportedInterfaces[0], [provider]);

    private static HttpRequestMessage Get(string url) => new(HttpMethod.Get, url);

    [Fact]
    public async Task Handler_applies_on_every_send()
    {
        var provider = new RecordingProvider("p");
        var inner = new FakeHandler();
        using var http = new HttpClient(new A2ACredentialHandler(Credentialed(provider), inner));

        await http.SendAsync(Get("https://agent.example/a2a"));
        await http.SendAsync(Get("https://agent.example/a2a"));

        Assert.Equal(2, provider.Applied.Count);
        Assert.All(inner.Requests, r => Assert.Equal("p", r.Headers.GetValues("X-Cred").Single()));
    }

    [Fact]
    public async Task Anonymous_binding_passes_through_any_origin()
    {
        var binding = A2ACredentialBinding.Create(Resolved(Card()), Card().SupportedInterfaces[0], [new RecordingProvider("p")]);
        var inner = new FakeHandler();
        using var http = new HttpClient(new A2ACredentialHandler(binding, inner));
        await http.SendAsync(Get("https://elsewhere.example/x"));
        Assert.Single(inner.Requests);
        Assert.False(inner.Requests.Single().Headers.Contains("X-Cred"));
    }

    [Theory]
    [InlineData("https://other.example/a2a")]
    [InlineData("https://agent.example:8443/a2a")]
    [InlineData("http://agent.example/a2a")]
    public async Task Handler_refuses_another_origin_without_calling_provider_or_inner(string url)
    {
        var provider = new RecordingProvider("p");
        var inner = new FakeHandler();
        using var http = new HttpClient(new A2ACredentialHandler(Credentialed(provider), inner));

        var ex = await Assert.ThrowsAsync<A2AException>(() => http.SendAsync(Get(url)));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
        Assert.Empty(provider.Applied);
        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task Handler_refuses_a_provider_that_changes_the_request_uri()
    {
        var provider = new RecordingProvider("p", apply: r => { r.RequestUri = new Uri("https://agent.example/a2a?key=1"); return Task.CompletedTask; });
        var inner = new FakeHandler();
        using var http = new HttpClient(new A2ACredentialHandler(Credentialed(provider), inner));

        await Assert.ThrowsAsync<InvalidOperationException>(() => http.SendAsync(Get("https://agent.example/a2a")));
        Assert.Empty(inner.Requests);
    }

    private sealed class Passthrough(HttpMessageHandler inner) : DelegatingHandler(inner);
    private sealed class Unknown : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage());
    }

    [Fact]
    public void EnsureNoAutoRedirect_refuses_following_handlers_and_accepts_the_rest()
    {
        Assert.Throws<ArgumentException>(() => A2ACredentialHandler.EnsureNoAutoRedirect(new SocketsHttpHandler()));
        Assert.Throws<ArgumentException>(() => A2ACredentialHandler.EnsureNoAutoRedirect(new HttpClientHandler()));
        Assert.Throws<ArgumentException>(() => A2ACredentialHandler.EnsureNoAutoRedirect(new Passthrough(new SocketsHttpHandler())));
        Assert.Throws<ArgumentException>(() => A2ACredentialHandler.EnsureNoAutoRedirect(new Passthrough(new Passthrough(new HttpClientHandler()))));

        A2ACredentialHandler.EnsureNoAutoRedirect(new SocketsHttpHandler { AllowAutoRedirect = false });
        A2ACredentialHandler.EnsureNoAutoRedirect(new HttpClientHandler { AllowAutoRedirect = false });
        A2ACredentialHandler.EnsureNoAutoRedirect(new Passthrough(new SocketsHttpHandler { AllowAutoRedirect = false }));
        A2ACredentialHandler.EnsureNoAutoRedirect(new Unknown());
    }

    [Fact]
    public void Handler_constructor_refuses_a_redirecting_inner_handler()
    {
        var binding = A2ACredentialBinding.Create(Resolved(Card()), Card().SupportedInterfaces[0], [new RecordingProvider("p")]);
        Assert.Throws<ArgumentException>(() => new A2ACredentialHandler(binding, new HttpClientHandler()));
    }

    // ---- no-DI CreateAsync ----

    private static string CardJson(string schemesAndReqs = "") => $$"""
        {"name":"a","description":"d","version":"1",
         "supportedInterfaces":[{"url":"https://agent.example/a2a","protocolBinding":"JSONRPC","protocolVersion":"1.0"}],
         "capabilities":{},"defaultInputModes":["text/plain"],"defaultOutputModes":["text/plain"],"skills":[]{{schemesAndReqs}}}
        """;

    private const string ApiKeyDecl = """
        ,"securitySchemes":{"key":{"apiKeySecurityScheme":{"location":"header","name":"X-Key"}}},"securityRequirements":[{"schemes":{"key":{"list":[]}}}]
        """;

    [Fact]
    public async Task CreateAsync_sends_no_credential_on_the_card_request_and_one_on_the_a2a_request()
    {
        var inner = new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("agent-card.json")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CardJson(ApiKeyDecl), Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"jsonrpc":"2.0","id":"x","result":{"id":"t1","contextId":"c1","status":{"state":"TASK_STATE_WORKING"}}}""", Encoding.UTF8, "application/json") });
        var provider = new RecordingProvider("p", c => c.Scheme is ApiKeyScheme);
        var noAccess = new A2AClientOptions { ValidateCapabilities = false };

        var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example/"), [provider], inner, noAccess);
        await client.GetTaskAsync(new GetTaskRequest { Id = "t1" });

        var requests = inner.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.EndsWith("agent-card.json", requests[0].RequestUri!.AbsolutePath);
        Assert.False(requests[0].Headers.Contains("X-Cred"));
        Assert.Equal("p", requests[1].Headers.GetValues("X-Cred").Single());
    }

    [Fact]
    public async Task CreateAsync_refuses_a_redirecting_handler_before_any_request()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            A2AClientFactory.CreateAsync(new Uri("https://agent.example/"), [new RecordingProvider("p")], new HttpClientHandler()));
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task CreateAsync_with_no_providers_throws()
    {
        var inner = new FakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            A2AClientFactory.CreateAsync(new Uri("https://agent.example/"), Array.Empty<IA2ACredentialProvider>(), inner));
        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task CreateAsync_throws_the_same_error_as_Create_when_no_interface_fits()
    {
        var inner = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CardJson(), Encoding.UTF8, "application/json") });
        var opts = new A2AClientOptions { SupportedBindings = [ProtocolBindings.HttpJson] };
        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            A2AClientFactory.CreateAsync(new Uri("https://agent.example/"), [new RecordingProvider("p")], inner, opts));
        var viaCreate = Assert.Throws<A2AException>(() => A2AClientFactory.Create(Card(), new HttpClient(inner), opts));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
        Assert.Equal(viaCreate.Message.Replace("'agent'", "'a'"), ex.Message);
    }

    [Fact]
    public async Task Handler_refuses_a_sigma_lookalike_origin()
    {
        Assert.NotEqual(new Uri("https://σagent.example/").IdnHost, new Uri("https://ςagent.example/a2a").IdnHost);
        var card = Card("https://σagent.example/a2a", schemes: TwoSchemes(), requirements: [Req(("key", []))]);
        var binding = A2ACredentialBinding.Create(Resolved(card, "https://σagent.example/.well-known/agent-card.json"),
            card.SupportedInterfaces[0], [new RecordingProvider("p")]);
        var provider = new RecordingProvider("p");
        var inner = new FakeHandler();
        using var http = new HttpClient(new A2ACredentialHandler(binding, inner));

        var ex = await Assert.ThrowsAsync<A2AException>(() => http.SendAsync(Get("https://ςagent.example/a2a")));

        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
        Assert.Empty(inner.Requests);
        Assert.Empty(provider.Applied);
        await http.SendAsync(Get("https://σagent.example/a2a")); // the real origin still works
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task Handler_message_has_no_user_info()
    {
        var provider = new RecordingProvider("p");
        using var http = new HttpClient(new A2ACredentialHandler(Credentialed(provider), new FakeHandler()));
        var ex = await Assert.ThrowsAsync<A2AException>(() => http.SendAsync(Get("https://user:hunter2@other.example/a2a")));
        Assert.DoesNotContain("hunter2", ex.Message);
        Assert.Contains("other.example", ex.Message);
    }
}
