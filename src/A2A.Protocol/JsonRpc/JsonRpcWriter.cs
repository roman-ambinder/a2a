using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using A2A.Json;

namespace A2A.JsonRpc;

/// <summary>
///     Writes JSON-RPC envelopes with one <see cref="Utf8JsonWriter" /> pass: the payload is encoded straight into the
///     envelope
///     (or its stored bytes are copied in), never encoded to a <see cref="JsonElement" /> first. The output is the same as
///     serializing a <see cref="JsonRpcResponse" /> or <see cref="JsonRpcRequest" />: properties in the order
///     <c>jsonrpc</c>,
///     <c>id</c>, then <c>result</c> (or <c>method</c>, <c>params</c>); a missing id is written as <c>"id":null</c>.
/// </summary>
public static class JsonRpcWriter
{
    private const int EnvelopeOverhead = 64;

    // The same escaping as A2AJson.Options, so strings are written exactly as the serializer would.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = A2AJson.Options.Encoder };

    /// <summary>Builds a success response whose <c>result</c> is the given JSON, copied in as it is.</summary>
    /// <param name="id">The request id; null is written as <c>null</c>.</param>
    /// <param name="resultJson">One complete JSON value in UTF-8. It is not validated: pass only bytes from the serializer.</param>
    /// <returns>The UTF-8 bytes of the response.</returns>
    public static byte[] Success(JsonElement? id, ReadOnlySpan<byte> resultJson)
    {
        using var buffer = new PooledBufferWriter(resultJson.Length + EnvelopeOverhead);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            Begin(w, id);
            w.WritePropertyName("result"u8);
            w.WriteRawValue(resultJson, true);
            w.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    ///     The bytes of a success response up to its result: <c>{"jsonrpc":"2.0","id":…,"result":</c>. Followed by one JSON
    ///     value
    ///     and <c>}</c>, they are the same bytes as <see cref="Success(JsonElement?, ReadOnlySpan{byte})" />. A stream writes
    ///     this
    ///     once and copies each event's stored bytes after it, with no envelope built per event.
    /// </summary>
    /// <param name="id">The request id; null is written as <c>null</c>.</param>
    /// <returns>The UTF-8 bytes of the envelope before the result.</returns>
    public static byte[] SuccessPrefix(JsonElement? id)
    {
        using var buffer = new PooledBufferWriter(EnvelopeOverhead);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            Begin(w, id);
            w.WritePropertyName("result"u8);
            w.Flush(); // the object stays open: the caller adds the value and the closing brace
        }

        return buffer.ToArray();
    }

    /// <summary>Builds a success response, encoding <paramref name="result" /> straight into the envelope (one encode).</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="id">The request id; null is written as <c>null</c>.</param>
    /// <param name="result">The result.</param>
    /// <param name="info">The metadata for <typeparamref name="T" />, e.g. <c>A2AJson.GetTypeInfo&lt;T&gt;()</c>.</param>
    /// <returns>The UTF-8 bytes of the response. Nothing is returned if encoding throws.</returns>
    public static byte[] Success<T>(JsonElement? id, T result, JsonTypeInfo<T> info)
    {
        using var buffer = new PooledBufferWriter(1024);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteSuccess(w, id, result, info);
        }

        return buffer.ToArray();
    }

    /// <summary>
    ///     Writes a success response whose <c>result</c> is <paramref name="result" />, encoded directly into
    ///     <paramref name="w" />.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="w">The writer to write the whole response object to.</param>
    /// <param name="id">The request id; null is written as <c>null</c>.</param>
    /// <param name="result">The result.</param>
    /// <param name="info">The metadata for <typeparamref name="T" />, e.g. <c>A2AJson.GetTypeInfo&lt;T&gt;()</c>.</param>
    public static void WriteSuccess<T>(Utf8JsonWriter w, JsonElement? id, T result, JsonTypeInfo<T> info)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(info);
        Begin(w, id);
        w.WritePropertyName("result"u8);
        JsonSerializer.Serialize(w, result, info);
        w.WriteEndObject();
    }

    /// <summary>Builds a request with a string id, encoding <paramref name="params" /> straight into the envelope.</summary>
    /// <typeparam name="T">The parameters type.</typeparam>
    /// <param name="id">The request id.</param>
    /// <param name="method">The A2A method name.</param>
    /// <param name="params">The parameters.</param>
    /// <param name="info">The metadata for <typeparamref name="T" />.</param>
    /// <returns>The UTF-8 bytes of the request.</returns>
    public static byte[] Request<T>(string id, string method, T @params, JsonTypeInfo<T> info)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(info);
        using var buffer = new PooledBufferWriter(512);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc"u8, "2.0"u8);
            w.WriteString("id"u8, id);
            w.WriteString("method"u8, method);
            w.WritePropertyName("params"u8);
            JsonSerializer.Serialize(w, @params, info);
            w.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>The writer options of the envelope writers, for a <see cref="Utf8JsonWriter" /> made elsewhere (tests).</summary>
    /// <returns>Options with the same string escaping as <see cref="A2AJson.Options" />.</returns>
    internal static JsonWriterOptions CreateWriterOptions()
    {
        return WriterOptions;
    }

    private static void Begin(Utf8JsonWriter w, JsonElement? id)
    {
        w.WriteStartObject();
        w.WriteString("jsonrpc"u8, "2.0"u8);
        w.WritePropertyName("id"u8);
        if (id is { } v) v.WriteTo(w);
        else w.WriteNullValue();
    }

    /// <summary>
    ///     An <see cref="IBufferWriter{T}" /> over a pooled array. <see cref="Utf8JsonWriter" /> asks for at least 4 KB at a
    ///     time,
    ///     which a plain <see cref="ArrayBufferWriter{T}" /> would allocate for every envelope.
    /// </summary>
    private sealed class PooledBufferWriter(int initialCapacity) : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 4096));
        private int _written;

        public void Advance(int count)
        {
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Grow(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Grow(sizeHint);
            return _buffer.AsSpan(_written);
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        public byte[] ToArray()
        {
            return _buffer.AsSpan(0, _written).ToArray();
        }

        private void Grow(int sizeHint)
        {
            var needed = _written + Math.Max(sizeHint, 1);
            if (needed <= _buffer.Length) return;
            var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, _buffer.Length * 2));
            _buffer.AsSpan(0, _written).CopyTo(bigger);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = bigger;
        }
    }
}