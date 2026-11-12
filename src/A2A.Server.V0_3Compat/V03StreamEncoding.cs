using System.Runtime.CompilerServices;
using System.Text.Json;
using A2A.Server.Streaming;
using A2A.V0_3Compat;

namespace A2A.Server.V0_3Compat;

/// <summary>
///     The v0.3 bytes of stream events, encoded once per event and shared by every v0.3 subscriber of the task, as the
///     v1.0 bindings share <see cref="SequencedEvent.Json" />. Only an event that carries its v1.0 bytes is shared: such an
///     event is immutable by contract and the same instance goes to every subscriber. A snapshot (no bytes) is encoded
///     for its caller alone. The JSON-RPC envelope differs per caller (its id), so the shared bytes are the
///     <c>result</c> member, and each stream writes its own envelope around them.
/// </summary>
internal static class V03StreamEncoding
{
    private static readonly byte[] NullResultEnd = "null}"u8.ToArray();

    private static readonly RecentBytes JsonRpcResults =
        new(evt => V03Json.ToUtf8(V03JsonCodec.Instance.WriteStreamEvent(evt)));

    private static readonly RecentBytes RestEvents =
        new(evt => V03Json.ToUtf8(V03ProtoJsonCodec.Instance.WriteStreamEvent(evt)));

    /// <summary>The closing brace of the JSON-RPC envelope written after <see cref="JsonRpcResult" />.</summary>
    public static ReadOnlyMemory<byte> EnvelopeEnd { get; } = "}"u8.ToArray();

    /// <summary>
    ///     The JSON-RPC success envelope up to its <c>result</c> value, exactly as the whole envelope is written:
    ///     <c>{"jsonrpc":"2.0","id":...,"result":</c>.
    /// </summary>
    public static byte[] SuccessPrefix(JsonElement? id)
    {
        var whole = V03Json.ToUtf8(V03JsonCodec.WriteSuccess(id, null));
        if (!whole.AsSpan().EndsWith(NullResultEnd))
            throw new InvalidOperationException("Unexpected JSON-RPC envelope.");
        return whole[..^NullResultEnd.Length];
    }

    /// <summary>The <c>result</c> of the v0.3 JSON-RPC stream response for <paramref name="evt" />.</summary>
    public static ReadOnlyMemory<byte> JsonRpcResult(SequencedEvent evt)
    {
        return JsonRpcResults.Get(evt);
    }

    /// <summary>The v0.3 HTTP+JSON (ProtoJSON) stream event for <paramref name="evt" />.</summary>
    public static ReadOnlyMemory<byte> RestEvent(SequencedEvent evt)
    {
        return RestEvents.Get(evt);
    }

    /// <summary>
    ///     A small table of the most recently encoded events, by identity. Subscribers of a task get each event at about
    ///     the same time, so the table needs to hold only the latest few; an older event that was pushed out is encoded
    ///     again. It holds at most <see cref="Size" /> events, so it pins little memory.
    /// </summary>
    private sealed class RecentBytes(Func<StreamResponse, byte[]> encode)
    {
        private const int Size = 128; // a power of two

        private readonly Entry?[] _slots = new Entry?[Size];

        public ReadOnlyMemory<byte> Get(SequencedEvent evt)
        {
            if (evt.Json.IsEmpty) return encode(evt.Event);
            var slot = RuntimeHelpers.GetHashCode(evt.Event) & (Size - 1);
            var entry = Volatile.Read(ref _slots[slot]);
            if (entry is not null && ReferenceEquals(entry.Event, evt.Event)) return entry.Bytes;
            var bytes = encode(evt.Event);
            Volatile.Write(ref _slots[slot], new Entry(evt.Event, bytes));
            return bytes;
        }

        private sealed class Entry(StreamResponse evt, byte[] bytes)
        {
            public StreamResponse Event { get; } = evt;
            public byte[] Bytes { get; } = bytes;
        }
    }
}
