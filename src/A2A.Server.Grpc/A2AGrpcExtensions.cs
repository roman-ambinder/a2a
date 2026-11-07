using A2A.Server.AspNetCore;
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Server.Grpc;

public static class A2AGrpcExtensions
{
    /// <summary>
    /// Adds the gRPC services needed by <see cref="MapA2AGrpc"/>, and the ASP.NET Core A2A services
    /// (<see cref="A2AServiceCollectionExtensions.AddA2AServerAspNetCore"/>: tenant resolver, startup check). Call after <c>AddA2AServer</c>.
    /// </summary>
    public static IGrpcServerBuilder AddA2AGrpc(this IServiceCollection services, Action<GrpcServiceOptions>? configure = null)
    {
        services.AddA2AServerAspNetCore();
        return configure is null ? services.AddGrpc() : services.AddGrpc(configure);
    }

    /// <summary>
    /// Maps <c>lf.a2a.v1.A2AService</c>. gRPC needs HTTP/2: on Kestrel, enable <c>HttpProtocols.Http2</c> (or Http1AndHttp2 with TLS).
    /// In the Agent Card, declare an interface with <c>protocolBinding = "GRPC"</c> and the server address as its URL.
    /// </summary>
    /// <remarks>
    /// Like <c>MapA2A</c>, the app fails at startup unless you call <c>RequireAuthorization()</c> on the result or set
    /// <see cref="A2AServerOptions.AllowAnonymous"/>. The <c>"a2a"</c> rate limit policy is not applied to gRPC: its rejection
    /// is an HTTP 429 that gRPC clients see as <c>Unavailable</c>. Add your own limiter if you need one.
    /// </remarks>
    public static GrpcServiceEndpointConventionBuilder MapA2AGrpc(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGrpcService<A2AGrpcService>().WithA2AStartupCheck(endpoints, "gRPC");
}
