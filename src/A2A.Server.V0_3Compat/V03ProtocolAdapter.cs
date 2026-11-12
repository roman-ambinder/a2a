using A2A.Server.AspNetCore;
using A2A.Server.AspNetCore.Operations;
using A2A.V0_3Compat;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Server.V0_3Compat;

/// <summary>Plugs the v0.3 surface into <c>MapA2A</c> (see <see cref="IA2AProtocolAdapter" />).</summary>
internal sealed class V03ProtocolAdapter : IA2AProtocolAdapter
{
    private IServiceProvider? _services;
    private V03Surface? _surface;

    // The v0.3 card lists GRPC interfaces only when the host serves v0.3 gRPC (A2A.Server.Grpc.V0_3Compat).
    public Func<ReadOnlyMemory<byte>, byte[]>? CardBodyTransform =>
        body => V03CardBody.Blend(body, V03GrpcRegistration.IsServed(_services));

    // v0.2.x clients use the v0.3 JSON-RPC methods (a subset of them) but read the card at /.well-known/agent.json.
    public bool ServeLegacyCardPath => true;

    public RequestDelegate WrapJsonRpc(IServiceProvider services, RequestDelegate core)
    {
        RejectExtensionNameCollisions(services);
        return Surface(services).WrapJsonRpc(core);
    }

    public void MapRoutes(IServiceProvider services, RouteGroupBuilder group, bool tenantInPattern)
    {
        Surface(services).MapRest(group, tenantInPattern);
    }

    /// <summary>
    ///     The wrapper takes every v0.3 method name before the core sees it, so an extension operation with such a name would
    ///     never run. Fail at startup instead. Names are compared ignoring case, like the table's own check for built-in
    ///     names.
    /// </summary>
    private static void RejectExtensionNameCollisions(IServiceProvider services)
    {
        var table = services.GetService<A2AOperationTable>();
        if (table is null) return;
        foreach (var extension in table.Extensions)
            if (V03Surface.IsV03Method(extension.Method))
                throw new InvalidOperationException(
                    $"The extension operation '{extension.Method}' has the name of a v0.3 method. With EnableV03Compat it would never run. Rename it, or do not enable v0.3 compat.");
    }

    private V03Surface Surface(IServiceProvider services)
    {
        _services = services;
        return _surface ??= ActivatorUtilities.CreateInstance<V03Surface>(services);
    }
}

/// <summary>The blended Agent Card body.</summary>
internal static class V03CardBody
{
    /// <summary>The v1.0 card body plus the v0.3 members (see <see cref="V03CardCodec.Blend" />).</summary>
    public static byte[] Blend(ReadOnlyMemory<byte> v1Card, bool includeGrpc = false)
    {
        var node = V03Json.Parse(v1Card.Span);
        return V03Json.ToUtf8(V03CardCodec.Blend(V03Json.Obj(node, "Agent Card"), includeGrpc));
    }
}

/// <summary>
///     Registered by <c>AddA2AGrpcV03Compat()</c> (A2A.Server.Grpc.V0_3Compat): the host serves the v0.3 gRPC service, so
///     the v0.3 cards list its GRPC interfaces.
/// </summary>
internal sealed class V03GrpcRegistration
{
    public static bool IsServed(IServiceProvider? services)
    {
        return services?.GetService(typeof(V03GrpcRegistration)) is not null;
    }
}