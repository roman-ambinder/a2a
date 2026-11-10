using A2A.Json;

namespace A2A.Grpc.Tests;

public sealed class GrpcCardCompletionTests
{
    [Fact]
    public async Task Grpc_binding_is_listed_when_mapped()
    {
        await using var host = await GrpcHost.StartAsync(configure: o => o.AgentCard.SupportedInterfaces = []);
        using var http = new HttpClient(host.Handler()) { BaseAddress = new Uri("http://localhost") };
        var raw = await http.GetStringAsync(A2AProtocol.WellKnownAgentCardPath);
        var card = A2AJson.Deserialize<AgentCard>(raw);
        var only = Assert.Single(card.SupportedInterfaces);
        Assert.Equal(ProtocolBindings.Grpc, only.ProtocolBinding);
        Assert.Equal("http://localhost", only.Url);
    }
}
