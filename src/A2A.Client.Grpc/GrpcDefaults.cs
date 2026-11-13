using Grpc.Net.Client;

namespace A2A.Client.Grpc;

/// <summary>The channel options of the channels the SDK makes when the caller gives none.</summary>
internal static class GrpcDefaults
{
    /// <summary>
    ///     How long a connection with a call open may stay quiet before the client sends an HTTP/2 ping. Not below 5
    ///     minutes: gRPC servers in Go, Java and C core refuse more frequent pings on a quiet connection by default (they
    ///     answer with GOAWAY <c>too_many_pings</c> and close it).
    /// </summary>
    internal static readonly TimeSpan KeepAlivePingDelay = TimeSpan.FromMinutes(5);

    /// <summary>How long the client waits for the ping's answer before it closes the connection, failing its calls.</summary>
    internal static readonly TimeSpan KeepAlivePingTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    ///     New options with a handler of their own, which the channel disposes. It is the handler Grpc.Net.Client makes by
    ///     default (several HTTP/2 connections allowed) plus keep-alive pings while a call is open, so a stream over a
    ///     connection that a NAT or proxy dropped silently fails (<c>UNAVAILABLE</c>) within about 5 minutes instead of
    ///     waiting for the operating system, and <see cref="A2AClient.ObserveTaskAsync" /> reconnects.
    /// </summary>
    public static GrpcChannelOptions ChannelOptions()
    {
        return new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = KeepAlivePingDelay,
                KeepAlivePingTimeout = KeepAlivePingTimeout,
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests
            },
            DisposeHttpClient = true
        };
    }
}
