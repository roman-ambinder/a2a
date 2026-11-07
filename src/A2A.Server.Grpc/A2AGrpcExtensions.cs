using A2A.Server.AspNetCore;
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace A2A.Server.Grpc;

public static class A2AGrpcExtensions
{
    /// <summary>Display name prefix of the gRPC endpoints. The startup check finds A2A endpoints by the <c>"A2A "</c> prefix.</summary>
    internal const string DisplayNamePrefix = "A2A gRPC ";

    /// <summary>
    /// Adds the gRPC services needed by <see cref="MapA2AGrpc"/>. Call it next to <c>AddA2AServer</c> and
    /// <c>AddA2AServerAspNetCore</c>. The largest message the A2A service accepts is <see cref="A2ALimits.MaxRequestBodyBytes"/>
    /// (larger messages get <c>RESOURCE_EXHAUSTED</c> from the gRPC layer); <paramref name="configure"/> sets the other,
    /// app-wide gRPC options.
    /// </summary>
    public static IGrpcServerBuilder AddA2AGrpc(this IServiceCollection services, Action<GrpcServiceOptions>? configure = null)
    {
        var grpc = configure is null ? services.AddGrpc() : services.AddGrpc(configure);
        // Per-service options run after the app-wide ones, so the A2A limit applies to this service whatever the app sets.
        services.AddOptions<GrpcServiceOptions<A2AGrpcService>>()
            .Configure<IOptions<A2AServerOptions>>((o, a2a) => o.MaxReceiveMessageSize = (int)Math.Min(a2a.Value.Limits.MaxRequestBodyBytes, int.MaxValue));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IA2ARejectionWriter, GrpcRejectionWriter>());
        return grpc;
    }

    /// <summary>
    /// Maps <c>lf.a2a.v1.A2AService</c>. Like <c>MapA2A</c>, call <c>RequireAuthorization()</c> on the result: if the endpoints
    /// have no authorization policy and <see cref="A2AServerOptions.AllowAnonymous"/> is false, the app fails at startup. The
    /// <c>"a2a"</c> rate limit policy is applied (rejections are <c>RESOURCE_EXHAUSTED</c> with <c>RetryInfo</c>), so the host
    /// must call <c>app.UseA2A()</c> unless <see cref="A2AServerOptions.EnableRateLimiting"/> is false.
    /// gRPC needs HTTP/2: on Kestrel, enable <c>HttpProtocols.Http2</c> (or Http1AndHttp2 with TLS). In the Agent Card, declare
    /// an interface with <c>protocolBinding = "GRPC"</c> and the server address as its URL.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>services.AddA2AServerAspNetCore()</c> was not called.</exception>
    public static GrpcServiceEndpointConventionBuilder MapA2AGrpc(this IEndpointRouteBuilder endpoints)
    {
        A2AEndpointRouteBuilderExtensions.RequireRegistration(endpoints);
        var builder = endpoints.MapGrpcService<A2AGrpcService>();
        builder.Add(e => e.DisplayName = DisplayNamePrefix + (e.DisplayName ?? "").Replace("gRPC - ", "", StringComparison.Ordinal));
        A2AEndpointRouteBuilderExtensions.ApplyRateLimiting(endpoints, builder);
        return builder;
    }
}
