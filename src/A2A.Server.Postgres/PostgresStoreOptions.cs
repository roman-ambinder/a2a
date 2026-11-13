using System.Text.RegularExpressions;

namespace A2A.Server.Postgres;

/// <summary>Settings for the PostgreSQL stores.</summary>
public sealed partial class PostgresStoreOptions
{
    private TimeSpan _cancelPollInterval = TimeSpan.FromSeconds(1);
    private string _schema = "public";

    /// <summary>
    ///     The database schema that holds the <c>a2a_*</c> tables. Default <c>public</c>. Letters, digits and underscores only
    ///     (it is put into SQL text, so it is checked).
    /// </summary>
    public string Schema
    {
        get => _schema;
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            if (!IdentifierPattern().IsMatch(value))
                throw new ArgumentException("Schema must match [A-Za-z_][A-Za-z0-9_]* (at most 63 characters).",
                    nameof(value));
            _schema = value;
        }
    }

    /// <summary>
    ///     When true (the default), a hosted service applies the missing schema migrations at startup, in one transaction
    ///     under an
    ///     advisory lock. A database newer than the package only logs a warning. When false, the service changes nothing and
    ///     refuses to start unless the database version equals <see cref="PostgresSchema.Version" />: apply
    ///     <see cref="PostgresSchema.ScriptFor(string, int)" /> yourself (for example with your migration tool).
    /// </summary>
    public bool CreateSchemaOnStartup { get; set; } = true;

    /// <summary>
    ///     How often a hosted service deletes expired rows: event logs of terminal tasks past their retention and finished
    ///     idempotency entries past theirs. Expired rows are already invisible to reads; this only frees space.
    ///     Default 5 minutes. <see cref="Timeout.InfiniteTimeSpan" /> or zero turns the cleanup off.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     How long a running idempotent request may hold its key before another request may take it over. The in-memory store
    ///     never expires a running entry, but a durable one would keep the key of a crashed instance forever, so this is
    ///     bounded.
    ///     A caller that holds a claim for longer keeps it by calling
    ///     <see cref="A2A.Server.Idempotency.IdempotencyStore.RenewAsync" />
    ///     more often than this (the execution coordinator's lease heartbeat is meant to), so the timeout only has to cover a
    ///     missed heartbeat or two and a crashed instance releases its keys quickly. Default 2 minutes (it was 1 hour).
    ///     <see cref="Timeout.InfiniteTimeSpan" /> means never.
    /// </summary>
    public TimeSpan IdempotencyClaimTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     How often a request that waits on the same idempotency key (running elsewhere) checks whether it finished. Default
    ///     200 ms.
    /// </summary>
    public TimeSpan IdempotencyPollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    ///     When true (the default), deadlines are decided by the database's clock (<c>statement_timestamp()</c>): push outbox
    ///     due
    ///     times and leases, idempotency claim timeouts and retention, and event log retention. Instances that share the
    ///     database
    ///     then agree on what has expired even when their own clocks are off. When false, the stores use the
    ///     <see cref="TimeProvider" /> they are given, which is meant for tests that move a fake clock.
    ///     Task timestamps (<c>UpdatedAt</c>, status times) always come from the instance's clock, because they are part of
    ///     the task.
    /// </summary>
    public bool UseDatabaseClock { get; set; } = true;

    /// <summary>
    ///     Whether the stores registration also registers the <see cref="PostgresExecutionCoordinator" /> (unless another
    ///     <see cref="A2A.Server.Coordination.ExecutionCoordinator" />, for example the Redis one, is already registered).
    ///     Default true.
    ///     With the process-local default coordinator, a crash loses every lease and the retention sweep never recovers the
    ///     tasks
    ///     that were running. Set to false to keep the coordinator you register yourself.
    /// </summary>
    public bool RegisterCoordinator { get; set; } = true;

    /// <summary>
    ///     How often the coordinator reads the cancel requests of the other instances. A plain poll, so it works through
    ///     connection
    ///     poolers such as PgBouncer. It is the delay of a cross-instance CancelTask. Default 1 second. Must be positive.
    /// </summary>
    public TimeSpan CancelPollInterval
    {
        get => _cancelPollInterval;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _cancelPollInterval = value;
        }
    }

    /// <summary>
    ///     Stores the parts of each task's artifacts in their own rows (<c>a2a_artifact_parts</c>, migration 0007), so an
    ///     append to an artifact writes only its new parts instead of rewriting the whole task. Default false.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Turn it on only when every instance that shares the database runs a version that has this option. An older
    ///         version reads such a task with empty parts, and its writes leave the rows stale.
    ///     </para>
    ///     <para>
    ///         Reads handle both forms whatever this says. With the option off, every write stores the parts in the task
    ///         again. Before downgrading to a version without this option, turn it off and call
    ///         <see cref="PostgresTaskStore.InlineArtifactPartsAsync" /> until it returns 0.
    ///     </para>
    /// </remarks>
    public bool ArtifactRows { get; set; }

    /// <summary>
    ///     With <see cref="ArtifactRows" />: once an artifact has this many rows, the next write of that artifact stores all
    ///     its parts in one row again. This bounds the rows of a long stream. Default 256. Must be at least 2.
    /// </summary>
    public int ArtifactRowsCompactAt
    {
        get => _artifactRowsCompactAt;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 2);
            _artifactRowsCompactAt = value;
        }
    }

    private int _artifactRowsCompactAt = 256;

    /// <summary>
    ///     Stores each task's <see cref="AgentTask.History" /> messages and A2A 1.1 <see cref="AgentTask.Timeline" /> entries
    ///     in their own rows (<c>a2a_task_entries</c>, migration 0008, one row per message or entry), so a status update or
    ///     a follow-up message writes only its new message and timeline entry instead of rewriting the whole task. The
    ///     timeline keeps its newest <see cref="A2ALimits.MaxTimelineEntries" /> entries in rows too: the oldest rows are
    ///     deleted as the engine drops those entries. Default false.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Turn it on only when every instance that shares the database runs a version that has this option. An older
    ///         version reads such a task with an empty history and timeline, and its writes leave the rows stale.
    ///     </para>
    ///     <para>
    ///         Reads handle both forms whatever this says. With the option off, every write stores the history and timeline
    ///         in the task again. Before downgrading to a version without this option, turn it off and call
    ///         <see cref="PostgresTaskStore.InlineHistoryAndTimelineAsync" /> until it returns 0. It is independent of
    ///         <see cref="ArtifactRows" />: either can be on without the other.
    ///     </para>
    /// </remarks>
    public bool HistoryRows { get; set; }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}