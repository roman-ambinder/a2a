using System.Net;
using A2A.Client;
using A2A.Client.Grpc;
using A2A.Server;
using A2A.Server.Grpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace A2A.Grpc.Tests;

/// <summary>
///     A card's gRPC address without a scheme ("host:port"). The proto comment and this SDK read it as TLS; some SDKs list
///     cleartext gRPC that way (found by the a2a-itk run), so <c>UseGrpc(channelOptions, false)</c> calls it over cleartext.
/// </summary>
public class GrpcSchemeLessAddressTests
{
    [Theory]
    [InlineData("agent.example:50051", true, "https://agent.example:50051/")]
    [InlineData("agent.example:50051", false, "http://agent.example:50051/")]
    [InlineData("//agent.example:50051", false, "http://agent.example:50051/")]
    [InlineData("https://agent.example:50051", false, "https://agent.example:50051/")] // an explicit scheme always wins
    [InlineData("http://agent.example:50051", true, "http://agent.example:50051/")]
    public void A_scheme_less_address_follows_the_setting_and_an_explicit_scheme_wins(string url, bool tls, string expected)
    {
        Assert.Equal(new Uri(expected), GrpcTransport.ParseAddress(url, tls));
    }

    [Fact]
    public void The_one_argument_ParseAddress_still_reads_a_bare_address_as_https()
    {
        Assert.Equal(new Uri("https://agent.example:50051/"), GrpcTransport.ParseAddress("agent.example:50051"));
    }

    /// <summary>An A2A agent that serves only cleartext HTTP/2 gRPC on a real port, with a card that lists it as host:port.</summary>
    private static async Task<(WebApplication App, AgentCard Card)> StartCleartextAgentAsync()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var card = GrpcHost.Card();
        card.SupportedInterfaces = [new AgentInterface { Url = $"127.0.0.1:{port}", ProtocolBinding = ProtocolBindings.Grpc }];
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddA2AServer(o =>
        {
            o.AllowAnonymous = true;
            o.AgentCard = card;
        }).AddAgentExecutor<ScenarioAgent>();
        builder.Services.AddA2AGrpc();
        var app = builder.Build();
        app.MapA2AGrpc();
        await app.StartAsync();
        return (app, card);
    }

    [Fact]
    public async Task With_TLS_off_for_scheme_less_addresses_a_cleartext_agent_answers()
    {
        var (app, card) = await StartCleartextAgentAsync();
        await using var _ = app;
        var client = A2AClientFactory.Create(card, new HttpClient(),
            new A2AClientOptions { SupportedBindings = [ProtocolBindings.Grpc] }.UseGrpc(null, useTlsForSchemeLessAddresses: false));

        var reply = await client.SendMessageAsync("hello");
        Assert.Equal("hi there", reply.GetText());
    }

    [Fact]
    public async Task By_default_a_scheme_less_address_is_called_over_TLS_and_a_cleartext_agent_is_not_reached()
    {
        var (app, card) = await StartCleartextAgentAsync();
        await using var _ = app;
        var client = A2AClientFactory.Create(card, new HttpClient(),
            new A2AClientOptions { SupportedBindings = [ProtocolBindings.Grpc], MaxRetries = 0 }.UseGrpc());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAnyAsync<Exception>(() => client.SendMessageAsync("hello", cancellationToken: cts.Token));
    }
}
