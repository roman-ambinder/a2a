using System.Text.Json;
using A2A.Json;
using Google.Protobuf;

namespace A2A.Grpc;

/// <summary>
/// Converts between generated protobuf messages (<c>Lf.A2a.V1</c>) and the A2A .NET model.
/// </summary>
/// <remarks>
/// The conversion goes through ProtoJSON. The .NET model's JSON form is ProtoJSON by design (spec §5.5) and is exercised
/// by the JSON-RPC/REST bindings and the cross-SDK interop tests, so one well-tested mapping serves all three bindings and
/// cannot drift from the proto. The cost is one extra JSON encode/decode per message, which is small next to network I/O.
/// </remarks>
public static class ProtoMapper
{
    private static readonly JsonFormatter Formatter = new(JsonFormatter.Settings.Default.WithFormatDefaultValues(false));
    private static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    /// <summary>Proto → model.</summary>
    public static T ToModel<T>(IMessage proto)
    {
        ArgumentNullException.ThrowIfNull(proto);
        try
        {
            return A2AJson.Deserialize<T>(Formatter.Format(proto));
        }
        catch (JsonException ex)
        {
            throw A2AException.InvalidParams($"Cannot map {proto.Descriptor.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Proto → model for an incoming request: nesting deeper than <paramref name="maxDepth"/> (the request depth limit of the
    /// HTTP bindings, applied to the same JSON form) is InvalidParams.
    /// </summary>
    public static T ToModel<T>(IMessage proto, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(proto);
        try
        {
            using var doc = JsonDocument.Parse(Formatter.Format(proto), new JsonDocumentOptions { MaxDepth = maxDepth });
            return A2AJson.Deserialize<T>(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw A2AException.InvalidParams($"Cannot map {proto.Descriptor.Name}: {ex.Message}");
        }
    }

    /// <summary>Model → proto. <typeparamref name="TModel"/> is the declared model type (for unions, the abstract base), so the
    /// source-generated metadata for that type writes the wire form, including the union member name.</summary>
    public static TProto ToProto<TProto, TModel>(TModel model) where TProto : IMessage<TProto>, new()
    {
        ArgumentNullException.ThrowIfNull(model);
        try
        {
            return Parser.Parse<TProto>(A2AJson.Serialize(model));
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new A2AException(A2AErrorCode.InvalidAgentResponse, $"Cannot map {typeof(TProto).Name}: {ex.Message}");
        }
    }
}
