using A2A.Server;
using A2A.Server.Audit;
using A2A.Server.Postgres;
using A2A.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace A2A.Stores.Tests;

/// <summary>The PostgreSQL audit sink: one row per event, nothing lost on the way in, and its registration.</summary>
[Collection(ContainersCollection.Name)]
public sealed class PostgresAuditTests(PostgresFixture db)
{
    private static AgentCard Card() => new()
    {
        Name = "Test Agent",
        Description = "Test agent",
        Version = "1.0.0",
        SupportedInterfaces = [new AgentInterface { Url = "http://localhost/a2a", ProtocolBinding = ProtocolBindings.JsonRpc }],
        Capabilities = new AgentCapabilities(),
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "s", Name = "S", Description = "S", Tags = ["test"] }],
    };

    private static A2AAuditEvent Full() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Time = new DateTimeOffset(2026, 11, 9, 8, 30, 15, TimeSpan.Zero).AddTicks(1234567),
        Action = A2AAuditAction.ApprovalGranted,
        Outcome = A2AAuditOutcome.Failed,
        Actor = "alice",
        Tenant = "acme",
        TaskId = "task-1",
        Resource = "status-message-7",
        Operation = "SendMessage",
        Reason = "InvalidParams",
        Binding = "HTTP+JSON",
        RemoteAddress = "203.0.113.9",
        TraceId = "0af7651916cd43dd8448eb211c80319c",
    };

    private static async Task<List<object?[]>> RowsAsync(NpgsqlDataSource source, PostgresStoreOptions schema, string id)
    {
        await using var cmd = source.CreateCommand(
            $"SELECT id, time_ticks, action, outcome, actor, tenant, task_id, resource, operation, reason, binding, remote_address, trace_id FROM \"{schema.Schema}\".a2a_audit_log WHERE id = $1 ORDER BY seq");
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    [SkippableFact]
    public async Task Event_round_trips_with_every_field()
    {
        var schema = await db.NewSchemaAsync();
        var sink = new PostgresAuditSink(db.DataSource, schema);
        var e = Full();

        await sink.WriteAsync(e, CancellationToken.None);

        var row = Assert.Single(await RowsAsync(db.DataSource, schema, e.Id));
        Assert.Equal(
            [e.Id, e.Time.UtcTicks, "ApprovalGranted", "Failed", "alice", "acme", "task-1", "status-message-7", "SendMessage", "InvalidParams",
                "HTTP+JSON", "203.0.113.9", "0af7651916cd43dd8448eb211c80319c"],
            row);
    }

    [SkippableFact]
    public async Task Event_with_only_the_required_fields_stores_nulls()
    {
        var schema = await db.NewSchemaAsync();
        var sink = new PostgresAuditSink(db.DataSource, schema);
        var e = new A2AAuditEvent
        {
            Id = Guid.NewGuid().ToString("N"), Time = DateTimeOffset.UtcNow, Action = A2AAuditAction.AdminTaskDeleted,
            Outcome = A2AAuditOutcome.Succeeded, Actor = A2AAuditEvent.UnknownOperator, Tenant = "",
        };

        await sink.WriteAsync(e, CancellationToken.None);

        var row = Assert.Single(await RowsAsync(db.DataSource, schema, e.Id));
        Assert.Equal("", row[5]);
        Assert.All(row[6..], Assert.Null);
    }

    [SkippableFact]
    public async Task Rows_are_appended_in_order_and_a_final_row_keeps_the_id_of_its_attempt()
    {
        var schema = await db.NewSchemaAsync();
        var sink = new PostgresAuditSink(db.DataSource, schema);
        var attempt = Full() with { Outcome = A2AAuditOutcome.Attempted, Reason = null };

        await sink.WriteAsync(attempt, CancellationToken.None);
        await sink.WriteAsync(attempt with { Outcome = A2AAuditOutcome.Succeeded }, CancellationToken.None);

        var rows = await RowsAsync(db.DataSource, schema, attempt.Id);
        Assert.Equal(["Attempted", "Succeeded"], rows.Select(r => r[3]));
    }

    [SkippableFact]
    public async Task A_sanitized_event_with_a_NUL_is_stored_and_a_raw_one_is_refused_by_the_database()
    {
        var schema = await db.NewSchemaAsync();
        var sink = new PostgresAuditSink(db.DataSource, schema);
        var dirty = Full() with { TaskId = "t\0-1", Actor = "al\ud800ice" };

        await Assert.ThrowsAnyAsync<Exception>(async () => await sink.WriteAsync(dirty, CancellationToken.None));
        var clean = A2AAuditor.Sanitize(dirty);
        await sink.WriteAsync(clean, CancellationToken.None);

        var row = Assert.Single(await RowsAsync(db.DataSource, schema, clean.Id));
        Assert.Equal("t_-1", row[6]);
        Assert.Equal("al_ice", row[4]);
    }

    [SkippableFact]
    public async Task Schema_script_runs_twice()
    {
        var schema = await db.NewSchemaAsync(); // creates it once
        await PostgresSchema.EnsureSchemaAsync(db.DataSource, schema.Schema);

        await using var cmd = db.DataSource.CreateCommand(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = $1 AND tablename = 'a2a_audit_log'");
        cmd.Parameters.AddWithValue(schema.Schema);
        Assert.Equal(4L, await cmd.ExecuteScalarAsync()); // the primary key and the three lookup indexes
    }

    [SkippableFact]
    public async Task Registration_replaces_the_logger_sink_and_a_later_sink_replaces_it()
    {
        var schema = await db.NewSchemaAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddA2AServer(o => o.AgentCard = Card());
        Assert.IsType<LoggerAuditSink>(services.BuildServiceProvider().GetRequiredService<A2AAuditSink>());

        builder.AddPostgresAuditSink(db.DataSource, o => { o.Schema = schema.Schema; o.CreateSchemaOnStartup = false; });

        await using (var provider = services.BuildServiceProvider())
        {
            Assert.IsType<PostgresAuditSink>(provider.GetRequiredService<A2AAuditSink>());
            // The auditor the server uses writes to the table.
            var auditor = provider.GetRequiredService<A2AAuditor>();
            var e = Full() with { Outcome = A2AAuditOutcome.Succeeded };
            await auditor.RecordAsync(e);
            Assert.Single(await RowsAsync(db.DataSource, schema, e.Id));
        }

        builder.AddAuditSink(new RecordingSink());
        await using var later = services.BuildServiceProvider();
        Assert.IsType<RecordingSink>(later.GetRequiredService<A2AAuditSink>());
    }

    [SkippableFact]
    public async Task Registration_creates_the_table_at_startup()
    {
        db.SkipIfUnavailable();
        var schema = "t_" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddA2AServer(o => o.AgentCard = Card());
        services.AddA2APostgresAuditSink(db.DataSource, o => o.Schema = schema);
        await using var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>().Where(h => h.GetType().Name == "PostgresAuditHostedService"))
            await hosted.StartAsync(CancellationToken.None);

        var e = Full();
        await provider.GetRequiredService<A2AAuditSink>().WriteAsync(e, CancellationToken.None);
        Assert.Single(await RowsAsync(db.DataSource, new PostgresStoreOptions { Schema = schema }, e.Id));
    }

    private sealed class RecordingSink : A2AAuditSink
    {
        public override ValueTask WriteAsync(A2AAuditEvent auditEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
