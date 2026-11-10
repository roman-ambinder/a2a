namespace A2A.Server.Redis;

/// <summary>Settings for <see cref="RedisTaskEventBus" />.</summary>
public sealed class RedisEventBusOptions
{
    /// <summary>
    ///     Prefix of the Redis pub/sub channels. Each task publishes on <c>{prefix}{tenant}\u001f{taskId}</c>, and every
    ///     instance
    ///     subscribes to <c>{prefix}*</c>. Use a different prefix per deployment when several share one Redis. Default
    ///     <c>a2a:events:</c>.
    /// </summary>
    public string ChannelPrefix { get; set; } = "a2a:events:";

    /// <summary>
    ///     Whether <c>AddRedisEventBus</c> also registers the <see cref="RedisExecutionCoordinator" /> (running executions,
    ///     cancel
    ///     requests and expired leases shared by every instance) over the same connection and prefix. Default true. Ignored by
    ///     <c>AddRedisExecutionCoordinator</c>.
    /// </summary>
    public bool RegisterCoordinator { get; set; } = true;
}