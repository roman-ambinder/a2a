using System.Diagnostics.CodeAnalysis;
using A2A.Server.AspNetCore;
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace A2A.Server.Grpc;

/// <summary>Dependency injection and endpoint extensions for the gRPC binding.</summary>
public static class A2AGrpcExtensions
{
    /// <summary>Display name prefix of the gRPC endpoints. The startup check finds A2A endpoints by the <c>"A2A "</c> prefix.</summary>
    internal const string DisplayNamePrefix = "A2A gRPC ";

    /// <summary>
    ///     Adds the gRPC services needed by <see cref="MapA2AGrpc" /> and <see cref="MapA2AGrpcExtensions" />, and the ASP.NET
    ///     Core A2A services
    ///     (<see cref="A2AServiceCollectionExtensions.AddA2AServerAspNetCore" />: tenant resolver, startup check, rate limit
    ///     policy).
    ///     Call after <c>AddA2AServer</c>. The largest message the A2A service accepts is
    ///     <see cref="A2ALimits.MaxRequestBodyBytes" />
    ///     (larger messages get <c>RESOURCE_EXHAUSTED</c> from the gRPC layer); <paramref name="configure" /> sets the other,
    ///     app-wide gRPC options.
    /// </summary>
    public static IGrpcServerBuilder AddA2AGrpc(this IServiceCollection services,
        Action<GrpcServiceOptions>? configure = null)
    {
        services.AddA2AServerAspNetCore();
        var grpc = configure is null ? services.AddGrpc() : services.AddGrpc(configure);
        // Per-service options run after the app-wide ones, so the A2A limit applies to this service whatever the app sets.
        services.AddOptions<GrpcServiceOptions<A2AGrpcService>>()
            .Configure<IOptions<A2AServerOptions>>((o, a2a) =>
                o.MaxReceiveMessageSize = (int)Math.Min(a2a.Value.Limits.MaxRequestBodyBytes, int.MaxValue));
        services.AddOptions<GrpcServiceOptions<A2AGrpcExtensionService>>()
            .Configure<IOptions<A2AServerOptions>>((o, a2a) =>
                o.MaxReceiveMessageSize = (int)Math.Min(a2a.Value.Limits.MaxRequestBodyBytes, int.MaxValue));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IA2ARejectionWriter, GrpcRejectionWriter>());
        return grpc;
    }

    /// <summary>
    ///     Maps <c>lf.a2a.v1.A2AService</c>. gRPC needs HTTP/2: on Kestrel, enable <c>HttpProtocols.Http2</c> (or
    ///     Http1AndHttp2 with TLS).
    ///     In the Agent Card, declare an interface with <c>protocolBinding = "GRPC"</c> and the server address as its URL, or
    ///     leave the card's interfaces empty to have it listed from the server address (see
    ///     <see cref="A2AHttpOptions.PublicBaseUrl" />).
    /// </summary>
    /// <remarks>
    ///     Like <c>MapA2A</c>, the app fails at startup unless you call <c>RequireAuthorization()</c> on the result or set
    ///     <see cref="A2AServerOptions.AllowAnonymous" />. The <c>"a2a"</c> rate limit policy is applied (rejections are
    ///     <c>RESOURCE_EXHAUSTED</c> with <c>RetryInfo</c>), so the host must call <c>app.UseA2A()</c> unless
    ///     <see cref="A2ARateLimitOptions.Enabled" /> is false.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><c>services.AddA2AServerAspNetCore()</c> was not called.</exception>
    public static GrpcServiceEndpointConventionBuilder MapA2AGrpc(this IEndpointRouteBuilder endpoints)
    {
        var builder = endpoints.MapGrpcService<A2AGrpcService>();
        // Before the startup check's prefix: "A2A gRPC /lf.a2a.v1.A2AService/SendMessage" (the rejection writer matches the prefix).
        builder.Add(e => e.DisplayName = (e.DisplayName ?? "").Replace("gRPC - ", "", StringComparison.Ordinal));
        builder.WithA2AStartupCheck(endpoints, "gRPC");
        // A gRPC interface URL is the server address: the service path is part of the method name, not of the URL.
        endpoints.RegisterA2ABinding(ProtocolBindings.Grpc, "/");
        return builder;
    }

    /// <summary>
    ///     Maps <c>a2a.extensions.v1.A2AExtensionService</c>, which serves the extension operations
    ///     (<see cref="A2AServerBuilder.AddExtensionOperation" />) over gRPC with the same service definition as the official
    ///     A2A .NET SDK.
    ///     Call it in addition to <see cref="MapA2AGrpc" />, which it does not replace.
    /// </summary>
    /// <remarks>
    ///     A separate call, so <c>RequireAuthorization()</c> on the result applies to this service alone. As for
    ///     <see cref="MapA2AGrpc" />,
    ///     the app fails at startup unless the endpoints require authorization or
    ///     <see cref="A2AServerOptions.AllowAnonymous" /> is set,
    ///     and the <c>"a2a"</c> rate limit policy applies. The unary <c>InvokeExtensionOperation</c> is served; the streaming
    ///     RPC
    ///     answers UNIMPLEMENTED. The payload is UTF-8 JSON. See <see cref="A2AGrpcExtensionService" />.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><c>services.AddA2AServerAspNetCore()</c> was not called.</exception>
    [Experimental("A2AEXP004",
        UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public static GrpcServiceEndpointConventionBuilder MapA2AGrpcExtensions(this IEndpointRouteBuilder endpoints)
    {
        var builder = endpoints.MapGrpcService<A2AGrpcExtensionService>();
        // As MapA2AGrpc: "A2A gRPC /a2a.extensions.v1.A2AExtensionService/InvokeExtensionOperation".
        builder.Add(e => e.DisplayName = (e.DisplayName ?? "").Replace("gRPC - ", "", StringComparison.Ordinal));
        builder.WithA2AStartupCheck(endpoints, "gRPC");
        // The gRPC binding is registered once, by MapA2AGrpc.
        return builder;
    }
}