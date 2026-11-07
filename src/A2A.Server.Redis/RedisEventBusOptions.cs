namespace A2A.Server.Redis;

/// <summary>Settings for <see cref="RedisTaskEventBus"/>.</summary>
public sealed class RedisEventBusOptions
{
    /// <summary>
    /// Prefix of the Redis pub/sub channels. Each task publishes on <c>{prefix}{tenant}\u001f{taskId}</c>, and every instance
    /// subscribes to <c>{prefix}*</c>. Use a different prefix per deployment when several share one Redis. Default <c>a2a:events:</c>.
    /// </summary>
    public string ChannelPrefix { get; set; } = "a2a:events:";
}
