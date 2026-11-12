using System.Collections.Concurrent;
using System.Net;
using System.Text;
using A2A.Client;

namespace A2A.Tests;

/// <summary>
/// The client asks only for protocol versions it speaks (spec §3.6.2, §3.6.3): the same major, no newer minor than
/// <see cref="A2AClientOptions.ProtocolVersion" />, in the card's order (§8.3.2), and never an older minor with
/// <see cref="A2AClientOptions.RequireExactProtocolVersion" />.
/// </summary>
public sealed class ClientVersionSelectionTests
{
    private const string TaskJson = """{"id":"t1","contextId":"c1","status":{"state":"TASK_STATE_COMPLETED"}}""";

    private static AgentCard Card(params (string Url, string Version)[] interfaces) => new()
    {
        Name = "agent", Description = "d", Version = "1",
        SupportedInterfaces = interfaces
            .Select(i => new AgentInterface { Url = i.Url, ProtocolBinding = ProtocolBindings.HttpJson, ProtocolVersion = i.Version })
            .ToList(),
        Capabilities = new AgentCapabilities(),
        DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"], Skills = [],
    };

    private static A2AClientOptions Options(string version = "1.0", bool exact = false) => new()
    {
        ProtocolVersion = version, RequireExactProtocolVersion = exact, EndpointPolicy = AgentEndpointPolicies.AllowAll, MaxRetries = 0,
    };

    private sealed class Recorder : HttpMessageHandler
    {
        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TaskJson, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task A_1_0_client_skips_a_preferred_1_1_interface_and_sends_1_0()
    {
        // The SDK's own server lists 1.1 first once EnableProtocolV11 is called.
        var card = Card(("http://localhost/v11", "1.1"), ("http://localhost/v10", "1.0"));
        var recorder = new Recorder();
        var client = A2AClientFactory.Create(card, new HttpClient(recorder), Options());

        await client.GetTaskAsync("t1");

        var request = Assert.Single(recorder.Requests);
        Assert.StartsWith("/v10/", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("1.0", Assert.Single(request.Headers.GetValues(A2AProtocol.Headers.Version)));
    }

    [Fact]
    public void A_newer_minor_version_is_never_selected()
    {
        var card = Card(("http://localhost/v17", "1.7"));
        Assert.Null(A2AClientFactory.SelectInterface(card, Options()));
        var ex = Assert.Throws<A2AException>(() => A2AClientFactory.Create(card, new HttpClient(new Recorder()), Options()));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
    }

    [Fact]
    public async Task A_1_1_client_follows_the_card_order_and_sends_the_version_of_the_interface_it_uses()
    {
        var recorder = new Recorder();
        var preferred10 = Card(("http://localhost/v10", "1.0"), ("http://localhost/v11", "1.1"));
        await A2AClientFactory.Create(preferred10, new HttpClient(recorder), Options("1.1")).GetTaskAsync("t1");
        var preferred11 = Card(("http://localhost/v11", "1.1"), ("http://localhost/v10", "1.0"));
        await A2AClientFactory.Create(preferred11, new HttpClient(recorder), Options("1.1")).GetTaskAsync("t1");

        var requests = recorder.Requests.ToArray();
        Assert.StartsWith("/v10/", requests[0].RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("1.0", Assert.Single(requests[0].Headers.GetValues(A2AProtocol.Headers.Version)));
        Assert.StartsWith("/v11/", requests[1].RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("1.1", Assert.Single(requests[1].Headers.GetValues(A2AProtocol.Headers.Version)));
    }

    [Fact]
    public async Task RequireExactProtocolVersion_never_falls_back_to_an_older_minor()
    {
        var card = Card(("http://localhost/v10", "1.0"), ("http://localhost/v11", "1.1"));
        var selected = A2AClientFactory.SelectInterface(card, Options("1.1", exact: true));
        Assert.Equal("http://localhost/v11", selected!.Url);

        var recorder = new Recorder();
        await A2AClientFactory.Create(card, new HttpClient(recorder), Options("1.1", exact: true)).GetTaskAsync("t1");
        Assert.Equal("1.1", Assert.Single(Assert.Single(recorder.Requests).Headers.GetValues(A2AProtocol.Headers.Version)));

        Assert.Null(A2AClientFactory.SelectInterface(Card(("http://localhost/v10", "1.0")), Options("1.1", exact: true)));
    }

    [Theory]
    [InlineData("1.0.2", true)]
    [InlineData("1.0", true)]
    [InlineData("1", true)]
    [InlineData("1.1", false)]
    [InlineData("2.0", false)]
    [InlineData("0.3", false)]
    [InlineData("", false)]
    [InlineData("one.zero", false)]
    public void A_1_0_client_speaks_only_1_0_interfaces(string version, bool selected)
    {
        var card = Card(("http://localhost/a2a", version));
        Assert.Equal(selected, A2AClientFactory.SelectInterface(card, Options()) is not null);
    }

    [Fact]
    public async Task A_patch_version_on_the_card_is_sent_as_major_minor()
    {
        var recorder = new Recorder();
        await A2AClientFactory.Create(Card(("http://localhost/a2a", "1.0.2")), new HttpClient(recorder), Options()).GetTaskAsync("t1");
        Assert.Equal("1.0", Assert.Single(Assert.Single(recorder.Requests).Headers.GetValues(A2AProtocol.Headers.Version)));
    }

    [Fact]
    public void Clone_keeps_RequireExactProtocolVersion()
    {
        Assert.True(Options(exact: true).Clone().RequireExactProtocolVersion);
    }
}
