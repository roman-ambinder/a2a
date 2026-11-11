using A2A.Client;

namespace A2A.Tests;

public class CreateForEndpointTests : IAsyncLifetime
{
    private TestAgentHost _host = null!;

    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    public async ValueTask InitializeAsync() => _host = await TestAgentHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Calls_the_endpoint_without_a_card(string binding)
    {
        var client = A2AClientFactory.CreateForEndpoint(new Uri(TestAgentHost.BaseUrl), _host.Http("alice"), binding);
        Assert.Null(client.AgentCard);
        Assert.Equal("hi there", await client.AskAsync("hello"));
        var chunks = new List<string>();
        await foreach (var chunk in client.StreamTextAsync("task")) chunks.Add(chunk); // no card: no capability check
        Assert.Equal(["part1", "part2"], chunks);
    }

    [Fact]
    public void Defaults_to_json_rpc() =>
        Assert.NotNull(A2AClientFactory.CreateForEndpoint(new Uri(TestAgentHost.BaseUrl), _host.Http("alice")));

    [Theory]
    [InlineData("ftp://agent.example.com/a2a")]
    [InlineData("/a2a")]
    public void Refuses_non_http_endpoints(string url) =>
        Assert.Throws<ArgumentException>(() =>
            A2AClientFactory.CreateForEndpoint(new Uri(url, UriKind.RelativeOrAbsolute), new HttpClient()));

    [Fact]
    public void Unknown_binding_is_unsupported()
    {
        var ex = Assert.Throws<A2AException>(() =>
            A2AClientFactory.CreateForEndpoint(new Uri(TestAgentHost.BaseUrl), new HttpClient(), "CARRIER_PIGEON"));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
    }
}
