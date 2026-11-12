using A2A.Server.AspNetCore;
using A2A.Server.Storage;
using A2A.Server.V0_3Compat;
using A2A.V0_3Compat;
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace A2A.Server.Grpc.V0_3Compat;

/// <summary>Opt-in to A2A protocol v0.3 over gRPC: the service <c>a2a.v1.A2AService</c> of the v0.3 proto.</summary>
public static class A2AGrpcV03CompatExtensions
{
    /// <summary>
    ///     Registers the v0.3 gRPC service (and the v1.0 gRPC services, as <c>AddA2AGrpc</c> does). The v0.3 Agent Cards
    ///     served by <c>EnableV03Compat</c> then list the card's GRPC interfaces for v0.3 too: both services run on the
    ///     same address.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="configure">gRPC options, as for <c>AddA2AGrpc</c>.</param>
    /// <returns>The gRPC server builder.</returns>
    /// <exception cref="InvalidOperationException">The compat packages and the core packages have different versions.</exception>
    public static IGrpcServerBuilder AddA2AGrpcV03Compat(this IServiceCollection services,
        Action<GrpcServiceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        RequireSameVersion();
        var grpc = services.AddA2AGrpc(configure);
        services.TryAddSingleton<V03GrpcRegistration>();
        services.AddOptions<GrpcServiceOptions<V03GrpcService>>()
            .Configure<IOptions<A2AServerOptions>>((o, a2a) =>
                o.MaxReceiveMessageSize = (int)Math.Min(a2a.Value.Limits.MaxRequestBodyBytes, int.MaxValue));
        return grpc;
    }

    /// <summary>
    ///     Maps the v0.3 gRPC service <c>a2a.v1.A2AService</c>, next to the v1.0 service that <c>MapA2AGrpc</c> maps. Calls
    ///     are run by the same request handler as v1.0 calls, so authentication, ownership, limits and filters are the same.
    ///     Like <c>MapA2AGrpc</c>, it needs an authorization policy: call <c>RequireAuthorization()</c> on the result, or
    ///     the app does not start. Push configs created over v0.3 gRPC get v0.3 webhook bodies, as over v0.3 HTTP.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder (the app).</param>
    /// <returns>A convention builder for the service's endpoints.</returns>
    /// <exception cref="InvalidOperationException"><see cref="AddA2AGrpcV03Compat" /> was not called.</exception>
    public static GrpcServiceEndpointConventionBuilder MapA2AGrpcV03Compat(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        RequireSameVersion();
        var services = endpoints.ServiceProvider;
        if (services.GetService<V03GrpcRegistration>() is null)
            throw new InvalidOperationException(
                "Call services.AddA2AGrpcV03Compat() before app.MapA2AGrpcV03Compat().");
        // Set before the host starts, so the v0.3 configs stored earlier are sent v0.3 bodies after a restart too.
        var options = services.GetRequiredService<IOptions<A2AServerOptions>>().Value;
        options.PushPayloadEncoder ??= new V03PushPayloadEncoder(services.GetRequiredService<TaskStore>(), options.Limits);

        var builder = endpoints.MapGrpcService<V03GrpcService>();
        // "A2A gRPC /a2a.v1.A2AService/SendMessage": the startup check and the rate limit rejection writer match the prefix.
        builder.Add(e => e.DisplayName = (e.DisplayName ?? "").Replace("gRPC - ", "", StringComparison.Ordinal));
        builder.WithA2AStartupCheck(endpoints, "gRPC");
        return builder;
    }

    private static void RequireSameVersion()
    {
        V03Compat.RequireSameVersion(typeof(A2AGrpcV03CompatExtensions).Assembly, typeof(A2AGrpcExtensions).Assembly,
            typeof(A2AV03CompatExtensions).Assembly, typeof(V03Compat).Assembly, typeof(AgentCard).Assembly);
    }
}
