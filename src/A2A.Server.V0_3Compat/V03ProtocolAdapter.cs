using A2A.Server.AspNetCore;
using A2A.V0_3Compat;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Server.V0_3Compat;

/// <summary>Plugs the v0.3 surface into <c>MapA2A</c> (see <see cref="IA2AProtocolAdapter"/>).</summary>
internal sealed class V03ProtocolAdapter : IA2AProtocolAdapter
{
    private V03Surface? _surface;

    public Func<ReadOnlyMemory<byte>, byte[]>? CardBodyTransform { get; } = V03CardBody.Blend;

    public RequestDelegate WrapJsonRpc(IServiceProvider services, RequestDelegate core) => Surface(services).WrapJsonRpc(core);

    public void MapRoutes(IServiceProvider services, RouteGroupBuilder group, bool tenantInPattern) =>
        Surface(services).MapRest(group, tenantInPattern);

    private V03Surface Surface(IServiceProvider services) => _surface ??= ActivatorUtilities.CreateInstance<V03Surface>(services);
}

/// <summary>The blended Agent Card body.</summary>
internal static class V03CardBody
{
    /// <summary>The v1.0 card body plus the v0.3 members (see <see cref="V03CardCodec.Blend"/>).</summary>
    public static byte[] Blend(ReadOnlyMemory<byte> v1Card)
    {
        var node = V03Json.Parse(v1Card.Span);
        return V03Json.ToUtf8(V03CardCodec.Blend(V03Json.Obj(node, "Agent Card")));
    }
}
