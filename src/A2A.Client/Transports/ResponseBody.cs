using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using A2A.Json;

namespace A2A.Client.Transports;

/// <summary>
///     Response text as UTF-8 bytes in an <see cref="ArrayPool{T}" /> array, decoded as
///     <see cref="HttpContent.ReadAsStringAsync()" />
///     would decode it (charset, then BOM, then UTF-8) and without a BOM. <see cref="Dispose" /> clears the used bytes and
///     returns
///     the array. A mutable struct:
///     keep it in one local and never copy it.
/// </summary>
internal struct PooledUtf8 : IDisposable
{
    private byte[]? _array;
    private readonly int _length;

    public PooledUtf8(byte[] array, int start, int length)
    {
        _array = array;
        Start = start;
        _length = length;
    }

    private int Start { get; }

    public readonly ReadOnlySpan<byte> Span => _array is null ? default : _array.AsSpan(Start, _length);

    public readonly ReadOnlyMemory<byte> Memory => _array is null ? default : _array.AsMemory(Start, _length);

    public void Dispose()
    {
        var array = _array;
        if (array is null) return;
        _array = null;
        array.AsSpan(0, Start + _length)
            .Clear(); // response bodies can carry secrets; do not leave them in the shared pool
        ArrayPool<byte>.Shared.Return(array);
    }
}

/// <summary>Reads response bodies as UTF-8 bytes instead of strings.</summary>
internal static class ResponseBody
{
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>
    ///     True when the content is UTF-8 by its headers: no charset, or a UTF-8 charset. Other charsets need
    ///     <see cref="HttpContent.ReadAsStringAsync()" /> to decode them.
    /// </summary>
    public static bool IsUtf8(HttpContent content)
    {
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        return string.IsNullOrEmpty(charset)
               || charset!.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
               || charset.Equals("utf8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="prefix" /> starts with a UTF-16 or UTF-32 byte order mark.</summary>
    public static bool HasWideBom(ReadOnlySpan<byte> prefix)
    {
        return prefix.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) || prefix.StartsWith(
                                                                       (ReadOnlySpan<byte>)[0xFF, 0xFE])
                                                                   || prefix.StartsWith((ReadOnlySpan<byte>)
                                                                       [0x00, 0x00, 0xFE, 0xFF]);
    }

    /// <summary>The whole body as pooled UTF-8 bytes (see <see cref="PooledUtf8" />). The content must be buffered.</summary>
    public static async Task<PooledUtf8> ReadUtf8Async(HttpContent content, CancellationToken cancellationToken)
    {
        if (IsUtf8(content))
        {
            var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer =
                ArrayPool<byte>.Shared.Rent(stream.CanSeek ? (int)Math.Max(stream.Length - stream.Position, 1) : 4096);
            var length = 0;
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken)
                           .ConfigureAwait(false)) > 0)
                {
                    length += read;
                    if (length == buffer.Length)
                    {
                        var bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                        buffer.AsSpan(0, length).CopyTo(bigger);
                        buffer.AsSpan(0, length).Clear();
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = bigger;
                    }
                }
            }
            catch
            {
                buffer.AsSpan(0, length).Clear();
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }

            var bytes = buffer.AsSpan(0, length);
            if (!HasWideBom(bytes) && Utf8.IsValid(bytes))
            {
                var start = bytes.StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;
                return new PooledUtf8(buffer, start, length - start);
            }

            bytes.Clear();
            ArrayPool<byte>.Shared.Return(buffer);
            // A UTF-16/32 BOM, or invalid UTF-8 (which the text form replaces with U+FFFD): decode as ReadAsStringAsync does.
            // The caller buffered the content, so it can be read again.
        }

        return FromString(await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Buffers the content so it can be read again (for example as text for an error message).</summary>
    public static Task BufferAsync(HttpContent content, CancellationToken cancellationToken) =>
#if NET9_0_OR_GREATER
        content.LoadIntoBufferAsync(cancellationToken);
#else
        content.ReadAsByteArrayAsync(cancellationToken);
#endif

    /// <summary>The UTF-8 bytes of <paramref name="text" /> in a pooled array.</summary>
    public static PooledUtf8 FromString(string text)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(Encoding.UTF8.GetMaxByteCount(text.Length), 1));
        var length = Encoding.UTF8.GetBytes(text, buffer);
        return new PooledUtf8(buffer, 0, length);
    }

    /// <summary>
    ///     Deserializes a UTF-8 body straight from the content stream; other encodings are decoded first. The content must be
    ///     buffered (<see cref="HttpCompletionOption.ResponseContentRead" />), so a failed read can be replayed from the text.
    /// </summary>
    public static async Task<T?> DeserializeAsync<T>(HttpContent content, CancellationToken cancellationToken)
    {
        var info = A2AJson.GetTypeInfo<T>();
        if (IsUtf8(content))
        {
            var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (!stream.CanSeek || !StartsWithWideBom(stream))
                try
                {
                    return await JsonSerializer.DeserializeAsync(stream, info, cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException) when (stream.CanSeek)
                {
                    // Failure path only: invalid UTF-8 binds from the text form (U+FFFD), and every other failure keeps the
                    // exact exception of the text path.
                }
        }

        return JsonSerializer.Deserialize(await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false),
            info);
    }

    private static bool StartsWithWideBom(Stream stream)
    {
        var position = stream.Position;
        Span<byte> prefix = stackalloc byte[4];
        var read = stream.ReadAtLeast(prefix, prefix.Length, false);
        stream.Position = position;
        return HasWideBom(prefix[..read]);
    }
}

/// <summary>
///     One <see cref="Utf8JsonReader" /> pass over a JSON value (default reader options, the same rules as
///     <see cref="JsonDocument.Parse(string, JsonDocumentOptions)" />)
///     that records where the named members of a root object sit.
/// </summary>
internal static class JsonMembers
{
    /// <summary>
    ///     False when <paramref name="json" /> is not one well-formed JSON value, or when a named member appears twice in the
    ///     root
    ///     object (callers then take their former path). <paramref name="found" /> gets each name's value range, -1 when
    ///     absent.
    /// </summary>
    public static bool TryScan(ReadOnlySpan<byte> json, byte[][] names, Span<(int Start, int End)> found,
        out Range root, out bool rootIsObject)
    {
        root = default;
        rootIsObject = false;
        found.Fill((-1, -1));
        try
        {
            var reader = new Utf8JsonReader(json);
            int rootStart = -1, rootEnd = -1, member = -1, memberStart = -1;
            while (reader.Read())
            {
                var token = reader.TokenType;
                var depth = reader.CurrentDepth;
                if (rootStart < 0)
                {
                    rootStart = (int)reader.TokenStartIndex;
                    rootIsObject = token == JsonTokenType.StartObject;
                }

                if (token == JsonTokenType.PropertyName)
                {
                    if (depth != 1) continue;
                    member = -1;
                    for (var i = 0; i < names.Length; i++)
                    {
                        if (!reader.ValueTextEquals(names[i])) continue;
                        if (found[i].Start >= 0) return false; // repeated: let the former path decide which one counts
                        member = i;
                        break;
                    }

                    continue;
                }

                if (member >= 0 && depth == 1)
                {
                    if (memberStart < 0) memberStart = (int)reader.TokenStartIndex;
                    if (token is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
                    {
                        found[member] = (memberStart, (int)reader.BytesConsumed);
                        member = -1;
                        memberStart = -1;
                    }
                }

                if (depth == 0 && token is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
                    rootEnd = (int)reader.BytesConsumed;
            }

            if (rootStart < 0 || rootEnd < 0) return false;
            root = new Range(rootStart, rootEnd);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}