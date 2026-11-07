using Npgsql;

namespace A2A.Server.Postgres;

/// <summary>
/// The tables and indexes the PostgreSQL stores use. The script is idempotent (<c>IF NOT EXISTS</c> everywhere), so it is
/// safe to run on every start and from several instances at once (<see cref="EnsureSchemaAsync"/> takes an advisory lock).
/// </summary>
/// <remarks>
/// Times are stored as UTC ticks (<c>bigint</c>, 100 ns units, the same as <see cref="DateTimeOffset.UtcTicks"/>) so values
/// round-trip exactly and match the ListTasks page cursor. JSON payloads are stored as <c>text</c> so they round-trip byte
/// for byte. Text keys use the <c>"C"</c> collation, so ordering is ordinal, like the in-memory stores.
/// </remarks>
public static class PostgresSchema
{
    /// <summary>Version of the schema this package expects. Recorded in <c>a2a_schema_version</c>.</summary>
    public const int Version = 1;

    /// <summary>The schema script for the <c>public</c> schema. Use <see cref="ScriptFor"/> for another schema.</summary>
    public static string Script => ScriptFor("public");

    /// <summary>The schema script for <paramref name="schema"/>.</summary>
    public static string ScriptFor(string schema)
    {
        var s = new PostgresStoreOptions { Schema = schema }.Schema; // validates
        return Template.Replace("{s}", Quote(s), StringComparison.Ordinal);
    }

    /// <summary>Creates the tables and indexes if missing. Serialized across instances with a transaction-level advisory lock.</summary>
    public static async Task EnsureSchemaAsync(NpgsqlDataSource dataSource, string schema = "public", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var script = ScriptFor(schema);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('a2a_schema:' || @s))", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("s", schema);
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using (var cmd = new NpgsqlCommand(script, conn, tx))
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    internal static string Quote(string identifier) => "\"" + identifier + "\"";

    private const string Template = """
        CREATE SCHEMA IF NOT EXISTS {s};

        CREATE TABLE IF NOT EXISTS {s}.a2a_schema_version (
            version int NOT NULL PRIMARY KEY
        );
        INSERT INTO {s}.a2a_schema_version (version) VALUES (1) ON CONFLICT DO NOTHING;

        -- One row per task. status_ticks = (task.status.timestamp ?? created) in UTC ticks: the ListTasks sort key.
        CREATE TABLE IF NOT EXISTS {s}.a2a_tasks (
            tenant        text COLLATE "C" NOT NULL,
            task_id       text COLLATE "C" NOT NULL,
            owner         text COLLATE "C" NOT NULL,
            context_id    text COLLATE "C" NOT NULL,
            state         int    NOT NULL,
            status_ticks  bigint NOT NULL,
            version       bigint NOT NULL,
            created_ticks bigint NOT NULL,
            updated_ticks bigint NOT NULL,
            task          text   NOT NULL,
            PRIMARY KEY (tenant, task_id)
        );
        -- ListTasks: owner-scoped, newest first, keyset paging on (status_ticks, task_id).
        CREATE INDEX IF NOT EXISTS a2a_tasks_list_ix ON {s}.a2a_tasks (tenant, owner, status_ticks DESC, task_id);
        CREATE INDEX IF NOT EXISTS a2a_tasks_context_ix ON {s}.a2a_tasks (tenant, owner, context_id, status_ticks DESC, task_id);

        CREATE TABLE IF NOT EXISTS {s}.a2a_contexts (
            tenant        text COLLATE "C" NOT NULL,
            context_id    text COLLATE "C" NOT NULL,
            owner         text COLLATE "C" NOT NULL,
            created_ticks bigint NOT NULL,
            PRIMARY KEY (tenant, context_id)
        );

        -- Per-task event log header: the last sequence ever appended, and when a terminal task's log expires.
        CREATE TABLE IF NOT EXISTS {s}.a2a_event_logs (
            tenant      text COLLATE "C" NOT NULL,
            owner       text COLLATE "C" NOT NULL,
            task_id     text COLLATE "C" NOT NULL,
            last_seq    bigint NOT NULL,
            purge_ticks bigint NULL,
            PRIMARY KEY (tenant, owner, task_id)
        );
        CREATE INDEX IF NOT EXISTS a2a_event_logs_purge_ix ON {s}.a2a_event_logs (purge_ticks) WHERE purge_ticks IS NOT NULL;

        -- Retained events (the last ReplayBufferSize per task).
        CREATE TABLE IF NOT EXISTS {s}.a2a_events (
            tenant  text COLLATE "C" NOT NULL,
            owner   text COLLATE "C" NOT NULL,
            task_id text COLLATE "C" NOT NULL,
            seq     bigint NOT NULL,
            event   text   NOT NULL,
            PRIMARY KEY (tenant, owner, task_id, seq)
        );

        -- Push configs. config holds protected secrets (IPushSecretProtector), never plaintext.
        CREATE TABLE IF NOT EXISTS {s}.a2a_push_configs (
            tenant    text COLLATE "C" NOT NULL,
            owner     text COLLATE "C" NOT NULL,
            task_id   text COLLATE "C" NOT NULL,
            config_id text COLLATE "C" NOT NULL,
            config    text NOT NULL,
            PRIMARY KEY (tenant, owner, task_id, config_id)
        );

        -- Push deliveries waiting to be sent (PostgresPushOutbox), written with their event by the task commit; payload is the
        -- webhook body (the event JSON, UTF-8), so delivery does not depend on event-log retention. Only the lowest
        -- seq of a (task, config) is leased; lease_ticks hides a leased row from other instances until then.
        CREATE TABLE IF NOT EXISTS {s}.a2a_push_outbox (
            tenant       text COLLATE "C" NOT NULL,
            owner        text COLLATE "C" NOT NULL,
            task_id      text COLLATE "C" NOT NULL,
            config_id    text COLLATE "C" NOT NULL,
            seq          bigint NOT NULL,
            delivery_id  text   NOT NULL,
            trace_parent text   NULL,
            attempt      int    NOT NULL,
            payload      bytea  NOT NULL,
            due_ticks    bigint NOT NULL,
            lease_ticks  bigint NULL,
            last_error   text   NULL,
            PRIMARY KEY (tenant, owner, task_id, config_id, seq)
        );
        CREATE INDEX IF NOT EXISTS a2a_push_outbox_due_ix ON {s}.a2a_push_outbox (due_ticks);

        -- Push deliveries that failed for good, until re-driven (A2APushAdmin) or deleted with their task.
        CREATE TABLE IF NOT EXISTS {s}.a2a_push_dead_letters (
            tenant       text COLLATE "C" NOT NULL,
            owner        text COLLATE "C" NOT NULL,
            task_id      text COLLATE "C" NOT NULL,
            config_id    text COLLATE "C" NOT NULL,
            seq          bigint NOT NULL,
            delivery_id  text   NOT NULL,
            trace_parent text   NULL,
            attempt      int    NOT NULL,
            payload      bytea  NOT NULL,
            dead_ticks   bigint NOT NULL,
            last_error   text   NOT NULL,
            PRIMARY KEY (tenant, owner, task_id, config_id, seq)
        );
        CREATE INDEX IF NOT EXISTS a2a_push_dead_letters_list_ix ON {s}.a2a_push_dead_letters (tenant, dead_ticks);

        -- Idempotency keys. outcome is NULL while the request runs; claim_id identifies the running claim.
        CREATE TABLE IF NOT EXISTS {s}.a2a_idempotency (
            tenant        text COLLATE "C" NOT NULL,
            principal     text COLLATE "C" NOT NULL,
            key           text COLLATE "C" NOT NULL,
            hash          bytea  NOT NULL,
            claim_id      uuid   NOT NULL,
            claimed_ticks bigint NOT NULL,
            outcome       text   NULL,
            expires_ticks bigint NULL,
            PRIMARY KEY (tenant, principal, key)
        );
        CREATE INDEX IF NOT EXISTS a2a_idempotency_expires_ix ON {s}.a2a_idempotency (expires_ticks) WHERE expires_ticks IS NOT NULL;
        """;
}
