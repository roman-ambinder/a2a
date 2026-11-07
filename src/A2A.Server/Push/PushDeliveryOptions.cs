namespace A2A.Server.Push;

/// <summary>Retry and polling settings of <see cref="PushDeliveryService"/> (<see cref="A2AServerOptions.PushDelivery"/>).</summary>
public sealed class PushDeliveryOptions
{
    /// <summary>Wait before the first retry. Each later retry waits twice as long, up to <see cref="MaxBackoff"/>, ±20 % jitter. Default 1 second.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest wait between two attempts. Default 5 minutes.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Attempts per delivery, the first included. After the last one fails, the delivery is dead-lettered. Default 10.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>
    /// How long a leased delivery stays invisible to other instances. An instance that dies mid-send delays the delivery by
    /// at most this long. Keep it above <see cref="PushNotificationOptions.Timeout"/> plus the DNS check. Default 1 minute.
    /// </summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How often the outbox is checked for due deliveries (retries, and deliveries committed by other instances). Deliveries
    /// committed by this instance are picked up at once. Default 1 second.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Most deliveries leased and sent in parallel per poll. Default 50.</summary>
    public int BatchSize { get; set; } = 50;
}
