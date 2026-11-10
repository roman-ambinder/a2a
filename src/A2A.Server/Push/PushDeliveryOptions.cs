using A2A.Server.Storage;

namespace A2A.Server.Push;

/// <summary>
///     Retry and polling settings of <see cref="PushDeliveryService" /> (<see cref="A2AServerOptions.PushDelivery" />
///     ).
/// </summary>
public sealed class PushDeliveryOptions
{
    /// <summary>
    ///     Wait before the first retry. Each later retry waits twice as long, up to <see cref="MaxBackoff" />, ±20 %
    ///     jitter. Default 1 second.
    /// </summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest wait between two attempts. Default 5 minutes.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Attempts per delivery, the first included. When set, the delivery is dead-lettered after the last one fails.
    ///     Default
    ///     null: no limit on the count; <see cref="MaxDeliveryAge" /> ends a delivery that keeps failing.
    /// </summary>
    public int? MaxAttempts { get; set; }

    /// <summary>
    ///     How long a delivery may stay in the outbox, measured from when it was queued (by the outbox's clock: the database
    ///     clock
    ///     for PostgreSQL). A delivery that is older when it is next leased is dead-lettered without another attempt, and
    ///     keeps
    ///     its last error. A re-driven dead letter starts again with age 0. Null: no age limit. Default 24 hours.
    /// </summary>
    public TimeSpan? MaxDeliveryAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    ///     Called after a delivery was dead-lettered (too many attempts, too old, refused by the SSRF policy, a 4xx answer,
    ///     secrets
    ///     that cannot be unprotected). It gets the task, config id, sequence and last error, never secrets; use it to alert
    ///     or
    ///     to re-drive (<see cref="A2APushAdmin" />). An exception it throws is logged and has no other effect. The send slot
    ///     is
    ///     held while it runs, so keep it short. Default null.
    /// </summary>
    public Func<PushDeadLetterInfo, ValueTask>? OnDeadLetter { get; set; }

    /// <summary>
    ///     How long a leased delivery stays invisible to other instances. An instance that dies mid-send delays the delivery
    ///     by
    ///     at most this long. Must be longer than 2 × <see cref="PushNotificationOptions.Timeout" /> + 5 s (one DNS check and
    ///     one
    ///     POST), checked when the service starts. Default 1 minute.
    /// </summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     How often the outbox is checked for due deliveries (retries, and deliveries committed by other instances).
    ///     Deliveries
    ///     committed by this instance are picked up at once. Default 1 second.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     Most deliveries sent at the same time by one instance. Each send holds one slot; a freed slot is filled at once, so
    ///     one
    ///     slow receiver does not hold up the others. Default 50.
    /// </summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    ///     Most deliveries of one caller (the task owner, across tenants and instances) in flight at once, so one caller's
    ///     slow
    ///     webhooks cannot take every send slot. All anonymous callers count as one caller. It is checked when deliveries are
    ///     leased, so several instances can pass it briefly by a small amount (a soft cap). Null for no cap. Must be at least
    ///     1.
    ///     Default 20.
    /// </summary>
    public int? MaxInFlightPerCaller { get; set; } = 20;
}

/// <summary>
///     What <see cref="PushDeliveryOptions.OnDeadLetter" /> is told about a dead-lettered delivery. Never holds
///     secrets.
/// </summary>
/// <param name="Task">The task the delivery belongs to.</param>
/// <param name="ConfigId">The push config the delivery was for.</param>
/// <param name="Sequence">The event sequence of the delivery.</param>
/// <param name="LastError">Why the delivery was dead-lettered or why its last attempt failed.</param>
public sealed record PushDeadLetterInfo(TaskKey Task, string ConfigId, long Sequence, string? LastError);