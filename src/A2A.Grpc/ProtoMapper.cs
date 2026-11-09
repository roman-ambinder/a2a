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

    // Below this size a model's JSON is decoded to one string for the parser: smaller than a StreamReader's own buffers.
    private const int MaxStringBytes = 16 * 1024;

    // One reusable buffer and writer per thread. A buffer that grew past MaxRetainedBytes is dropped (with its writer) so one
    // large message does not pin memory.
    [ThreadStatic]
    private static MemoryStream? t_buffer;
    [ThreadStatic]
    private static StreamWriter? t_writer;

    /// <summary>Proto → model.</summary>
    public static T ToModel<T>(IMessage proto) => ToModelCore<T>(proto, maxDepth: 0);

    /// <summary>
    /// Proto → model for an incoming request: nesting deeper than <paramref name="maxDepth"/> (the request depth limit of the
    /// HTTP bindings, applied to the same JSON form) is InvalidParams.
    /// </summary>
    public static T ToModel<T>(IMessage proto, int maxDepth) => ToModelCore<T>(proto, maxDepth);

    /// <summary>Formats <paramref name="proto"/> as ProtoJSON into a reused UTF-8 buffer and reads the model from it. <paramref name="maxDepth"/> 0 means the reader's default.</summary>
    private static T ToModelCore<T>(IMessage proto, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(proto);
        var buffer = t_buffer ??= new MemoryStream();
        buffer.SetLength(0);
        // The writer (and its 4 KB char buffer) is reused: one per thread, not one per message.
        var writer = t_writer ??= new StreamWriter(buffer, Utf8, 4096, leaveOpen: true);
        var written = false;
        try
        {
            // The writer encodes straight into UTF-8 bytes, so no UTF-16 string holds the whole message.
            Formatter.Format(proto, writer);
            writer.Flush();
            written = true;
            var json = new ReadOnlySpan<byte>(buffer.GetBuffer(), 0, (int)buffer.Length);
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = maxDepth });
            return JsonSerializer.Deserialize(ref reader, A2AJson.GetTypeInfo<T>()) ?? throw new JsonException($"Null {typeof(T).Name} payload.");
        }
        catch (JsonException ex)
        {
            throw A2AException.InvalidParams($"Cannot map {proto.Descriptor.Name}: {ex.Message}");
        }
        finally
        {
            // A failed format may leave text in the writer's buffer: start over with a new pair.
            if (!written || buffer.Capacity > MaxRetainedBytes)
            {
                t_buffer = null;
                t_writer = null;
            }
            else buffer.SetLength(0);
        }
    }

    /// <summary>Model → proto. <typeparamref name="TModel"/> is the declared model type (for unions, the abstract base), so the
    /// source-generated metadata for that type writes the wire form, including the union member name.</summary>
    public static TProto ToProto<TProto, TModel>(TModel model) where TProto : IMessage<TProto>, new()
    {
        ArgumentNullException.ThrowIfNull(model);
        // The declared type's metadata writes the wire form (for unions, the member name).
        return ToProto<TProto>(JsonSerializer.SerializeToUtf8Bytes(model, A2AJson.GetTypeInfo<TModel>()));
    }

    /// <summary>
    /// Model JSON → proto: <paramref name="modelJson"/> is the ProtoJSON form of a model, as the SDK's serializer writes it (for
    /// example the stored bytes of a stream event), so a model that is already encoded is not encoded again.
    /// </summary>
    public static TProto ToProto<TProto>(ReadOnlyMemory<byte> modelJson) where TProto : IMessage<TProto>, new()
    {
        try
        {
            // JsonParser reads only a string or a TextReader. A small message is one short string; a large one is read through
            // a reader that decodes the UTF-8 in small chunks, so no UTF-16 string holds the whole message.
            if (modelJson.Length <= MaxStringBytes) return Parser.Parse<TProto>(Utf8.GetString(modelJson.Span));
            var bytes = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(modelJson, out var segment)
                ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
                : new MemoryStream(modelJson.ToArray(), writable: false);
            using var reader = new StreamReader(bytes, Utf8);
            return Parser.Parse<TProto>(reader);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new A2AException(A2AErrorCode.InvalidAgentResponse, $"Cannot map {typeof(TProto).Name}: {ex.Message}");
        }
    }
}
