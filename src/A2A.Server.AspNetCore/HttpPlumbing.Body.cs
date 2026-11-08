using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace A2A.Server.AspNetCore;

/// <summary>
/// A request body held in an <see cref="ArrayPool{T}"/> array. <see cref="Dispose"/> clears the bytes and returns the array.
/// It is a mutable struct: keep it in one local (<c>using var</c>) and never copy it, or the array is returned twice.
/// </summary>
internal struct PooledBody : IDisposable
{
    private byte[]? _array;

    public PooledBody(byte[] array, int length)
    {
        _array = array;
        Length = length;
    }

    /// <summary>Number of body bytes.</summary>
    public int Length { get; }

    /// <summary>The body bytes. Valid until <see cref="Dispose"/>.</summary>
    public readonly ReadOnlyMemory<byte> Memory => _array is null ? ReadOnlyMemory<byte>.Empty : _array.AsMemory(0, Length);

    public void Dispose()
    {
        var array = _array;
        if (array is null) return;
        _array = null;
        array.AsSpan(0, Length).Clear(); // request bodies can carry secrets; do not leave them in the shared pool
        ArrayPool<byte>.Shared.Return(array);
    }
}

/// <summary>Where the members of a JSON-RPC envelope sit in the body; -1 when the member is absent.</summary>
internal struct EnvelopeMembers
{
    public (int Start, int End) JsonRpc, Id, Method, Params;

    public static EnvelopeMembers None => new() { JsonRpc = (-1, -1), Id = (-1, -1), Method = (-1, -1), Params = (-1, -1) };
}

internal static partial class HttpPlumbing
{
    private const int ChunkedBodyInitialBuffer = 16 * 1024;

    /// <summary>
    /// Reads the request body into a pooled buffer. More than <paramref name="maxBytes"/> bytes is
    /// <see cref="A2AException.PayloadTooLarge"/> (HTTP 413): a larger <c>Content-Length</c> is refused before reading, and
    /// otherwise reading stops as soon as the running total passes the limit (at most <paramref name="maxBytes"/> + 1 bytes are read).
    /// </summary>
    public static async Task<PooledBody> ReadBodyAsync(HttpRequest request, long maxBytes)
    {
        if (request.ContentLength > maxBytes) throw A2AException.PayloadTooLarge(maxBytes);
        // One byte more than the limit proves the limit was passed; Array.MaxLength bounds a limit no array can hold.
        var cap = (int)Math.Min(maxBytes + 1, Array.MaxLength);
        var initial = request.ContentLength is { } declared ? (int)Math.Min(declared + 1, cap) : Math.Min(ChunkedBodyInitialBuffer, cap);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initial, 1));
        var total = 0;
        try
        {
            var ct = request.HttpContext.RequestAborted;
            while (true)
            {
                var room = Math.Min(buffer.Length, cap);
                if (total == room)
                {
                    if (room == cap) throw A2AException.PayloadTooLarge(maxBytes);
                    var bigger = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)buffer.Length * 2, cap));
                    buffer.AsSpan(0, total).CopyTo(bigger);
                    buffer.AsSpan(0, total).Clear();
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                    room = Math.Min(buffer.Length, cap);
                }
                var read = await request.Body.ReadAsync(buffer.AsMemory(total, room - total), ct).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > maxBytes) throw A2AException.PayloadTooLarge(maxBytes);
            }
            return new PooledBody(buffer, total);
        }
        catch
        {
            buffer.AsSpan(0, total).Clear();
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    /// <summary>
    /// Checks a request body in one <see cref="Utf8JsonReader"/> pass: one well-formed JSON value, nesting no deeper than
    /// <paramref name="maxDepth"/>, and no object that repeats a property name (compared after unescaping, case sensitive),
    /// on .NET 8 and .NET 10 alike. Returns where the value sits in <paramref name="json"/> (surrounding white space excluded),
    /// which is the slice to deserialize. A body that fails the pass is classified by <see cref="ClassifyFailure"/>, so every
    /// rejection keeps its exact former exception and message.
    /// </summary>
    public static Range CheckDepthAndDuplicates(ReadOnlyMemory<byte> json, int maxDepth)
    {
        var members = EnvelopeMembers.None;
        return CheckDepthAndDuplicates(json, maxDepth, collectEnvelope: false, ref members);
    }

    /// <summary>
    /// <see cref="CheckDepthAndDuplicates(ReadOnlyMemory{byte}, int)"/> that also records where the <c>jsonrpc</c>, <c>id</c>,
    /// <c>method</c> and <c>params</c> members of a root object sit, in the same pass.
    /// </summary>
    public static Range CheckJsonRpcBody(ReadOnlyMemory<byte> json, int maxDepth, out EnvelopeMembers members)
    {
        members = EnvelopeMembers.None;
        return CheckDepthAndDuplicates(json, maxDepth, collectEnvelope: true, ref members);
    }

    private static Range CheckDepthAndDuplicates(ReadOnlyMemory<byte> json, int maxDepth, bool collectEnvelope, ref EnvelopeMembers members)
    {
        if (BodyScanner.TryScan(json.Span, maxDepth, checkDuplicates: true, collectEnvelope, ref members, out var root)) return root;
        ClassifyFailure(json, maxDepth);
        // The former parser accepted what the pass refused (not expected): accept it too, and locate the members without the duplicate check.
        members = EnvelopeMembers.None;
        if (BodyScanner.TryScan(json.Span, maxDepth, checkDuplicates: false, collectEnvelope, ref members, out root)) return root;
        throw new JsonException("Invalid request body.");
    }

    /// <summary>
    /// Failure path only: replays the former parse so a rejected body gets exactly the exception it always got. Valid JSON that
    /// breaks a limit is InvalidParams (<see cref="A2AException"/>); malformed JSON throws <see cref="JsonException"/>.
    /// On .NET 10 the parser rejects duplicates itself; on .NET 8 <see cref="ContainsDuplicateProperties"/> checks after the parse.
    /// </summary>
    private static void ClassifyFailure(ReadOnlyMemory<byte> body, int maxDepth)
    {
        try
        {
#if NET10_0_OR_GREATER
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = maxDepth, AllowDuplicateProperties = false });
#else
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = maxDepth });
            if (ContainsDuplicateProperties(body.Span, maxDepth))
                throw A2AException.InvalidParams("Invalid request body: duplicate property names are not allowed");
#endif
        }
        catch (JsonException ex) when (IsWellFormed(body))
        {
            throw A2AException.InvalidParams($"Invalid request body: {ex.Message}");
        }
    }

    /// <summary>Failure path only: tells a limit violation (valid JSON) from a syntax error. Nesting beyond 1024 counts as malformed.</summary>
    private static bool IsWellFormed(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var _ = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 1024 });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when any JSON object in <paramref name="json"/> repeats a property name (compared after unescaping, case sensitive).
    /// The .NET 8 half of <see cref="ClassifyFailure"/>. Throws <see cref="JsonException"/> for malformed JSON or nesting deeper
    /// than <paramref name="maxDepth"/>.
    /// </summary>
    internal static bool ContainsDuplicateProperties(ReadOnlySpan<byte> json, int maxDepth)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = maxDepth });
        var scopes = new Stack<HashSet<string>?>(); // one entry per open container; null for arrays
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    scopes.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    scopes.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (!scopes.Peek()!.Add(reader.GetString()!)) return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>The single validation pass behind <see cref="CheckDepthAndDuplicates(ReadOnlyMemory{byte}, int)"/>.</summary>
    private ref struct BodyScanner
    {
        /// <summary>Objects with more names than this switch from pairwise comparison to a hash set.</summary>
        private const int LinearLimit = 16;

        private int[] _nameStart;   // per recorded name: offset into the body, or ~offset into _scratch for unescaped copies
        private int[] _nameLength;
        private int _names;
        private int[] _frames;      // per open container: index of its first name, or -1 for an array
        private HashSet<string>?[]? _sets;
        private int _depth;
        private byte[]? _scratch;
        private int _scratchUsed;

        public BodyScanner()
        {
            _nameStart = ArrayPool<int>.Shared.Rent(64);
            _nameLength = ArrayPool<int>.Shared.Rent(64);
            _frames = ArrayPool<int>.Shared.Rent(16);
        }

        public static bool TryScan(ReadOnlySpan<byte> json, int maxDepth, bool checkDuplicates, bool collectEnvelope,
            ref EnvelopeMembers members, out Range root)
        {
            root = default;
            var scanner = new BodyScanner();
            try
            {
                return scanner.Scan(json, maxDepth, checkDuplicates, collectEnvelope, ref members, out root);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return false; // malformed, too deep, or a name that cannot be unescaped: ClassifyFailure decides
            }
            finally
            {
                scanner.Release();
            }
        }

        private bool Scan(ReadOnlySpan<byte> json, int maxDepth, bool checkDuplicates, bool collectEnvelope, ref EnvelopeMembers members, out Range root)
        {
            root = default;
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = maxDepth });
            var rootStart = -1;
            var rootEnd = -1;
            var rootIsObject = false;
            var member = 0;          // envelope member whose value comes next: 1 jsonrpc, 2 id, 3 method, 4 params
            var memberStart = -1;
            while (reader.Read())
            {
                var token = reader.TokenType;
                var depth = reader.CurrentDepth;
                if (rootStart < 0)
                {
                    rootStart = (int)reader.TokenStartIndex;
                    rootIsObject = token == JsonTokenType.StartObject;
                }
                switch (token)
                {
                    case JsonTokenType.PropertyName:
                        if (checkDuplicates && !AddName(ref reader, json)) return false;
                        if (collectEnvelope && depth == 1 && rootIsObject)
                            member = reader.ValueTextEquals("jsonrpc"u8) ? 1 : reader.ValueTextEquals("id"u8) ? 2
                                : reader.ValueTextEquals("method"u8) ? 3 : reader.ValueTextEquals("params"u8) ? 4 : 0;
                        continue;
                    case JsonTokenType.StartObject:
                        if (checkDuplicates) Push(_names);
                        break;
                    case JsonTokenType.StartArray:
                        if (checkDuplicates) Push(-1);
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (checkDuplicates) Pop();
                        break;
                }

                if (member != 0 && depth == 1)
                {
                    if (memberStart < 0) memberStart = (int)reader.TokenStartIndex;
                    if (token is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
                    {
                        var range = (memberStart, (int)reader.BytesConsumed);
                        switch (member)
                        {
                            case 1: members.JsonRpc = range; break;
                            case 2: members.Id = range; break;
                            case 3: members.Method = range; break;
                            default: members.Params = range; break;
                        }
                        member = 0;
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

        private void Push(int firstName)
        {
            if (_depth == _frames.Length) Grow(ref _frames, _depth);
            _frames[_depth++] = firstName;
        }

        private void Pop()
        {
            var first = _frames[--_depth];
            if (first < 0) return;
            _names = first;
            if (_sets is not null && _depth < _sets.Length) _sets[_depth] = null;
        }

        /// <summary>Records the current property name in the innermost object. False when that object already has it.</summary>
        private bool AddName(ref Utf8JsonReader reader, ReadOnlySpan<byte> json)
        {
            int start;
            ReadOnlySpan<byte> name;
            if (!reader.ValueIsEscaped)
            {
                start = (int)reader.TokenStartIndex + 1; // TokenStartIndex is the opening quote
                name = json.Slice(start, reader.ValueSpan.Length);
            }
            else
            {
                var need = _scratchUsed + reader.ValueSpan.Length; // unescaping never makes a name longer
                if (_scratch is null || _scratch.Length < need)
                {
                    var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(need, 256));
                    if (_scratch is not null)
                    {
                        _scratch.AsSpan(0, _scratchUsed).CopyTo(bigger);
                        ArrayPool<byte>.Shared.Return(_scratch);
                    }
                    _scratch = bigger;
                }
                var written = reader.CopyString(_scratch.AsSpan(_scratchUsed));
                name = _scratch.AsSpan(_scratchUsed, written);
                start = ~_scratchUsed;
                _scratchUsed += written;
            }

            var frame = _depth - 1;
            var first = _frames[frame];
            var set = _sets is not null && frame < _sets.Length ? _sets[frame] : null;
            if (set is not null) return set.Add(Key(name));

            for (var i = first; i < _names; i++)
                if (Name(i, json).SequenceEqual(name)) return false;

            if (_names - first < LinearLimit)
            {
                if (_names == _nameStart.Length)
                {
                    Grow(ref _nameStart, _names);
                    Grow(ref _nameLength, _names);
                }
                _nameStart[_names] = start;
                _nameLength[_names] = name.Length;
                _names++;
                return true;
            }

            // A large object: hash its names from here on. Latin-1 maps each byte to one char, so equal keys mean equal bytes.
            set = new HashSet<string>(StringComparer.Ordinal);
            for (var i = first; i < _names; i++) set.Add(Key(Name(i, json)));
            set.Add(Key(name));
            _sets ??= new HashSet<string>?[Math.Max(_frames.Length, 16)];
            if (frame >= _sets.Length) Array.Resize(ref _sets, _frames.Length);
            _sets[frame] = set;
            return true;
        }

        private readonly ReadOnlySpan<byte> Name(int i, ReadOnlySpan<byte> json)
        {
            var start = _nameStart[i];
            return start >= 0 ? json.Slice(start, _nameLength[i]) : _scratch.AsSpan(~start, _nameLength[i]);
        }

        private static string Key(ReadOnlySpan<byte> name) => Encoding.Latin1.GetString(name);

        private static void Grow(ref int[] array, int used)
        {
            var bigger = ArrayPool<int>.Shared.Rent(array.Length * 2);
            array.AsSpan(0, used).CopyTo(bigger);
            ArrayPool<int>.Shared.Return(array);
            array = bigger;
        }

        private readonly void Release()
        {
            ArrayPool<int>.Shared.Return(_nameStart);
            ArrayPool<int>.Shared.Return(_nameLength);
            ArrayPool<int>.Shared.Return(_frames);
            if (_scratch is not null) ArrayPool<byte>.Shared.Return(_scratch);
        }
    }
}
