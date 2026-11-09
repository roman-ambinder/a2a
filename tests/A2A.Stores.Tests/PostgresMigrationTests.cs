using A2A.Server.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace A2A.Stores.Tests;

/// <summary>Parsing and export of the schema migrations. No database needed.</summary>
public sealed class PostgresMigrationParseTests
{
    private static (string, string)[] Files(params string[] names) => names.Select(n => (n, "SELECT 1;")).ToArray();

    [Fact]
    public void Parse_rejects_a_duplicate_number_and_names_both_files()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PostgresMigrations.Parse(Files("0002_a.sql", "0003_b.sql", "0003_c.sql")));
        Assert.Contains("0003_b.sql", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0003_c.sql", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Renumber", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_rejects_a_gap() =>
        Assert.Throws<InvalidOperationException>(() => PostgresMigrations.Parse(Files("0002_a.sql", "0004_b.sql")));

    [Theory]
    [InlineData("0001_a.sql")]
    [InlineData("0003_a.sql")]
    public void Parse_rejects_a_first_number_other_than_2(string name) =>
        Assert.Throws<InvalidOperationException>(() => PostgresMigrations.Parse(Files(name)));

    [Theory]
    [InlineData("2_a.sql")]
    [InlineData("0002a.sql")]
    [InlineData("0002_.sql")]
    [InlineData("0002_Upper.sql")]
    [InlineData("0002_a-b.sql")]
    [InlineData("0002_a.txt")]
    [InlineData("000x_a.sql")]
    public void Parse_rejects_a_bad_file_name(string name) =>
        Assert.Throws<InvalidOperationException>(() => PostgresMigrations.Parse(Files(name)));

    [Fact]
    public void Parse_sorts_valid_input()
    {
        var result = PostgresMigrations.Parse(Files("0004_c.sql", "0002_a.sql", "0003_b.sql"));
        Assert.Equal([2, 3, 4], result.Select(m => m.Version));
        Assert.Equal(["a", "b", "c"], result.Select(m => m.Name));
    }

    [Fact]
    public void Shipped_migrations_are_valid_and_set_the_version()
    {
        var all = PostgresMigrations.All;
        Assert.Equal(all[^1].Version, PostgresSchema.Version);
        Assert.Equal(2, all[0].Version);
        foreach (var m in all)
        {
            Assert.DoesNotContain("CONCURRENTLY", m.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("a2a_schema_version", m.Sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ScriptFor_from_the_current_version_has_no_migrations()
    {
        var script = PostgresSchema.ScriptFor("s1", PostgresSchema.Version);
        Assert.DoesNotContain("-- Migration", script, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_xact_lock(hashtext('a2a_schema:s1'))", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptFor_from_1_contains_the_baseline_and_stamps_it()
    {
        var script = PostgresSchema.ScriptFor("s1", 1);
        Assert.Contains("-- Migration 0002_baseline.sql", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"s1\".a2a_tasks", script, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO \"s1\".a2a_schema_version (version) VALUES (2) ON CONFLICT DO NOTHING;", script, StringComparison.Ordinal);
        Assert.Equal(PostgresSchema.ScriptFor("s1", 0), PostgresSchema.ScriptFor("s1"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1000)]
    public void ScriptFor_throws_for_a_version_out_of_range(int from) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgresSchema.ScriptFor("s1", from));
}

/// <summary>Applying, verifying and exporting the schema migrations against a real database.</summary>
[Collection(ContainersCollection.Name)]
public sealed class PostgresMigrationTests(PostgresFixture db)
{
    private static readonly string[] Tables =
    [
        "a2a_schema_version", "a2a_tasks", "a2a_contexts", "a2a_event_logs", "a2a_events", "a2a_push_configs", "a2a_push_outbox",
        "a2a_push_dead_letters", "a2a_idempotency", "a2a_execution_leases", "a2a_sweep_leases", "a2a_cancel_requests",
    ];

    private static string NewSchema() => "t_" + Guid.NewGuid().ToString("N");

    private async Task ExecAsync(string sql)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<int> VersionAsync(string schema) =>
        Convert.ToInt32(await ScalarAsync<object>($"SELECT coalesce(max(version), 0) FROM \"{schema}\".a2a_schema_version"));

    private Task<bool> TableExistsAsync(string schema, string table) =>
        ScalarAsync<bool>($"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = '{schema}' AND table_name = '{table}')");

    private Task<bool> ColumnExistsAsync(string schema, string table, string column) =>
        ScalarAsync<bool>($"SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{schema}' AND table_name = '{table}' AND column_name = '{column}')");

    private Task<bool> IndexExistsAsync(string schema, string index) =>
        ScalarAsync<bool>($"SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = '{schema}' AND indexname = '{index}')");

    private async Task AssertAllTablesAsync(string schema)
    {
        foreach (var table in Tables) Assert.True(await TableExistsAsync(schema, table), $"{table} is missing");
    }

    private static string FrozenV1(string schema)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "schema-v1.sql");
        return File.ReadAllText(path).Replace("{s}", "\"" + schema + "\"", StringComparison.Ordinal);
    }

    /// <summary>A schema as the last script that stamped version 1 left it, with one task row.</summary>
    private async Task<string> NewV1SchemaAsync()
    {
        db.SkipIfUnavailable();
        var schema = NewSchema();
        await ExecAsync(FrozenV1(schema));
        await ExecAsync($"INSERT INTO \"{schema}\".a2a_tasks (tenant, task_id, owner, context_id, state, status_ticks, version, created_ticks, updated_ticks, task) VALUES ('t', 'task1', 'alice', 'c1', 3, 1, 1, 1, 1, '{{}}')");
        return schema;
    }

    private static string Quoted(string schema) => "\"" + schema + "\"";

    [Fact]
    public async Task Fresh_schema_gets_the_version_and_every_table()
    {
        db.SkipIfUnavailable();
        var schema = NewSchema();

        await PostgresSchema.EnsureSchemaAsync(db.DataSource, schema);

        Assert.Equal(PostgresSchema.Version, await VersionAsync(schema));
        Assert.Equal(PostgresMigrations.All.Count, await ScalarAsync<long>($"SELECT count(*) FROM \"{schema}\".a2a_schema_version")); // one stamp per migration
        await AssertAllTablesAsync(schema);
    }

    [Fact]
    public async Task Upgrade_from_the_frozen_v1_keeps_the_data()
    {
        var schema = await NewV1SchemaAsync();
        Assert.Equal(1, await VersionAsync(schema));

        await PostgresSchema.EnsureSchemaAsync(db.DataSource, schema);

        Assert.Equal(PostgresSchema.Version, await VersionAsync(schema));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM \"{schema}\".a2a_tasks WHERE task_id = 'task1'"));
        await AssertAllTablesAsync(schema);
        await PostgresSchema.VerifySchemaAsync(db.DataSource, schema);
    }

    /// <summary>What <c>CREATE TABLE IF NOT EXISTS</c> alone could never fix: a column and indexes missing from an existing table.</summary>
    [Fact]
    public async Task Upgrade_from_an_early_v1_adds_columns_and_indexes_and_drops_the_legacy_index()
    {
        db.SkipIfUnavailable();
        var schema = NewSchema();
        var q = Quoted(schema);
        await ExecAsync($"""
            CREATE SCHEMA {q};
            CREATE TABLE {q}.a2a_schema_version (version int NOT NULL PRIMARY KEY);
            INSERT INTO {q}.a2a_schema_version (version) VALUES (1);
            CREATE TABLE {q}.a2a_tasks (
                tenant text COLLATE "C" NOT NULL, task_id text COLLATE "C" NOT NULL, owner text COLLATE "C" NOT NULL,
                context_id text COLLATE "C" NOT NULL, state int NOT NULL, status_ticks bigint NOT NULL, version bigint NOT NULL,
                created_ticks bigint NOT NULL, updated_ticks bigint NOT NULL, task text NOT NULL,
                PRIMARY KEY (tenant, task_id));
            CREATE INDEX a2a_tasks_state_ix ON {q}.a2a_tasks (state, status_ticks);
            CREATE TABLE {q}.a2a_push_outbox (
                tenant text COLLATE "C" NOT NULL, owner text COLLATE "C" NOT NULL, task_id text COLLATE "C" NOT NULL,
                config_id text COLLATE "C" NOT NULL, seq bigint NOT NULL, delivery_id text NOT NULL, trace_parent text NULL,
                attempt int NOT NULL, payload bytea NOT NULL, due_ticks bigint NOT NULL, lease_ticks bigint NULL,
                lease_token uuid NULL, last_error text NULL,
                PRIMARY KEY (tenant, owner, task_id, config_id, seq));
            """);
        Assert.False(await ColumnExistsAsync(schema, "a2a_push_outbox", "queued_ticks"));
        Assert.True(await IndexExistsAsync(schema, "a2a_tasks_state_ix"));

        await PostgresSchema.EnsureSchemaAsync(db.DataSource, schema);

        Assert.True(await ColumnExistsAsync(schema, "a2a_push_outbox", "queued_ticks"));
        foreach (var index in new[] { "a2a_tasks_terminal_ix", "a2a_tasks_interrupted_ix", "a2a_tasks_tenant_context_ix" })
            Assert.True(await IndexExistsAsync(schema, index), index);
        Assert.False(await IndexExistsAsync(schema, "a2a_tasks_state_ix"));
        Assert.True(await TableExistsAsync(schema, "a2a_execution_leases"));
        await PostgresSchema.VerifySchemaAsync(db.DataSource, schema);
    }

    private static PostgresMigration Next(int offset, string name, string sql) => new(PostgresSchema.Version + offset, name, sql);

    [Fact]
    public async Task A_future_migration_is_applied_once_and_stamped()
    {
        var schema = await NewV1SchemaAsync();
        await PostgresSchema.EnsureSchemaAsync(db.DataSource, schema);
        var future = Next(1, "add_note", "ALTER TABLE {s}.a2a_tasks ADD COLUMN note text NULL; CREATE INDEX a2a_tasks_note_ix ON {s}.a2a_tasks (note);");
        IReadOnlyList<PostgresMigration> migrations = [.. PostgresMigrations.All, future];

        var before = await PostgresSchema.ApplyAsync(db.DataSource, schema, migrations, default);
        Assert.Equal(PostgresSchema.Version, before);
        Assert.Equal(future.Version, await VersionAsync(schema));
        Assert.True(await ColumnExistsAsync(schema, "a2a_tasks", "note"));
        Assert.True(await IndexExistsAsync(schema, "a2a_tasks_note_ix"));

        // A second call runs nothing: the same non-idempotent migration would fail if it ran again.
        var again = await PostgresSchema.ApplyAsync(db.DataSource, schema, migrations, default);
        Assert.Equal(future.Version, again);
    }

    [Fact]
    public async Task A_failing_migration_rolls_everything_back_and_names_the_file()
    {
        var schema = await NewV1SchemaAsync();
        IReadOnlyList<PostgresMigration> migrations =
        [
            .. PostgresMigrations.All,
            Next(1, "good_one", "ALTER TABLE {s}.a2a_tasks ADD COLUMN good_col text NULL;"),
            Next(2, "bad_one", "ALTER TABLE {s}.no_such_table ADD COLUMN x text NULL;"),
        ];

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PostgresSchema.ApplyAsync(db.DataSource, schema, migrations, default));

        Assert.Contains($"{PostgresSchema.Version + 2:D4}_bad_one.sql", ex.Message, StringComparison.Ordinal);
        Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(1, await VersionAsync(schema));
        Assert.False(await ColumnExistsAsync(schema, "a2a_tasks", "good_col"));
    }

    [Fact]
    public async Task Twenty_concurrent_ensures_all_succeed()
    {
        var schema = await NewV1SchemaAsync();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => PostgresSchema.EnsureSchemaAsync(db.DataSource, schema)));

        Assert.Equal(PostgresSchema.Version, await VersionAsync(schema));
        await PostgresSchema.VerifySchemaAsync(db.DataSource, schema);
    }

    [Fact]
    public async Task Verify_passes_on_an_up_to_date_database()
    {
        var schema = (await db.NewSchemaAsync()).Schema;
        await PostgresSchema.VerifySchemaAsync(db.DataSource, schema);
    }

    [Fact]
    public async Task Verify_names_the_schema_the_versions_and_the_fix_when_the_schema_is_missing()
    {
        db.SkipIfUnavailable();
        var schema = NewSchema();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PostgresSchema.VerifySchemaAsync(db.DataSource, schema));

        Assert.Contains(schema, ex.Message, StringComparison.Ordinal);
        Assert.Contains("found version 0", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"version {PostgresSchema.Version}", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"PostgresSchema.ScriptFor(\"{schema}\")", ex.Message, StringComparison.Ordinal);
        Assert.Contains("EnsureSchemaAsync", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CreateSchemaOnStartup = true", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_names_the_schema_the_versions_and_the_fix_when_the_schema_is_older()
    {
        var schema = await NewV1SchemaAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PostgresSchema.VerifySchemaAsync(db.DataSource, schema));

        Assert.Contains(schema, ex.Message, StringComparison.Ordinal);
        Assert.Contains("at version 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"needs version {PostgresSchema.Version}", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"PostgresSchema.ScriptFor(\"{schema}\", 1)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("EnsureSchemaAsync", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CreateSchemaOnStartup = true", ex.Message, StringComparison.Ordinal);
    }

    private async Task<string> NewNewerSchemaAsync()
    {
        var schema = (await db.NewSchemaAsync()).Schema;
        await ExecAsync($"INSERT INTO \"{schema}\".a2a_schema_version (version) VALUES ({PostgresSchema.Version + 1})");
        return schema;
    }

    [Fact]
    public async Task Verify_names_the_schema_the_versions_and_the_fix_when_the_schema_is_newer()
    {
        var schema = await NewNewerSchemaAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PostgresSchema.VerifySchemaAsync(db.DataSource, schema));

        Assert.Contains(schema, ex.Message, StringComparison.Ordinal);
        Assert.Contains($"at version {PostgresSchema.Version + 1}", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"only knows version {PostgresSchema.Version}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Upgrade the package", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", ex.Message, StringComparison.Ordinal);
    }

    private PostgresStoreHostedService Service(string schema, bool create) =>
        new(db.DataSource, new PostgresStoreOptions { Schema = schema, CreateSchemaOnStartup = create, CleanupInterval = Timeout.InfiniteTimeSpan },
            new PostgresMaintenance(db.DataSource, new PostgresStoreOptions { Schema = schema }), null, NullLogger<PostgresStoreHostedService>.Instance);

    [Fact]
    public async Task Hosted_service_without_auto_create_refuses_to_start_on_an_old_schema()
    {
        var schema = await NewV1SchemaAsync();
        using var service = Service(schema, create: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(default));

        Assert.Equal(1, await VersionAsync(schema)); // and it changed nothing
    }

    [Fact]
    public async Task Hosted_service_with_auto_create_starts_on_a_newer_schema_and_changes_nothing()
    {
        var schema = await NewNewerSchemaAsync();
        using var service = Service(schema, create: true);

        await service.StartAsync(default);
        await service.StopAsync(default);

        Assert.Equal(PostgresSchema.Version + 1, await VersionAsync(schema));
        Assert.Equal(PostgresMigrations.All.Count + 1, await ScalarAsync<long>($"SELECT count(*) FROM \"{schema}\".a2a_schema_version"));
    }

    [Fact]
    public async Task Exported_script_builds_an_empty_schema_in_one_transaction_and_can_run_again()
    {
        db.SkipIfUnavailable();
        var schema = NewSchema();
        var script = PostgresSchema.ScriptFor(schema);

        for (var run = 0; run < 2; run++)
        {
            await using var conn = await db.DataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var cmd = new NpgsqlCommand(script, conn, tx)) await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            await PostgresSchema.VerifySchemaAsync(db.DataSource, schema);
        }
        await AssertAllTablesAsync(schema);
    }

    [Fact]
    public async Task Exported_upgrade_script_brings_the_frozen_v1_to_the_current_version()
    {
        var schema = await NewV1SchemaAsync();

        await using var conn = await db.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(PostgresSchema.ScriptFor(schema, 1), conn, tx)) await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();

        await PostgresSchema.VerifySchemaAsync(db.DataSource, schema);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM \"{schema}\".a2a_tasks"));
    }
}
