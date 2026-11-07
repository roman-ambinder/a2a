using A2A.Client;
using A2A.Client.Grpc;
using Grpc.Net.Client;

namespace A2A.Grpc.Tests;

public class GrpcClientFactoryTests : IAsyncLifetime
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
