namespace A2A.Server.Retention;

/// <summary>
/// Settings of the retention sweep (<see cref="A2ARetentionService"/>, added by <c>AddA2ARetention</c>). The interrupted-state
/// timeout is <see cref="A2AServerOptions.InterruptedTimeout"/>; the replay window for resumed streams stays
/// <see cref="StreamingOptions.TerminalRetention"/> and <see cref="StreamingOptions.ReplayBufferSize"/>.
/// </summary>
public sealed class A2ARetentionOptions
{
    /// <summary>Largest allowed <see cref="BatchSize"/>.</summary>
    public const int MaxBatchSize = 10_000;

    /// <summary>
    /// How long a task in a terminal state (completed, failed, canceled, rejected) is kept, from its status timestamp. After
    /// that it is deleted with its events, push configs, pending push deliveries and dead letters. Default 7 days.
    /// </summary>
    public TimeSpan TaskRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How long a context that no task uses any more is kept, from its creation. Default 7 days.</summary>
    public TimeSpan OrphanContextRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How often a sweep runs. With several instances, only one sweeps per interval (a cluster-wide lease of the
    /// <see cref="Coordination.ExecutionCoordinator"/>). Default 5 minutes.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Most items one step of a sweep reads at once (tasks, contexts or expired executions). 1 to <see cref="MaxBatchSize"/>. Default 500.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> unless <see cref="SweepInterval"/>, <see cref="TaskRetention"/> and
    /// <see cref="OrphanContextRetention"/> are positive, <see cref="SweepInterval"/> is at most 49 days (the longest timer
    /// period), and <see cref="BatchSize"/> is between 1 and <see cref="MaxBatchSize"/>.
    /// </summary>
    public void Validate()
    {
        if (SweepInterval <= TimeSpan.Zero || SweepInterval > MaxSweepInterval)
            throw new InvalidOperationException($"Retention.SweepInterval ({SweepInterval}) must be greater than zero and at most {MaxSweepInterval.TotalDays:0} days.");
        if (BatchSize is < 1 or > MaxBatchSize)
            throw new InvalidOperationException($"Retention.BatchSize ({BatchSize}) must be between 1 and {MaxBatchSize}.");
        if (TaskRetention <= TimeSpan.Zero)
            throw new InvalidOperationException($"Retention.TaskRetention ({TaskRetention}) must be greater than zero.");
        if (OrphanContextRetention <= TimeSpan.Zero)
            throw new InvalidOperationException($"Retention.OrphanContextRetention ({OrphanContextRetention}) must be greater than zero.");
    }

    /// <summary>The longest period a timer accepts (2^32 - 2 milliseconds), rounded down to whole days.</summary>
    private static readonly TimeSpan MaxSweepInterval = TimeSpan.FromDays(49);
}
