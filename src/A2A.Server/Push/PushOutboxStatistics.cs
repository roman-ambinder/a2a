namespace A2A.Server.Push;

/// <summary>
/// Optional capability of a <see cref="PushOutbox"/>: cheap numbers about the whole outbox, for health checks. It is not
/// part of <see cref="PushOutbox"/> so that existing outboxes keep compiling. A feature that needs it fails at startup
/// when the registered outbox does not implement it.
/// </summary>
public interface IPushOutboxStatistics
{
    /// <summary>
    /// Reads the statistics of every tenant. Must be cheap: no scan over the whole outbox.
    /// </summary>
    /// <param name="maxDeadLetters">Where to stop counting dead letters. A value of zero or less gives a count of 0.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<PushOutboxStatistics> GetStatisticsAsync(int maxDeadLetters, CancellationToken cancellationToken = default);
}

/// <summary>Numbers about a <see cref="PushOutbox"/>, across all tenants.</summary>
/// <param name="OldestDueAge">
/// How long the entry that has been due the longest has been waiting, by the outbox's own clock. Zero when nothing is due.
/// Leased entries count. An entry waiting out a retry backoff is not due yet; entries queued behind it are.
/// </param>
/// <param name="DeadLetterCount">The number of dead letters, counted up to the limit given to <see cref="IPushOutboxStatistics.GetStatisticsAsync"/>.</param>
public readonly record struct PushOutboxStatistics(TimeSpan OldestDueAge, int DeadLetterCount);
