using System.Text.RegularExpressions;

namespace A2A.Client.Postgres;

/// <summary>Settings of <see cref="PostgresClientOutboxStore" /> and <see cref="PostgresClientOutboxSchema" />.</summary>
public sealed partial class PostgresClientOutboxOptions
{
    private string _schema = "public";

    /// <summary>
    ///     The database schema that holds the <c>a2a_client_outbox</c> table. Default <c>public</c>. Letters, digits and
    ///     underscores only
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
    ///     When true (the default), due times, leases and ages are decided by the database's clock (
    ///     <c>statement_timestamp()</c>), so
    ///     application instances whose clocks disagree still agree on what is due. When false, the store uses the
    ///     <see cref="TimeProvider" /> it is given, which is meant for tests that move a fake clock.
    /// </summary>
    public bool UseDatabaseClock { get; set; } = true;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}