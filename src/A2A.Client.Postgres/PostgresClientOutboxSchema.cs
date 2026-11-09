using Npgsql;

namespace A2A.Client.Postgres;

/// <summary>
/// The table and indexes of <see cref="PostgresClientOutboxStore"/>. The script is idempotent (<c>IF NOT EXISTS</c> everywhere), so it is
/// safe to run on every start and from several instances at once (<see cref="EnsureSchemaAsync"/> takes an advisory lock).
/// </summary>
/// <remarks>
/// Times are UTC ticks (<c>bigint</c>, 100 ns units, the same as <see cref="DateTimeOffset.UtcTicks"/>). Text keys use the <c>"C"</c> collation, so ordering is
/// ordinal. The request and the result are stored as <c>bytea</c>, byte for byte. The status numbers are those of <see cref="A2A.Client.Outbox.ClientOutboxStatus"/>.
/// </remarks>
public static class PostgresClientOutboxSchema
{
    /// <summary>The schema script for the <c>public</c> schema. Use <see cref="ScriptFor"/> for another schema.</summary>
    public static string Script => ScriptFor("public");

    /// <summary>The schema script for <paramref name="schema"/>.</summary>
    public static string ScriptFor(string schema)
    {
        var s = new PostgresClientOutboxOptions { Schema = schema }.Schema; // validates
        return Template.Replace("{s}", Quote(s), StringComparison.Ordinal);
    }

    /// <summary>Creates the table and indexes if missing. Serialized across instances with a transaction-level advisory lock.</summary>
    public static async Task EnsureSchemaAsync(NpgsqlDataSource dataSource, string schema = "public", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var script = ScriptFor(schema);
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('a2a_client_outbox_schema:' || @s))", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("s", schema);
            await lockCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var cmd = new NpgsqlCommand(script, conn, tx))
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static string Quote(string identifier) => "\"" + identifier + "\"";

    internal static string Table(PostgresClientOutboxOptions? options) => $"{Quote((options ?? new()).Schema)}.a2a_client_outbox";

    private const string Template = """
        CREATE SCHEMA IF NOT EXISTS {s};

        -- One row per outgoing message. status: 0 queued, 1 tracking, 2 finished, 3 failed. request is the SendMessageRequest JSON and
        -- result the SendMessageResponse JSON (UTF-8). lease_ticks hides a leased row from other instances until then; lease_token
        -- fences the holder's writes (a holder whose lease was taken over changes nothing). in_doubt is set when a queued row is leased.
        CREATE TABLE IF NOT EXISTS {s}.a2a_client_outbox (
            id                text COLLATE "C" NOT NULL PRIMARY KEY,
            peer              text COLLATE "C" NOT NULL,
            correlation       text COLLATE "C" NULL,
            request           bytea  NOT NULL,
            status            int    NOT NULL,
            attempts          int    NOT NULL DEFAULT 0,
            in_doubt          boolean NOT NULL DEFAULT false,
            remote_task_id    text COLLATE "C" NULL,
            remote_context_id text COLLATE "C" NULL,
            remote_state      int    NULL,
            last_error        text   NULL,
            created_ticks     bigint NOT NULL,
            due_ticks         bigint NOT NULL,
            collected_ticks   bigint NULL,
            lease_ticks       bigint NULL,
            lease_token       uuid   NULL,
            result            bytea  NULL
        );
        -- Leasing: queued and tracking rows by due time.
        CREATE INDEX IF NOT EXISTS a2a_client_outbox_due_ix ON {s}.a2a_client_outbox (due_ticks, created_ticks, id) WHERE status IN (0, 1);
        -- Harvest: finished and failed rows that are not collected, oldest first.
        CREATE INDEX IF NOT EXISTS a2a_client_outbox_results_ix ON {s}.a2a_client_outbox (created_ticks, id) WHERE status IN (2, 3) AND collected_ticks IS NULL;
        -- A webhook hint finds the tracked row of a peer's task.
        CREATE INDEX IF NOT EXISTS a2a_client_outbox_remote_ix ON {s}.a2a_client_outbox (peer, remote_task_id) WHERE status = 1;
        -- Purge: collected rows, oldest first.
        CREATE INDEX IF NOT EXISTS a2a_client_outbox_purge_ix ON {s}.a2a_client_outbox (collected_ticks, id) WHERE collected_ticks IS NOT NULL;
        """;
}
