namespace A2A.Client.Outbox;

/// <summary>
/// Settings of a <see cref="ClientOutboxDispatcher"/>. They are checked when the dispatcher is created.
/// </summary>
public sealed class ClientOutboxOptions
{
    /// <summary>How often the dispatcher looks for due entries. Default 1 second. <see cref="Timeout.InfiniteTimeSpan"/> turns the timer off; then call <see cref="ClientOutboxDispatcher.RunOnceAsync"/> yourself.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The most entries one sweep leases. Default 50.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>The most entries of a sweep that run at the same time. Default 8.</summary>
    public int MaxConcurrency { get; set; } = 8;

    /// <summary>
    /// How long a leased entry is hidden from other dispatchers. A dispatcher that dies only delays its entries by this long.
    /// Default 5 minutes. Must be at least twice <see cref="CallTimeout"/>.
    /// </summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The longest one send or one <c>GetTask</c> may take. A send that times out is in doubt. Default 60 seconds.</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>First wait before a send is tried again. It doubles with every attempt, with jitter, up to <see cref="RetryMaxDelay"/>. A <c>Retry-After</c> from the peer is used instead. Default 1 second.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between two sends (not a <c>Retry-After</c>, which is used as given). Default 5 minutes.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>First wait between two checks of a task (<c>GetTask</c>). It doubles with every check up to <see cref="MaxPollInterval"/>. Default 5 seconds.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait between two checks of a task. Default 5 minutes.</summary>
    public TimeSpan MaxPollInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long, counted from enqueue, an entry may wait to be sent. After that it is <see cref="ClientOutboxStatus.Failed"/>.
    /// Default 24 hours, the same as the SDK server's default idempotency retention (<c>IdempotencyOptions.Retention</c>). Keep it inside
    /// the retention of the peer's idempotency keys: after that the peer no longer recognizes the key, and a resend of an in-doubt
    /// send could create a second task. If your peer keeps keys for less, lower this value. <see cref="Timeout.InfiniteTimeSpan"/> means no limit.
    /// </summary>
    public TimeSpan MaxQueueAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long, counted from enqueue, a task may be tracked. After that the entry is <see cref="ClientOutboxStatus.Failed"/> and keeps its <see cref="ClientOutboxEntry.RemoteTaskId"/>. Default 7 days. <see cref="Timeout.InfiniteTimeSpan"/> means no limit.</summary>
    public TimeSpan MaxTrackingAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// What to do with an entry whose last send may have reached the peer (<see cref="ClientOutboxEntry.InDoubt"/>). True (the default):
    /// send again with the same key; the peer deduplicates, but a peer that ignores the key may create a second task. False: the entry
    /// becomes <see cref="ClientOutboxStatus.Failed"/> and the app decides. That gives at most once.
    /// </summary>
    public bool RetryInDoubt { get; set; } = true;

    /// <summary>How long a collected entry is kept before it is purged. Also how long its id stays deduplicated. Default 7 days.</summary>
    public TimeSpan CollectedRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How often collected entries past their retention are purged. Default 1 hour. <see cref="Timeout.InfiniteTimeSpan"/> turns it off.</summary>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(1);

    internal void Validate()
    {
        NeedPositiveOrInfinite(SweepInterval, nameof(SweepInterval));
        if (BatchSize < 1) Bad(nameof(BatchSize), "must be at least 1");
        if (MaxConcurrency < 1) Bad(nameof(MaxConcurrency), "must be at least 1");
        NeedPositiveFinite(CallTimeout, nameof(CallTimeout));
        NeedPositiveFinite(Lease, nameof(Lease));
        if (Lease < CallTimeout + CallTimeout) Bad(nameof(Lease), "must be at least twice CallTimeout, so a slow call cannot outlive its lease");
        NeedPositiveFinite(RetryBaseDelay, nameof(RetryBaseDelay));
        NeedPositiveFinite(RetryMaxDelay, nameof(RetryMaxDelay));
        if (RetryMaxDelay < RetryBaseDelay) Bad(nameof(RetryMaxDelay), "must be at least RetryBaseDelay");
        NeedPositiveFinite(PollInterval, nameof(PollInterval));
        NeedPositiveFinite(MaxPollInterval, nameof(MaxPollInterval));
        if (MaxPollInterval < PollInterval) Bad(nameof(MaxPollInterval), "must be at least PollInterval");
        NeedPositiveOrInfinite(MaxQueueAge, nameof(MaxQueueAge));
        NeedPositiveOrInfinite(MaxTrackingAge, nameof(MaxTrackingAge));
        if (CollectedRetention < TimeSpan.Zero || CollectedRetention == Timeout.InfiniteTimeSpan) Bad(nameof(CollectedRetention), "must be zero or positive, and finite");
        NeedPositiveOrInfinite(PurgeInterval, nameof(PurgeInterval));
    }

    private static void NeedPositiveFinite(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero) Bad(name, "must be positive and finite");
    }

    private static void NeedPositiveOrInfinite(TimeSpan value, string name)
    {
        if (value != Timeout.InfiniteTimeSpan && value <= TimeSpan.Zero) Bad(name, "must be positive, or Timeout.InfiniteTimeSpan");
    }

    private static void Bad(string name, string rule) => throw new ArgumentException($"ClientOutboxOptions.{name} {rule}.");
}
