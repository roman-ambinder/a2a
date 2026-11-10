using System.Buffers;
using System.Text.Json;
using A2A.Json;
using Microsoft.AspNetCore.Http;

namespace A2A.Server.AspNetCore;

/// <summary>
///     Writes a JSON response body encoded in full first, in a pooled buffer, then copied into the response pipe: an
///     encoding
///     failure still becomes an error response (nothing was written), and no response-sized array is made per request.
/// </summary>
internal static class PooledResponse
{
    // The same escaping as A2AJson.Options, so strings are written exactly as the serializer would.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = A2AJson.Options.Encoder };

    /// <summary>
    ///     Encodes with <paramref name="write" />; then, only if that succeeded, sets the status and content type, writes the
    ///     bytes
    ///     to the response and flushes.
    /// </summary>
    public static async Task WriteAsync<TState>(HttpContext http, int status, string contentType, TState state,
        Action<Utf8JsonWriter, TState> write)
    {
        var buffer = new Buffer();
        try
        {
            using (var w = new Utf8JsonWriter(buffer, WriterOptions))
            {
                write(w, state);
            }

            http.Response.StatusCode = status;
            http.Response.ContentType = contentType;
            var body = http.Response.BodyWriter;
            body.Write(buffer.Written);
            await body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>An <see cref="IBufferWriter{T}" /> over a pooled array that grows by renting a bigger one.</summary>
    private sealed class Buffer : IBufferWriter<byte>, IDisposable
    {
        private byte[] _array = ArrayPool<byte>.Shared.Rent(4096);
        private int _written;

        public ReadOnlySpan<byte> Written => _array.AsSpan(0, _written);

        public void Advance(int count)
        {
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _array.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _array.AsSpan(_written);
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_array);
            _array = [];
        }

        private void Ensure(int sizeHint)
        {
            if (_array.Length - _written >= Math.Max(sizeHint, 1)) return;
            var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(_array.Length * 2, _written + sizeHint));
            _array.AsSpan(0, _written).CopyTo(bigger);
            ArrayPool<byte>.Shared.Return(_array);
            _array = bigger;
        }
    }
}