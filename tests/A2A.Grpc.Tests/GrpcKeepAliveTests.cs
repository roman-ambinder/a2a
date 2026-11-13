using A2A.Client.Grpc;

namespace A2A.Grpc.Tests;

/// <summary>
///     gRPC streams carry no keep-alive messages, so a silently dropped connection is found by HTTP/2 pings: the channels
///     the SDK makes itself (no <c>GrpcChannelOptions</c> given) ping a connection that has a call open and is quiet.
/// </summary>
public class GrpcKeepAliveTests
{
    [Fact]
    public void Default_channels_ping_quiet_connections_with_active_calls()
    {
        var options = GrpcDefaults.ChannelOptions();

        var handler = Assert.IsType<SocketsHttpHandler>(options.HttpHandler);
        // Not below 5 minutes: gRPC servers in Go, Java and C core refuse more frequent pings by default
        // (GOAWAY too_many_pings), and the SDK's own server (Kestrel) does not police them.
        Assert.Equal(TimeSpan.FromMinutes(5), handler.KeepAlivePingDelay);
        Assert.Equal(TimeSpan.FromSeconds(20), handler.KeepAlivePingTimeout);
        Assert.Equal(HttpKeepAlivePingPolicy.WithActiveRequests, handler.KeepAlivePingPolicy);
        Assert.True(handler.EnableMultipleHttp2Connections); // as the handler Grpc.Net.Client makes by default
        Assert.True(options.DisposeHttpClient); // the channel owns its handler
    }

    [Fact]
    public void Each_default_channel_gets_its_own_handler()
    {
        Assert.NotSame(GrpcDefaults.ChannelOptions().HttpHandler, GrpcDefaults.ChannelOptions().HttpHandler);
    }
}
