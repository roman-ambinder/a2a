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
    public static async Task EnsureSchemaAsync(NpgsqlDataSource dataSource, string schema = "public", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var script = ScriptFor(schema);
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('a2a_schema:' || @s))", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("s", schema);
            await lockCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var cmd = new NpgsqlCommand(script, conn, tx))
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
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
        -- Retention sweeps (all tenants and owners): tasks by state and age, and "does any task still use this context".
        DROP INDEX IF EXISTS {s}.a2a_tasks_state_ix;
        -- state 3,4,5,7 = completed, failed, canceled, rejected; 6,8 = input-required, auth-required (TaskState values).
        CREATE INDEX IF NOT EXISTS a2a_tasks_terminal_ix ON {s}.a2a_tasks (status_ticks, tenant, task_id) WHERE state IN (3, 4, 5, 7);
        CREATE INDEX IF NOT EXISTS a2a_tasks_interrupted_ix ON {s}.a2a_tasks (status_ticks, tenant, task_id) WHERE state IN (6, 8);
        CREATE INDEX IF NOT EXISTS a2a_tasks_tenant_context_ix ON {s}.a2a_tasks (tenant, context_id);

        CREATE TABLE IF NOT EXISTS {s}.a2a_contexts (
            tenant        text COLLATE "C" NOT NULL,
            context_id    text COLLATE "C" NOT NULL,
            owner         text COLLATE "C" NOT NULL,
            created_ticks bigint NOT NULL,
            PRIMARY KEY (tenant, context_id)
        );
        -- Orphan-context sweep: oldest first.
        CREATE INDEX IF NOT EXISTS a2a_contexts_created_ix ON {s}.a2a_contexts (created_ticks, tenant, context_id);

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
        -- seq of a (task, config) is leased; lease_ticks hides a leased row from other instances until then, and lease_token
        -- fences the holder's result writes (a holder whose lease was taken over changes nothing).
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
            lease_token  uuid   NULL,
            last_error   text   NULL,
            queued_ticks bigint NULL,
            PRIMARY KEY (tenant, owner, task_id, config_id, seq)
        );
        -- Added after the first release of the table: nullable, no default, so it is safe on existing rows (a NULL is read as
        -- due_ticks). queued_ticks is when the delivery was queued; PushDeliveryOptions.MaxDeliveryAge counts from it.
        ALTER TABLE {s}.a2a_push_outbox ADD COLUMN IF NOT EXISTS queued_ticks bigint NULL;
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

        -- Execution leases (PostgresExecutionCoordinator): one row per running execution, renewed by its heartbeat. A row whose
        -- expiry_ticks is in the past belongs to an execution whose instance died; it stays until its execution unregisters.
        CREATE TABLE IF NOT EXISTS {s}.a2a_execution_leases (
            tenant       text COLLATE "C" NOT NULL,
            owner        text COLLATE "C" NOT NULL,
            task_id      text COLLATE "C" NOT NULL,
            execution_id text COLLATE "C" NOT NULL,
            expiry_ticks bigint NOT NULL,
            PRIMARY KEY (tenant, owner, task_id, execution_id)
        );
        -- ListExpiredAsync: longest expired first.
        CREATE INDEX IF NOT EXISTS a2a_execution_leases_expiry_ix ON {s}.a2a_execution_leases (expiry_ticks);

        -- Named cluster-wide leases of periodic jobs (the retention sweep).
        CREATE TABLE IF NOT EXISTS {s}.a2a_sweep_leases (
            name         text COLLATE "C" NOT NULL PRIMARY KEY,
            expiry_ticks bigint NOT NULL
        );

        -- Cancel requests between instances. Every instance polls the table and skips its own origin; a row is deleted by a
        -- later request once it is older than the retention (a few minutes), so the table stays small.
        CREATE TABLE IF NOT EXISTS {s}.a2a_cancel_requests (
            id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            tenant        text COLLATE "C" NOT NULL,
            owner         text COLLATE "C" NOT NULL,
            task_id       text COLLATE "C" NOT NULL,
            origin        text   NOT NULL,
            created_ticks bigint NOT NULL
        );
        CREATE INDEX IF NOT EXISTS a2a_cancel_requests_created_ix ON {s}.a2a_cancel_requests (created_ticks);
        """;
}
