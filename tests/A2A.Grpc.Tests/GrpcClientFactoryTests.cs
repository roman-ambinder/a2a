using A2A.Client;
using A2A.Client.Grpc;
using Grpc.Net.Client;

namespace A2A.Grpc.Tests;

public class GrpcClientFactoryCreateAsyncTests : IAsyncLifetime
{
    private GrpcHost _host = null!;

    public async Task InitializeAsync() => _host = await GrpcHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task CreateAsync_resolves_card_and_picks_grpc_interface()
    {
        // The HttpClient only fetches the card; the gRPC channel gets its own handler pointing at the same test server.
        using var http = new HttpClient(_host.Handler()) { BaseAddress = _host.BaseAddress };
        var channelOptions = new GrpcChannelOptions { HttpHandler = _host.Handler("alice") };

        var client = await A2AGrpcClientFactory.CreateAsync(_host.BaseAddress, http, channelOptions: channelOptions);

        Assert.Equal("gRPC Scenario Agent", client.AgentCard!.Name);
        var r = await client.SendMessageAsync("hello");
        Assert.Equal("hi there", Assert.IsType<MessageResponse>(r).Message.GetText());
    }
}

public class GrpcEndpointPolicyTests
{
    private static AgentCard Card(string url) => new()
    {
        Name = "g", Description = "d", Version = "1", Capabilities = new(), DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"], Skills = [],
        SupportedInterfaces = [new AgentInterface { Url = url, ProtocolBinding = ProtocolBindings.Grpc, ProtocolVersion = "1.0" }],
    };

    [Fact]
    public void A_bare_host_and_port_is_judged_by_its_host()
    {
        var options = new A2AClientOptions { EndpointPolicy = AgentEndpointPolicies.PublicOnly() };

        var ex = Assert.Throws<A2AException>(() => A2AGrpcClientFactory.Create(Card("10.0.0.5:50051"), options));

        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
        Assert.Contains("10.0.0.5", ex.Message);
    }

    [Fact]
    public void A_public_bare_host_and_port_is_allowed()
    {
        var options = new A2AClientOptions { EndpointPolicy = AgentEndpointPolicies.PublicOnly() };

        Assert.NotNull(A2AGrpcClientFactory.Create(Card("203.0.113.9:50051"), options));
    }
}
