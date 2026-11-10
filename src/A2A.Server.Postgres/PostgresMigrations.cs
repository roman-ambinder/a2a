using System.Globalization;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace A2A.Server.Postgres;

/// <summary>One numbered schema migration: the text of an embedded <c>Migrations/NNNN_name.sql</c> file.</summary>
internal sealed record PostgresMigration(int Version, string Name, string Sql)
{
    /// <summary>The file name, for messages and comments.</summary>
    public string FileName => $"{Version:D4}_{Name}.sql";
}

/// <summary>The migrations of this package, read from the embedded <c>Migrations/*.sql</c> files.</summary>
internal static class PostgresMigrations
{
    private const string ResourcePrefix = "A2A.Server.Postgres.Migrations.";

    // The numbers start at 2, not 1. Every older build stamped 1 with a different (shorter) script, so a version 1 row does
    // not prove the tables are complete. A database at 1 therefore runs the idempotent baseline (2) like any other upgrade.
    internal const int FirstVersion = 2;

    private static readonly Lazy<IReadOnlyList<PostgresMigration>> s_all = new(Load);

    /// <summary>All migrations, in order. Throws <see cref="InvalidOperationException" /> if the files are not valid.</summary>
    public static IReadOnlyList<PostgresMigration> All => s_all.Value;

    private static IReadOnlyList<PostgresMigration> Load()
    {
        var assembly = typeof(PostgresMigrations).Assembly;
        var files = new List<(string FileName, string Sql)>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            files.Add((resource[ResourcePrefix.Length..], reader.ReadToEnd()));
        }

        return Parse(files);
    }

    /// <summary>Validates the files (name format, first number 2, contiguous, no duplicates) and returns them sorted.</summary>
    internal static IReadOnlyList<PostgresMigration> Parse(IEnumerable<(string FileName, string Sql)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var byVersion = new SortedDictionary<int, (string FileName, PostgresMigration Migration)>();
        foreach (var (fileName, sql) in files)
        {
            var (version, name) = ParseName(fileName);
            if (byVersion.TryGetValue(version, out var existing))
                throw new InvalidOperationException(
                    $"Two migrations have the number {version}: '{existing.FileName}' and '{fileName}'. Renumber the later one to the next free number.");
            byVersion.Add(version, (fileName, new PostgresMigration(version, name, sql)));
        }

        if (byVersion.Count == 0)
            throw new InvalidOperationException(
                "No migrations found. Expected embedded files named Migrations/NNNN_name.sql.");
        var expected = FirstVersion;
        foreach (var (version, entry) in byVersion)
        {
            if (version != expected)
                throw new InvalidOperationException(expected == FirstVersion
                    ? $"The first migration must be number {FirstVersion} (found '{entry.FileName}'). Numbers start at {FirstVersion} because older builds stamped version 1 with a different script."
                    : $"Migration numbers must be contiguous: expected {expected:D4} but found '{entry.FileName}'.");
            expected++;
        }

        return byVersion.Values.Select(v => v.Migration).ToList();
    }

    private static (int Version, string Name) ParseName(string fileName)
    {
        const string Extension = ".sql";
        var bad = new InvalidOperationException(
            $"Migration file name '{fileName}' is not valid. Use NNNN_short_name.sql: four digits, an underscore, a lower-case snake_case name.");
        if (fileName is null || !fileName.EndsWith(Extension, StringComparison.Ordinal)) throw bad;
        var stem = fileName[..^Extension.Length];
        if (stem.Length < 6 || stem[4] != '_') throw bad;
        for (var i = 0; i < 4; i++)
            if (stem[i] is < '0' or > '9')
                throw bad;
        var name = stem[5..];
        foreach (var c in name)
            if (c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
                throw bad;
        return (int.Parse(stem.AsSpan(0, 4), CultureInfo.InvariantCulture), name);
    }

    /// <summary>Creates the schema and the version table. The same text is the start of the exported script.</summary>
    internal static string Preamble(string quotedSchema)
    {
        return $"""
                CREATE SCHEMA IF NOT EXISTS {quotedSchema};

                CREATE TABLE IF NOT EXISTS {quotedSchema}.a2a_schema_version (
                    version int NOT NULL PRIMARY KEY
                );
                """;
    }

    internal static string Body(PostgresMigration migration, string quotedSchema)
    {
        return migration.Sql.Replace("{s}", quotedSchema, StringComparison.Ordinal);
    }

    internal static string Stamp(PostgresMigration migration, string quotedSchema)
    {
        return
            $"INSERT INTO {quotedSchema}.a2a_schema_version (version) VALUES ({migration.Version}) ON CONFLICT DO NOTHING;";
    }

    /// <summary>The version stored in the database: 0 when the table is missing or has no rows.</summary>
    internal static async Task<int> ReadVersionAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string schema,
        CancellationToken cancellationToken)
    {
        var table = PostgresSchema.Quote(schema) + ".a2a_schema_version";
        await using (var exists = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", conn, tx))
        {
            exists.Parameters.AddWithValue("t", table);
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!) return 0;
        }

        await using var cmd = new NpgsqlCommand($"SELECT coalesce(max(version), 0) FROM {table}", conn, tx);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }
}

/// <summary>Source-generated log messages of the schema startup step (event ids 1020-1029).</summary>
internal static partial class PostgresLog
{
    [LoggerMessage(EventId = 1020, Level = LogLevel.Warning,
        Message =
            "A2A Postgres schema \"{Schema}\" is at version {Found}, newer than this package knows ({Known}). Nothing was changed and the app continues. Upgrade the package.")]
    public static partial void SchemaNewer(ILogger logger, string schema, int found, int known);
}