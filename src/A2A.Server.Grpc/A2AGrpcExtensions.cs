using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Server.Grpc;

public static class A2AGrpcExtensions
{
    /// <summary>
    /// Adds the gRPC services needed by <see cref="MapA2AGrpc"/>. Call after <c>AddA2AServer</c>.
    /// </summary>
    public static IGrpcServerBuilder AddA2AGrpc(this IServiceCollection services, Action<GrpcServiceOptions>? configure = null) =>
        configure is null ? services.AddGrpc() : services.AddGrpc(configure);

    /// <summary>
    /// Maps <c>lf.a2a.v1.A2AService</c>. gRPC needs HTTP/2: on Kestrel, enable <c>HttpProtocols.Http2</c> (or Http1AndHttp2 with TLS).
    /// In the Agent Card, declare an interface with <c>protocolBinding = "GRPC"</c> and the server address as its URL.
    /// </summary>
    public static GrpcServiceEndpointConventionBuilder MapA2AGrpc(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGrpcService<A2AGrpcService>();
}
