using A2A.Server.Audit;
using Npgsql;
using static A2A.Server.Postgres.Sql;

namespace A2A.Server.Postgres;

/// <summary>
/// An <see cref="A2AAuditSink"/> that appends each event to the <c>a2a_audit_log</c> table with one parameterized INSERT. Append
/// only: it never updates or deletes a row, and nothing in the SDK expires them (keep or archive them as your rules say). Create
/// the table first (<see cref="PostgresSchema"/>, or the hosted service that <c>AddPostgresAuditSink</c> registers). Times are
/// stored as UTC ticks, like the other tables. If you manage the schema yourself, the app needs only INSERT on the table.
/// </summary>
public sealed class PostgresAuditSink : A2AAuditSink
{
    private readonly NpgsqlDataSource _db;
    private readonly string _insertSql;

    /// <summary>Creates the sink over <paramref name="dataSource"/>.</summary>
    /// <param name="dataSource">The database.</param>
    /// <param name="options">The schema; default <c>public</c>.</param>
    public PostgresAuditSink(NpgsqlDataSource dataSource, PostgresStoreOptions? options = null)
    {
        _db = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        var schema = PostgresSchema.Quote((options ?? new()).Schema);
        _insertSql =
            $"""
            INSERT INTO {schema}.a2a_audit_log
                (id, time_ticks, action, outcome, actor, tenant, task_id, resource, operation, reason, binding, remote_address, trace_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
            """;
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(A2AAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        await using var cmd = _db.CreateCommand(_insertSql);
        cmd.Parameters.Add(TextParam(auditEvent.Id));
        cmd.Parameters.Add(Long(auditEvent.Time.UtcTicks));
        cmd.Parameters.Add(TextParam(auditEvent.Action.ToString()));
        cmd.Parameters.Add(TextParam(auditEvent.Outcome.ToString()));
        cmd.Parameters.Add(TextParam(auditEvent.Actor));
        cmd.Parameters.Add(TextParam(auditEvent.Tenant));
        cmd.Parameters.Add(NullableText(auditEvent.TaskId));
        cmd.Parameters.Add(NullableText(auditEvent.Resource));
        cmd.Parameters.Add(NullableText(auditEvent.Operation));
        cmd.Parameters.Add(NullableText(auditEvent.Reason));
        cmd.Parameters.Add(NullableText(auditEvent.Binding));
        cmd.Parameters.Add(NullableText(auditEvent.RemoteAddress));
        cmd.Parameters.Add(NullableText(auditEvent.TraceId));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
