using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace A2A.Server.Postgres;

/// <summary>
///     The tables and indexes the PostgreSQL stores use, kept by numbered migrations (embedded
///     <c>Migrations/NNNN_name.sql</c>
///     files). The applied version is stored in <c>a2a_schema_version</c>. <see cref="EnsureSchemaAsync" /> applies the
///     missing
///     migrations in one transaction under an advisory lock, so many instances can start together. A database that is
///     newer
///     than the package is left alone. Hosts that apply the schema by hand use <see cref="ScriptFor(string, int)" />, and
///     <see cref="VerifySchemaAsync" /> checks the database without changing it.
/// </summary>
/// <remarks>
///     Times are stored as UTC ticks (<c>bigint</c>, 100 ns units, the same as <see cref="DateTimeOffset.UtcTicks" />) so
///     values
///     round-trip exactly and match the ListTasks page cursor. JSON payloads are stored as <c>text</c> so they round-trip
///     byte
///     for byte. Text keys use the <c>"C"</c> collation, so ordering is ordinal, like the in-memory stores.
/// </remarks>
public static class PostgresSchema
{
    /// <summary>Version of the schema this package expects: the number of its last migration.</summary>
    public static int Version => PostgresMigrations.All[^1].Version;

    /// <summary>The schema script for the <c>public</c> schema. Use <see cref="ScriptFor(string)" /> for another schema.</summary>
    public static string Script => ScriptFor("public");

    /// <summary>The script that creates the whole schema in <paramref name="schema" /> (from version 0). Re-runnable.</summary>
    public static string ScriptFor(string schema)
    {
        return ScriptFor(schema, 0);
    }

    /// <summary>
    ///     The script that brings <paramref name="schema" /> from <paramref name="fromVersion" /> to <see cref="Version" />:
    ///     only the
    ///     migrations above <paramref name="fromVersion" />. Run it in one transaction (for example <c>psql -1 -f</c>). It
    ///     takes
    ///     the same advisory lock as <see cref="EnsureSchemaAsync" /> and is safe to run again.
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <param name="fromVersion">The version the database is at now (0 for a new database).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="fromVersion" /> is below 0 or above
    ///     <see cref="Version" />.
    /// </exception>
    public static string ScriptFor(string schema, int fromVersion)
    {
        var s = new PostgresStoreOptions { Schema = schema }.Schema; // validates
        var all = PostgresMigrations.All;
        var target = all[^1].Version;
        ArgumentOutOfRangeException.ThrowIfNegative(fromVersion);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fromVersion, target);
        var q = Quote(s);
        var sb = new StringBuilder();
        sb.Append("-- A2A.Server.Postgres schema ").Append(s).Append(": from version ").Append(fromVersion)
            .Append(" to version ").Append(target).Append(".\n");
        sb.Append("-- Run in one transaction, for example: psql -1 -f this-file.sql\n\n");
        sb.Append("SELECT pg_advisory_xact_lock(hashtext('a2a_schema:").Append(s).Append("'));\n\n");
        sb.Append(PostgresMigrations.Preamble(q)).Append('\n');
        foreach (var m in all.Where(m => m.Version > fromVersion))
        {
            sb.Append("\n-- Migration ").Append(m.FileName).Append('\n');
            sb.Append(PostgresMigrations.Body(m, q).TrimEnd()).Append('\n');
            sb.Append(PostgresMigrations.Stamp(m, q)).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Applies the migrations the database is missing, in one transaction under a transaction-level advisory lock. Does
    ///     nothing
    ///     (no lock, no DDL) when the database is already up to date. A database newer than this package is left alone.
    /// </summary>
    /// <exception cref="InvalidOperationException">A migration failed. Nothing was changed.</exception>
    public static Task EnsureSchemaAsync(NpgsqlDataSource dataSource, string schema = "public",
        CancellationToken cancellationToken = default)
    {
        return ApplyAsync(dataSource, schema, PostgresMigrations.All, cancellationToken);
    }

    /// <summary>
    ///     Reads the schema version and throws unless it equals <see cref="Version" />. Changes nothing. Use it at startup
    ///     when you
    ///     apply the schema yourself.
    /// </summary>
    /// <exception cref="InvalidOperationException">The database is missing the schema, older, or newer than this package.</exception>
    public static async Task VerifySchemaAsync(NpgsqlDataSource dataSource, string schema = "public",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var s = new PostgresStoreOptions { Schema = schema }.Schema; // validates
        int found;
        await using (var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            found = await PostgresMigrations.ReadVersionAsync(conn, null, s, cancellationToken).ConfigureAwait(false);
        }

        var known = Version;
        if (found == known) return;
        var fix =
            $"apply PostgresSchema.ScriptFor(\"{s}\"{(found > 0 ? ", " + found : "")}) in one transaction, call PostgresSchema.EnsureSchemaAsync, or set CreateSchemaOnStartup = true";
        if (found == 0)
            throw new InvalidOperationException(
                $"The A2A schema \"{s}\" is not in the database (found version 0, this package needs version {known}). To fix this, {fix}.");
        if (found < known)
            throw new InvalidOperationException(
                $"The A2A schema \"{s}\" is at version {found}, but this package needs version {known}. To fix this, {fix}.");
        throw new InvalidOperationException(
            $"The A2A schema \"{s}\" is at version {found}, but this package only knows version {known}. Upgrade the package or restore the database. Nothing was changed.");
    }

    /// <summary>
    ///     Applies the migrations above the database version. Returns the version found before it ran (so the caller can see a
    ///     newer database). The fast path reads the version without a lock and does nothing when it is up to date.
    /// </summary>
    internal static async Task<int> ApplyAsync(NpgsqlDataSource dataSource, string schema,
        IReadOnlyList<PostgresMigration> migrations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(migrations);
        var s = new PostgresStoreOptions { Schema = schema }.Schema; // validates
        var q = Quote(s);
        var target = migrations[^1].Version;
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var found = await PostgresMigrations.ReadVersionAsync(conn, null, s, cancellationToken).ConfigureAwait(false);
        if (found >= target) return found;

        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lockCmd =
                     new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('a2a_schema:' || @s))", conn, tx)
                         { CommandTimeout = 0 })
        {
            lockCmd.Parameters.AddWithValue("s", schema);
            await lockCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var pre = new NpgsqlCommand(PostgresMigrations.Preamble(q), conn, tx) { CommandTimeout = 0 })
        {
            await pre.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        found = await PostgresMigrations.ReadVersionAsync(conn, tx, s, cancellationToken).ConfigureAwait(false);
        if (found >= target)
            return found; // another instance finished while we waited; the transaction ends without changes
        var current = found;
        foreach (var m in migrations.Where(m => m.Version > current))
        {
            try
            {
                await using var cmd = new NpgsqlCommand(PostgresMigrations.Body(m, q), conn, tx) { CommandTimeout = 0 };
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex)
            {
                throw new InvalidOperationException(
                    $"Migration {m.FileName} failed for schema \"{s}\": {ex.MessageText}. Nothing was changed.", ex);
            }

            await using var stamp = new NpgsqlCommand(PostgresMigrations.Stamp(m, q), conn, tx);
            await stamp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return found;
    }

    /// <summary>
    ///     The schema step of a hosted service's start. With <see cref="PostgresStoreOptions.CreateSchemaOnStartup" /> it
    ///     applies
    ///     the migrations (a newer database only logs a warning, because migrations are additive and failing would block a
    ///     rollback). Without it, it verifies the version and throws on a mismatch.
    /// </summary>
    internal static async Task PrepareForStartupAsync(NpgsqlDataSource dataSource, PostgresStoreOptions options,
        ILogger? logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CreateSchemaOnStartup)
        {
            var found = await ApplyAsync(dataSource, options.Schema, PostgresMigrations.All, cancellationToken)
                .ConfigureAwait(false);
            if (found > Version && logger is not null) PostgresLog.SchemaNewer(logger, options.Schema, found, Version);
        }
        else
        {
            await VerifySchemaAsync(dataSource, options.Schema, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string Quote(string identifier)
    {
        return "\"" + identifier + "\"";
    }
}