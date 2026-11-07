using System.Text.RegularExpressions;

namespace A2A.Server.Postgres;

/// <summary>Settings for the PostgreSQL stores.</summary>
public sealed partial class PostgresStoreOptions
{
    private string _schema = "public";

    /// <summary>
    /// The database schema that holds the <c>a2a_*</c> tables. Default <c>public</c>. Letters, digits and underscores only
    /// (it is put into SQL text, so it is checked).
    /// </summary>
    public string Schema
    {
        get => _schema;
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            if (!IdentifierPattern().IsMatch(value))
                throw new ArgumentException("Schema must match [A-Za-z_][A-Za-z0-9_]* (at most 63 characters).", nameof(value));
            _schema = value;
        }
    }

    /// <summary>
    /// When true (the default), a hosted service creates the tables and indexes at startup if they are missing. Turn it off
    /// when you apply <see cref="PostgresSchema.Script"/> yourself (for example with your migration tool).
    /// </summary>
    public bool CreateSchemaOnStartup { get; set; } = true;

    /// <summary>
    /// How often a hosted service deletes expired rows: event logs of terminal tasks past their retention and finished
    /// idempotency entries past theirs. Expired rows are already invisible to reads; this only frees space.
    /// Default 5 minutes. <see cref="Timeout.InfiniteTimeSpan"/> or zero turns the cleanup off.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a running idempotent request may hold its key before another request may take it over. The in-memory store
    /// never expires a running entry, but a durable one would keep the key of a crashed instance forever, so this is bounded.
    /// Default 1 hour. <see cref="Timeout.InfiniteTimeSpan"/> means never.
    /// </summary>
    public TimeSpan IdempotencyClaimTimeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How often a request that waits on the same idempotency key (running elsewhere) checks whether it finished. Default 200 ms.
    /// </summary>
    public TimeSpan IdempotencyPollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
