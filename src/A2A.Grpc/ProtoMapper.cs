using System.Text;
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
/// cannot drift from the proto. The cost is one extra JSON encode/decode per message, which is small next to network I/O. Both directions stay in UTF-8 bytes; no whole-message UTF-16 string is built.
/// </remarks>
public static class ProtoMapper
{
    private static readonly JsonFormatter Formatter = new(JsonFormatter.Settings.Default.WithFormatDefaultValues(false));
    private static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private static readonly UTF8Encoding Utf8 = new(false);
    private const int MaxRetainedBytes = 256 * 1024;

    // One reusable buffer per thread. A buffer that grew past MaxRetainedBytes is dropped so one large message does not pin memory.
    [ThreadStatic]
    private static MemoryStream? t_buffer;

    /// <summary>Proto → model.</summary>
    public static T ToModel<T>(IMessage proto)
    {
        ArgumentNullException.ThrowIfNull(proto);
        var buffer = t_buffer ??= new MemoryStream();
        buffer.SetLength(0);
        try
        {
            // The writer encodes straight into UTF-8 bytes, so no UTF-16 string holds the whole message.
            using (var writer = new StreamWriter(buffer, Utf8, 4096, leaveOpen: true))
                Formatter.Format(proto, writer);
            var json = new ReadOnlySpan<byte>(buffer.GetBuffer(), 0, (int)buffer.Length);
            return JsonSerializer.Deserialize(json, A2AJson.GetTypeInfo<T>()) ?? throw new JsonException($"Null {typeof(T).Name} payload.");
        }
        catch (JsonException ex)
        {
            throw A2AException.InvalidParams($"Cannot map {proto.Descriptor.Name}: {ex.Message}");
        }
        finally
        {
            if (buffer.Capacity > MaxRetainedBytes) t_buffer = null;
            else buffer.SetLength(0);
        }
    }

    /// <summary>Model → proto.</summary>
    public static TProto ToProto<TProto>(object model) where TProto : IMessage<TProto>, new()
    {
        ArgumentNullException.ThrowIfNull(model);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(model, A2AJson.Options.GetTypeInfo(model.GetType()));
            // JsonParser reads only a string or a TextReader; the reader decodes the UTF-8 in small chunks.
            using var reader = new StreamReader(new MemoryStream(bytes, writable: false), Utf8);
            return Parser.Parse<TProto>(reader);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new A2AException(A2AErrorCode.InvalidAgentResponse, $"Cannot map {typeof(TProto).Name}: {ex.Message}");
        }
    }
}
