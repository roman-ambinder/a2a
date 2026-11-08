using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using A2A.Server.Coordination;
using A2A.Server.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace A2A.Server.Redis;

/// <summary>
/// An <see cref="ExecutionCoordinator"/> that every server instance on one Redis shares: a running check that covers the
/// whole cluster, cancel requests that reach the instance running the execution, and the leases of dead instances.
/// </summary>
/// <remarks>
/// <para>Every lease expiry is computed and compared inside Lua scripts on Redis <c>TIME</c>, never on the instance's clock,
/// so instances with skewed clocks agree on what has expired.</para>
/// <para>Keys, all under <see cref="RedisEventBusOptions.ChannelPrefix"/> (<c>{prefix}</c>):</para>
/// <list type="bullet">
/// <item><c>{prefix}lease:{task}</c>: a hash per task, execution id → expiry (Redis time, ms). Its <c>PX</c> TTL is renewed
/// with every heartbeat, so a task whose instances all died leaves nothing behind but its expiry entries.</item>
/// <item><c>{prefix}leases</c>: a sorted set of every lease (task + execution id), scored by expiry, for
/// <see cref="ListExpiredAsync"/>. An entry stays until the execution unregisters.</item>
/// <item><c>{prefix}sweep:{name}</c>: a sweep lease (<c>SET NX PX</c>).</item>
/// <item>Channel <c>{prefix}cancel</c>: cancel requests (pub/sub, at most once), tagged with the sender's origin id so a
/// coordinator never hands its own requests to its readers.</item>
/// </list>
/// <para>The scripts touch two keys at once, so a Redis Cluster deployment must keep the prefix in one hash slot (for
/// example <c>{a2a}:</c>). Call <see cref="StartAsync"/> before serving (the DI extension registers a hosted service that
/// does); reading <see cref="CancelRequestsAsync"/> also starts it.</para>
/// </remarks>
public sealed class RedisExecutionCoordinator : ExecutionCoordinator, IAsyncDisposable
{
    // Redis time in milliseconds, as `now`. Raw strings have no trailing newline, so parts are joined with "\n".
    // replicate_commands lets Redis < 5 write after TIME; later versions always do.
    private const string Now = """
        if redis.replicate_commands then redis.replicate_commands() end
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        """;

    // KEYS: lease hash, expiry set. ARGV: execution id, set member, TTL ms. Writes the lease and renews the hash's PX TTL.
    private const string Lease = """
        local ttl = tonumber(ARGV[3])
        local expiry = now + ttl
        redis.call('HSET', KEYS[1], ARGV[1], expiry)
        redis.call('ZADD', KEYS[2], expiry, ARGV[2])
        if redis.call('PTTL', KEYS[1]) < ttl then redis.call('PEXPIRE', KEYS[1], ttl) end
        return 1
        """;

    private const string RegisterScript = Now + "\n" + Lease;

    // Renews only a lease that exists and has not expired: a late heartbeat never brings an expired lease back. 1 when renewed.
    private const string HeartbeatScript = Now + "\n" + """
        local current = redis.call('HGET', KEYS[1], ARGV[1])
        if not current or tonumber(current) <= now then return 0 end
        """ + "\n" + Lease;

    private const string UnregisterScript = """
        redis.call('HDEL', KEYS[1], ARGV[1])
        redis.call('ZREM', KEYS[2], ARGV[2])
        return 1
        """;

    private const string IsRunningScript = Now + "\n" + """
        for _, expiry in ipairs(redis.call('HVALS', KEYS[1])) do
          if tonumber(expiry) > now then return 1 end
        end
        return 0
        """;

    private const string ListExpiredScript = Now + "\n" + """
        return redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', now, 'LIMIT', 0, tonumber(ARGV[1]))
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly string _prefix;
    private readonly RedisKey _expiries;
    private readonly RedisChannel _cancelChannel;
    private readonly long _ttlMs;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly List<Channel<TaskKey>> _cancelReaders = [];
    private readonly string _origin = Guid.NewGuid().ToString("N");
    private ChannelMessageQueue? _queue;

    /// <summary>
    /// Creates the coordinator over <paramref name="redis"/>. Only <see cref="RedisEventBusOptions.ChannelPrefix"/> is read
    /// from <paramref name="options"/>. Throws <see cref="InvalidOperationException"/> for invalid
    /// <paramref name="coordination"/> options (<see cref="CoordinationOptions.Validate"/>).
    /// </summary>
    public RedisExecutionCoordinator(IConnectionMultiplexer redis, RedisEventBusOptions? options = null, CoordinationOptions? coordination = null,
        ILogger<RedisExecutionCoordinator>? logger = null)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _prefix = (options ?? new()).ChannelPrefix;
        ArgumentException.ThrowIfNullOrEmpty(_prefix, nameof(RedisEventBusOptions.ChannelPrefix));
        coordination ??= new CoordinationOptions();
        coordination.Validate();
        _ttlMs = Math.Max(1, (long)Math.Ceiling(coordination.LeaseTtl.TotalMilliseconds));
        _expiries = _prefix + "leases";
        _cancelChannel = RedisChannel.Literal(_prefix + "cancel");
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Subscribes to the cancel requests of every instance. Idempotent.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _queue) is not null) return;
        var queue = await _redis.GetSubscriber().SubscribeAsync(_cancelChannel).WaitAsync(ct).ConfigureAwait(false);
        if (Interlocked.CompareExchange(ref _queue, queue, null) is not null)
        {
            await queue.UnsubscribeAsync().ConfigureAwait(false);
            return;
        }
        queue.OnMessage(OnCancelMessage);
    }

    /// <summary>Stops receiving cancel requests.</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _queue, null) is { } queue) await queue.UnsubscribeAsync().ConfigureAwait(false);
    }

    /// <summary>Same as <see cref="StopAsync"/>.</summary>
    public ValueTask DisposeAsync() => new(StopAsync());

    /// <inheritdoc />
    public override async ValueTask RegisterAsync(TaskKey task, string executionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        await EvaluateAsync(RegisterScript, LeaseKeys(task), LeaseArgs(task, executionId, withTtl: true), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask<bool> HeartbeatAsync(TaskKey task, string executionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        return (int)await EvaluateAsync(HeartbeatScript, LeaseKeys(task), LeaseArgs(task, executionId, withTtl: true), ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public override async ValueTask UnregisterAsync(TaskKey task, string executionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        await EvaluateAsync(UnregisterScript, LeaseKeys(task), LeaseArgs(task, executionId, withTtl: false), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask<bool> IsRunningAsync(TaskKey task, CancellationToken ct = default) =>
        (int)await EvaluateAsync(IsRunningScript, [LeaseKey(task)], [], ct).ConfigureAwait(false) == 1;

    /// <inheritdoc />
    /// <remarks>The message carries this coordinator's origin id, so its own readers ignore the echo.</remarks>
    public override async ValueTask RequestCancelAsync(TaskKey task, CancellationToken ct = default) =>
        await _redis.GetSubscriber().PublishAsync(_cancelChannel, Encode(task, _origin)).WaitAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public override async IAsyncEnumerable<TaskKey> CancelRequestsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<TaskKey>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate) _cancelReaders.Add(channel);
        try
        {
            await StartAsync(ct).ConfigureAwait(false);
            await foreach (var task in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return task;
        }
        finally
        {
            lock (_gate) _cancelReaders.Remove(channel);
        }
    }

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<(TaskKey Task, string ExecutionId)>> ListExpiredAsync(int max, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        if (max == 0) return [];
        var members = (RedisValue[])(await EvaluateAsync(ListExpiredScript, [_expiries], [max], ct).ConfigureAwait(false))!;
        var expired = new List<(TaskKey, string)>(members.Length);
        foreach (var member in members)
        {
            if (TryDecode(member, out var task, out var executionId) && executionId is not null) expired.Add((task, executionId));
            else _logger.LogWarning("Skipped a malformed A2A execution lease in Redis");
        }
        return expired;
    }

    /// <inheritdoc />
    public override async ValueTask<bool> TryAcquireSweepLeaseAsync(string name, TimeSpan duration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        var expiry = TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(duration.TotalMilliseconds)));
        return await _redis.GetDatabase().StringSetAsync(_prefix + "sweep:" + name, "1", expiry, When.NotExists).WaitAsync(ct).ConfigureAwait(false);
    }

    private Task<RedisResult> EvaluateAsync(string script, RedisKey[] keys, RedisValue[] args, CancellationToken ct) =>
        _redis.GetDatabase().ScriptEvaluateAsync(script, keys, args).WaitAsync(ct);

    private RedisKey LeaseKey(TaskKey task) => ((RedisKey)(_prefix + "lease:")).Append(Encode(task, null));

    private RedisKey[] LeaseKeys(TaskKey task) => [LeaseKey(task), _expiries];

    private RedisValue[] LeaseArgs(TaskKey task, string executionId, bool withTtl) =>
        withTtl ? [executionId, Encode(task, executionId), _ttlMs] : [executionId, Encode(task, executionId)];

    private void OnCancelMessage(ChannelMessage message)
    {
        if (!TryDecode(message.Message, out var task, out var origin))
        {
            _logger.LogWarning("Dropped a malformed A2A cancel request from Redis");
            return;
        }
        if (origin == _origin) return; // our own request: the sender cancels its own executions directly
        lock (_gate)
        {
            foreach (var reader in _cancelReaders) reader.Writer.TryWrite(task);
        }
    }

    /// <summary>
    /// <c>["tenant","owner","taskId"]</c>, plus a fourth string: the execution id of a lease entry, or the sender's origin id
    /// of a cancel request. JSON, so any character in an id is safe, and the same key always encodes to the same bytes.
    /// </summary>
    private static byte[] Encode(TaskKey task, string? extra)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            w.WriteStringValue(task.Tenant);
            w.WriteStringValue(task.Owner);
            w.WriteStringValue(task.TaskId);
            if (extra is not null) w.WriteStringValue(extra);
            w.WriteEndArray();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static bool TryDecode(RedisValue value, out TaskKey task, out string? extra)
    {
        task = default;
        extra = null;
        try
        {
            var reader = new Utf8JsonReader(((ReadOnlyMemory<byte>)value).Span);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return false;
            var parts = new List<string>(4);
            while (reader.Read() && reader.TokenType == JsonTokenType.String) parts.Add(reader.GetString()!);
            if (reader.TokenType != JsonTokenType.EndArray || parts.Count is < 3 or > 4) return false;
            task = new TaskKey(parts[0], parts[1], parts[2]);
            extra = parts.Count == 4 ? parts[3] : null;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
