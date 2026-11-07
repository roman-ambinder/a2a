using System.Buffers;
using System.Text;
using System.Text.Json;
using A2A.Json;
using A2A.Server.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace A2A.Server.Redis;

/// <summary>
/// A <see cref="TaskEventBus"/> that also fans events out to the other server instances over Redis pub/sub, so
/// SubscribeToTask and blocking SendMessage see events produced on any instance.
/// </summary>
/// <remarks>
/// <para>Local subscribers get events straight from the base class, with no Redis round trip. Every event is also published
/// on the task's channel (<c>{prefix}{tenant}\u001f{taskId}</c>); other instances deliver it to their own subscribers and
/// ignore their own messages. <see cref="Publish"/> and <see cref="Complete"/> never block: they queue the message on the
/// multiplexer (fire and forget).</para>
/// <para>Order: one instance publishes a task's events on one connection, in sequence order, and each instance reads its
/// subscription in order, so per-task order holds. Redis pub/sub is at-most-once (a disconnect drops messages); the request
/// handler already tolerates that: it drops duplicates and fills gaps from the <see cref="Storage.EventStore"/>, which
/// must then be shared too (for example A2A.Server.Postgres).</para>
/// <para>Owner isolation is enforced by the handler before it subscribes; the bus topic is tenant + task id.</para>
/// <para>Call <see cref="StartAsync"/> before serving (the DI extension registers a hosted service that does).</para>
/// </remarks>
public sealed class RedisTaskEventBus : TaskEventBus, IAsyncDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _prefix;
    private readonly string _origin = Guid.NewGuid().ToString("N");
    private readonly ILogger _logger;
    private ChannelMessageQueue? _queue;

    public RedisTaskEventBus(IConnectionMultiplexer redis, RedisEventBusOptions? options = null, ILogger<RedisTaskEventBus>? logger = null)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _prefix = (options ?? new()).ChannelPrefix;
        ArgumentException.ThrowIfNullOrEmpty(_prefix, nameof(RedisEventBusOptions.ChannelPrefix));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Subscribes to the other instances' events. Idempotent.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_queue is not null) return;
        var queue = await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Pattern(EscapeGlob(_prefix) + "*")).WaitAsync(ct).ConfigureAwait(false);
        if (Interlocked.CompareExchange(ref _queue, queue, null) is not null)
        {
            await queue.UnsubscribeAsync().ConfigureAwait(false);
            return;
        }
        // The queue hands messages to this callback one at a time, in arrival order.
        queue.OnMessage(OnMessage);
    }

    /// <summary>Stops receiving the other instances' events.</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _queue, null) is { } queue) await queue.UnsubscribeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    public override void Publish(string taskId, SequencedEvent evt)
    {
        base.Publish(taskId, evt);
        Send(taskId, Encode(evt));
    }

    public override void Complete(string taskId)
    {
        base.Complete(taskId);
        Send(taskId, Encode(null));
    }

    private void Send(string taskId, byte[] payload)
    {
        try
        {
            _redis.GetSubscriber().Publish(RedisChannel.Literal(_prefix + taskId), payload, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            // Never fail the task because the fan-out failed: subscribers elsewhere fill gaps from the event store.
            _logger.LogWarning(ex, "Could not publish an A2A task event to Redis");
        }
    }

    private void OnMessage(ChannelMessage message)
    {
        try
        {
            var channel = message.Channel.ToString();
            if (!channel.StartsWith(_prefix, StringComparison.Ordinal)) return;
            var taskId = channel[_prefix.Length..];
            if (!HasSubscribers(taskId)) return; // nobody here listens: skip decoding

            var data = (ReadOnlyMemory<byte>)message.Message;
            if (!TryDecode(data.Span, out var origin, out var evt) || origin == _origin) return;
            if (evt is { } e) base.Publish(taskId, e);
            else base.Complete(taskId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dropped a malformed A2A task event from Redis");
        }
    }

    /// <summary>{"o": origin, "s": sequence, "e": event} for an event; {"o": origin, "c": true} for the end of a task.</summary>
    private byte[] Encode(SequencedEvent? evt)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("o", _origin);
            if (evt is { } e)
            {
                w.WriteNumber("s", e.Sequence);
                w.WritePropertyName("e");
                JsonSerializer.Serialize(w, e.Event, A2AJson.GetTypeInfo<StreamResponse>());
            }
            else
            {
                w.WriteBoolean("c", true);
            }
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static bool TryDecode(ReadOnlySpan<byte> data, out string? origin, out SequencedEvent? evt)
    {
        origin = null;
        evt = null;
        var reader = new Utf8JsonReader(data);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
        long? seq = null;
        StreamResponse? payload = null;
        var complete = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "o": origin = reader.GetString(); break;
                case "s": seq = reader.GetInt64(); break;
                case "c": complete = reader.GetBoolean(); break;
                case "e": payload = JsonSerializer.Deserialize(ref reader, A2AJson.GetTypeInfo<StreamResponse>()); break;
                default: reader.Skip(); break;
            }
        }
        if (origin is null) return false;
        if (complete) return true;
        if (seq is not { } s || payload is null) return false;
        evt = new SequencedEvent(s, payload);
        return true;
    }

    /// <summary>Escapes Redis glob characters so the prefix matches literally.</summary>
    private static string EscapeGlob(string value)
    {
        var sb = new StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            if (c is '*' or '?' or '[' or ']' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
