using System.Globalization;
using System.Text;
using A2A.Json;
using A2A.Server.Idempotency;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Npgsql;
using NpgsqlTypes;
using static A2A.Server.Postgres.Sql;

namespace A2A.Server.Postgres;

/// <summary>
///     PostgreSQL <see cref="TaskStore" />. Same behavior as <see cref="InMemoryTaskStore" />: owner- and tenant-scoped
///     reads,
///     optimistic concurrency on <see cref="TaskRecord.Version" />, ListTasks newest first with keyset paging.
///     <see cref="CommitAsync" /> writes the task row, the event log (the tables <see cref="PostgresEventStore" /> reads)
///     and the
///     push deliveries (the outbox <see cref="PostgresPushOutbox" /> reads) in one transaction, so give these stores the
///     same
///     data source, <see cref="StreamingOptions" /> and schema.
/// </summary>
public sealed class PostgresTaskStore : TaskStore, IContextSummaryStore, IBatchTaskCommit, ITaskCommitLookup
{
    // state is stored as the TaskState number; these are the SQL lists of the two groups.
    private static readonly string TerminalStates =
        StateList(TaskState.Completed, TaskState.Failed, TaskState.Canceled, TaskState.Rejected);

    private static readonly string InterruptedStates = StateList(TaskState.InputRequired, TaskState.AuthRequired);
    private readonly DbClock _clock;
    private readonly string _contexts;
    private readonly NpgsqlDataSource _db;
    private readonly EventTables _eventTables;
    private readonly string _idempotency;
    private readonly string _insertSql;
    private readonly string _outbox;
    private readonly StreamingOptions _streaming;
    private readonly string _tasks;
    private readonly TimeProvider _time;
    private readonly string _updateSql;
    private readonly PostgresArtifactRows _artifactRows;
    private readonly bool _useArtifactRows;

    /// <summary>
    ///     Creates the store over <paramref name="dataSource" />. Create the tables first (<see cref="PostgresSchema" />
    ///     ).
    /// </summary>
    /// <param name="dataSource">The database.</param>
    /// <param name="streaming">
    ///     The event log retention, as for <see cref="PostgresEventStore" /> (
    ///     <see cref="StreamingOptions.ReplayBufferSize" />).
    /// </param>
    /// <param name="options">The schema; default <c>public</c>.</param>
    /// <param name="time">
    ///     The clock for <see cref="TaskRecord.UpdatedAt" /> and retention. Default
    ///     <see cref="TimeProvider.System" />.
    /// </param>
    public PostgresTaskStore(NpgsqlDataSource dataSource, StreamingOptions streaming,
        PostgresStoreOptions? options = null, TimeProvider? time = null)
    {
        _db = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _streaming = streaming ?? throw new ArgumentNullException(nameof(streaming));
        _time = time ?? TimeProvider.System;
        _clock = new DbClock(_time, options);
        var settings = options ?? new PostgresStoreOptions();
        var s = PostgresSchema.Quote(settings.Schema);
        _tasks = $"{s}.a2a_tasks";
        _artifactRows = new PostgresArtifactRows($"{s}.a2a_artifact_parts", _tasks, settings.ArtifactRowsCompactAt);
        _useArtifactRows = settings.ArtifactRows;
        _contexts = $"{s}.a2a_contexts";
        _eventTables = EventTables.For(options);
        _outbox = PostgresPushOutbox.OutboxTable(options);
        _idempotency = $"{s}.a2a_idempotency";
        _insertSql =
            $"""
             INSERT INTO {_tasks} (tenant, task_id, owner, context_id, state, status_ticks, version, created_ticks, updated_ticks, task, parts_layout)
             VALUES ($1, $2, $3, $4, $5, $6, 1, $7, $8, $9, $10)
             ON CONFLICT (tenant, task_id) DO NOTHING
             """;
        _updateSql =
            $"""
             UPDATE {_tasks}
                SET context_id = $4, state = $5, status_ticks = COALESCE($6, created_ticks), version = version + 1, updated_ticks = $7, task = $8, parts_layout = $10
              WHERE tenant = $1 AND task_id = $2 AND owner = $3 AND version = $9
             RETURNING created_ticks
             """;
    }

    /// <summary>
    ///     One transaction, like <see cref="CommitAsync" />: the task compare-and-set once (the task is serialized once for
    ///     the whole
    ///     batch), then each event with its sequence and its push deliveries. Sequences are consecutive: the task row lock
    ///     taken by
    ///     the update serializes commits to the task.
    /// </summary>
    /// <inheritdoc />
    public async ValueTask<BatchCommitResult> CommitBatchAsync(TaskBatchCommit commit,
        CancellationToken cancellationToken = default)
    {
        ValidateBatch(commit);
        var key = commit.Record.Key;
        var (json, stored) = ForUpdate(commit.Record, commit.ExpectedVersion, _useArtifactRows);

        await using var conn = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var rows = _useArtifactRows
            ? await _artifactRows.ReadAsync(conn, tx, key, cancellationToken).ConfigureAwait(false)
            : null;
        await using (var update = new NpgsqlCommand(_updateSql, conn, tx))
        {
            AddUpdateParameters(update, stored, json, commit.ExpectedVersion, Layout);
            if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long createdTicks)
                throw new ConcurrencyException(key, commit.ExpectedVersion);
            stored = stored with { CreatedAt = new DateTimeOffset(createdTicks, TimeSpan.Zero) };
        }

        if (_useArtifactRows)
        {
            var appended = new Dictionary<string, List<Part>>(StringComparer.Ordinal);
            var replaced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in commit.Events) Changes(e.Event.Event, appended, replaced);
            await _artifactRows.WriteAsync(conn, tx, key, commit.Record.Task, rows, appended, replaced,
                cancellationToken).ConfigureAwait(false);
        }

        var now = _clock.NowTicks;
        var capacity = Math.Max(1, _streaming.ReplayBufferSize);
        long first = 0;
        await using var deliveries = new NpgsqlBatch(conn, tx);
        foreach (var e in commit.Events)
        {
            var eventBytes = EventEncoder.EnsureEncoded(e.Event).Json;
            var seq = await AppendEventAsync(conn, tx, _eventTables, key, Encoding.UTF8.GetString(eventBytes.Span),
                    capacity, now, cancellationToken, first == 0 ? commit.MutationId : null)
                .ConfigureAwait(false);
            if (first == 0) first = seq;
            if (e.Deliveries.Count == 0) continue;
            var payload = eventBytes.ToArray(); // the webhook body, stored with the event
            foreach (var d in e.Deliveries)
                deliveries.BatchCommands.Add(PostgresPushOutbox.InsertCommand(_outbox, d, seq, payload, now));
        }

        if (deliveries.BatchCommands.Count > 0)
            await deliveries.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return new BatchCommitResult(stored, first);
    }

    /// <summary>
    ///     Reads the sequence of the retained event whose commit had <paramref name="mutationId" /> (column
    ///     <c>mutation_id</c> of <c>a2a_events</c>, migration 0006). Only the task's retained events are searched.
    /// </summary>
    /// <inheritdoc />
    public async ValueTask<long?> FindCommitAsync(TaskKey task, string mutationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutationId);
        await using var cmd = _db.CreateCommand(
            $"SELECT seq FROM {_eventTables.Events} WHERE tenant = $1 AND owner = $2 AND task_id = $3 AND mutation_id = $4 LIMIT 1");
        AddKey(cmd.Parameters, task);
        cmd.Parameters.Add(TextParam(mutationId));
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long seq ? seq : null;
    }

    /// <inheritdoc />
    public async ValueTask<bool> SetContextSummaryAsync(string tenant, string owner, string contextId, string? summary,
        CancellationToken cancellationToken = default)
    {
        await using var cmd =
            _db.CreateCommand(
                $"UPDATE {_contexts} SET summary = $4 WHERE tenant = $1 AND owner = $2 AND context_id = $3");
        cmd.Parameters.Add(TextParam(tenant));
        cmd.Parameters.Add(TextParam(owner));
        cmd.Parameters.Add(TextParam(contextId));
        cmd.Parameters.Add(NullableText(summary));
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public override async ValueTask<TaskRecord?> GetAsync(TaskKey key, CancellationToken cancellationToken = default)
    {
        await using var cmd = _db.CreateCommand(
            $"SELECT owner, version, created_ticks, updated_ticks, task, parts_layout FROM {_tasks} WHERE tenant = $1 AND task_id = $2 AND owner = $3");
        cmd.Parameters.Add(TextParam(key.Tenant));
        cmd.Parameters.Add(TextParam(key.TaskId));
        cmd.Parameters.Add(TextParam(key.Owner));
        return await ReadOneAsync(cmd, key.Tenant, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask<TaskRecord?> GetForMaintenanceAsync(string tenant, string taskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(taskId);
        await using var cmd = _db.CreateCommand(
            $"SELECT owner, version, created_ticks, updated_ticks, task, parts_layout FROM {_tasks} WHERE tenant = $1 AND task_id = $2");
        cmd.Parameters.Add(TextParam(tenant));
        cmd.Parameters.Add(TextParam(taskId));
        return await ReadOneAsync(cmd, tenant, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask<TaskRecord> CreateAsync(TaskRecord record,
        CancellationToken cancellationToken = default)
    {
        var (json, stored) = ForCreate(record, false);
        await using var cmd = _db.CreateCommand(_insertSql);
        AddInsertParameters(cmd, stored, json, PostgresArtifactRows.InlineLayout);
        if (await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            throw new DuplicateTaskException(record.Key);
        return stored;
    }

    /// <inheritdoc />
    public override async ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        // Written with its parts inline (layout 0): the rows, if any, are then ignored, and the next commit with
        // ArtifactRows on rewrites them.
        var (json, stored) = ForUpdate(record, expectedVersion, false);
        await using var cmd = _db.CreateCommand(_updateSql);
        AddUpdateParameters(cmd, stored, json, expectedVersion, PostgresArtifactRows.InlineLayout);
        var created = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (created is not long createdTicks) throw new ConcurrencyException(record.Key, expectedVersion);
        return stored with { CreatedAt = new DateTimeOffset(createdTicks, TimeSpan.Zero) };
    }

    /// <summary>
    ///     One transaction: the task insert or compare-and-set update (whose row lock serializes commits to the task, so log
    ///     order = version order across instances), then the event append with its sequence, then the push deliveries with
    ///     that sequence. Any failure rolls back all of it. The final COMMIT is not cancelled: once it is sent, a cancelled
    ///     token must not leave the caller unsure whether the event and its deliveries were stored.
    /// </summary>
    /// <inheritdoc />
    public override async ValueTask<CommitResult> CommitAsync(TaskCommit commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ValidateDeliveries(commit);
        var key = commit.Record.Key;
        var (json, stored) = commit.IsCreate
            ? ForCreate(commit.Record, _useArtifactRows)
            : ForUpdate(commit.Record, commit.ExpectedVersion, _useArtifactRows);
        // The bytes the engine encoded, as they are: the log keeps them and every delivery sends them (encoded here only for an
        // event made without them).
        var eventBytes = EventEncoder.EnsureEncoded(commit.Event).Json;
        var eventJson = Encoding.UTF8.GetString(eventBytes.Span);

        await using var conn = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var rows = _useArtifactRows && !commit.IsCreate
            ? await _artifactRows.ReadAsync(conn, tx, key, cancellationToken).ConfigureAwait(false)
            : null;
        if (commit.IsCreate)
        {
            await using var insert = new NpgsqlCommand(_insertSql, conn, tx);
            AddInsertParameters(insert, stored, json, Layout);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                throw new DuplicateTaskException(key);
        }
        else
        {
            await using var update = new NpgsqlCommand(_updateSql, conn, tx);
            AddUpdateParameters(update, stored, json, commit.ExpectedVersion, Layout);
            if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long createdTicks)
                throw new ConcurrencyException(key, commit.ExpectedVersion);
            stored = stored with { CreatedAt = new DateTimeOffset(createdTicks, TimeSpan.Zero) };
        }

        if (_useArtifactRows)
        {
            var appended = new Dictionary<string, List<Part>>(StringComparer.Ordinal);
            var replaced = new HashSet<string>(StringComparer.Ordinal);
            Changes(commit.Event.Event, appended, replaced);
            await _artifactRows.WriteAsync(conn, tx, key, commit.Record.Task, rows, appended, replaced,
                cancellationToken).ConfigureAwait(false);
        }

        var now = _clock.NowTicks; // log and outbox deadlines; null = the database clock
        var seq = await AppendEventAsync(conn, tx, _eventTables, key, eventJson,
            Math.Max(1, _streaming.ReplayBufferSize), now, cancellationToken, commit.MutationId).ConfigureAwait(false);
        if (commit.Deliveries.Count > 0)
        {
            var payload = eventBytes.ToArray(); // the webhook body, stored with the event
            await using var batch = new NpgsqlBatch(conn, tx);
            foreach (var d in commit.Deliveries)
                batch.BatchCommands.Add(PostgresPushOutbox.InsertCommand(_outbox, d, seq, payload, now));
            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (commit.Idempotency is { } idem)
            await WriteIdempotencyOutcomeAsync(conn, tx, idem, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return new CommitResult(stored, seq);
    }

    /// <summary>
    ///     Writes the idempotency outcome in the commit's transaction, like
    ///     <see cref="PostgresIdempotencyStore.CompleteAsync" />: only
    ///     for the claim that still owns a running entry. No matching row is fine (the idempotency store may be elsewhere, or
    ///     the
    ///     claim was taken over): the commit stays.
    /// </summary>
    private async ValueTask WriteIdempotencyOutcomeAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        IdempotencyCommit idem, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {_idempotency} SET outcome = $4, expires_ticks = {DbClock.Now(5)} + $6 WHERE tenant = $1 AND principal = $2 AND key = $3 AND claim_id = $7 AND outcome IS NULL",
            conn, tx);
        cmd.Parameters.Add(TextParam(idem.Scope.Tenant));
        cmd.Parameters.Add(TextParam(idem.Scope.Principal));
        cmd.Parameters.Add(TextParam(idem.Scope.Key));
        cmd.Parameters.Add(TextParam(OutcomeJson.Write(idem.Outcome)));
        cmd.Parameters.Add(_clock.NowParam());
        cmd.Parameters.Add(Span(idem.Retention));
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = idem.ClaimId });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask<TaskPage> ListAsync(TaskQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var cursor = PageCursor.Decode(query.PageToken);

        var countArgs = new List<NpgsqlParameter>();
        var filter = Filter(query, countArgs);
        var pageArgs = new List<NpgsqlParameter>();
        var pageFilter = Filter(query, pageArgs);
        if (cursor is { } c)
        {
            pageArgs.Add(Long(c.Ticks));
            var t = pageArgs.Count;
            pageArgs.Add(TextParam(c.Id));
            pageFilter += $" AND (status_ticks < ${t} OR (status_ticks = ${t} AND task_id > ${t + 1}))";
        }

        pageArgs.Add(new NpgsqlParameter<int> { TypedValue = checked(Math.Max(0, query.PageSize) + 1) });

        await using var batch = _db.CreateBatch();
        var count = new NpgsqlBatchCommand($"SELECT count(*) FROM {_tasks} WHERE {filter}");
        foreach (var a in countArgs) count.Parameters.Add(a);
        var page = new NpgsqlBatchCommand(
            $"SELECT owner, version, created_ticks, updated_ticks, task, parts_layout FROM {_tasks} WHERE {pageFilter} ORDER BY status_ticks DESC, task_id LIMIT ${pageArgs.Count}");
        foreach (var a in pageArgs) page.Parameters.Add(a);
        batch.BatchCommands.Add(count);
        batch.BatchCommands.Add(page);

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var total = checked((int)reader.GetInt64(0));
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<TaskRecord>();
        var inRows = new Dictionary<string, AgentTask>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = ReadRecord(reader, query.Tenant);
            records.Add(record);
            if (reader.GetInt16(5) == PostgresArtifactRows.RowsLayout) inRows[record.Task.Id] = record.Task;
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await _artifactRows.FillAsync(null, null, _db, query.Tenant, inRows, cancellationToken).ConfigureAwait(false);

        var hasMore = records.Count > query.PageSize;
        if (hasMore) records.RemoveAt(records.Count - 1);
        var next = hasMore && records.Count > 0 ? PageCursor.Encode(records[^1]) : string.Empty;
        return new TaskPage(records, next, total);
    }

    /// <inheritdoc />
    public override async ValueTask<ContextRecord?> GetContextAsync(string tenant, string contextId,
        CancellationToken cancellationToken = default)
    {
        await using var cmd =
            _db.CreateCommand(
                $"SELECT owner, created_ticks, summary FROM {_contexts} WHERE tenant = $1 AND context_id = $2");
        cmd.Parameters.Add(TextParam(tenant));
        cmd.Parameters.Add(TextParam(contextId));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ContextRecord(tenant, reader.GetString(0), contextId,
                new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero))
            {
                Summary = reader.IsDBNull(2) ? null : reader.GetString(2)
            }
            : null;
    }

    /// <inheritdoc />
    public override async ValueTask<ContextRecord> GetOrCreateContextAsync(ContextRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using (var cmd = _db.CreateCommand(
                         $"INSERT INTO {_contexts} (tenant, context_id, owner, created_ticks, summary) VALUES ($1, $2, $3, $4, $5) ON CONFLICT (tenant, context_id) DO NOTHING"))
        {
            cmd.Parameters.Add(TextParam(record.Tenant));
            cmd.Parameters.Add(TextParam(record.ContextId));
            cmd.Parameters.Add(TextParam(record.Owner));
            cmd.Parameters.Add(Long(record.CreatedAt.UtcTicks));
            cmd.Parameters.Add(NullableText(record.Summary));
            if (await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1) return record;
        }

        // Lost the race (or the context existed): a new statement sees the committed winner.
        return await GetContextAsync(record.Tenant, record.ContextId, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException(
                   $"Context '{record.ContextId}' vanished while it was being created.");
    }

    /// <summary>
    ///     Cuts the history in the database: only the newest tasks of the context are read, and only the message history of
    ///     each
    ///     (never the artifacts). The size limit cuts there too, so what is over budget never reaches the client. The JSON is
    ///     read as
    ///     <c>json</c>, not <c>jsonb</c>. Uses <c>a2a_tasks_context_ix</c>.
    /// </summary>
    /// <remarks>
    ///     PostgreSQL cannot look into a <c>json</c> value that holds the escape <c>\u0000</c> (a NUL character in some text;
    ///     error
    ///     22P05). For such a conversation the store reads it with the fallback of
    ///     <see cref="TaskStore.ReadContextMessagesAsync" />
    ///     instead: it loads whole tasks and cuts in this process. The result is the same, only that read costs more.
    /// </remarks>
    public override async ValueTask<IReadOnlyList<Message>> ReadContextMessagesAsync(ContextHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ValidateHistoryQuery(query);
        await using var cmd = _db.CreateCommand(
            $"""
             WITH recent AS (
                 SELECT task_id, status_ticks, (task::json -> 'history') AS h
                   FROM {_tasks}
                  WHERE tenant = $1 AND owner = $2 AND context_id = $3 AND task_id <> $4 AND state <> {(int)TaskState.Rejected}
                  ORDER BY status_ticks DESC, task_id
                  LIMIT $5
             ), msgs AS (
                 SELECT r.status_ticks, r.task_id, e.ord, e.value::text AS msg, octet_length(e.value::text) AS len
                   FROM recent r
                  CROSS JOIN LATERAL json_array_elements(CASE json_typeof(r.h) WHEN 'array' THEN r.h END) WITH ORDINALITY AS e(value, ord)
             ), sized AS (
                 SELECT *, sum(len) OVER (ORDER BY status_ticks DESC, task_id, ord DESC ROWS UNBOUNDED PRECEDING) AS total FROM msgs
             )
             SELECT msg FROM sized WHERE total <= $6 ORDER BY status_ticks, task_id DESC, ord
             """);
        cmd.Parameters.Add(TextParam(query.Tenant));
        cmd.Parameters.Add(TextParam(query.Owner));
        cmd.Parameters.Add(TextParam(query.ContextId));
        cmd.Parameters.Add(TextParam(query.ExcludeTaskId ?? string.Empty));
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = query.MaxTasks });
        cmd.Parameters.Add(
            new NpgsqlParameter<long> { TypedValue = query.MaxBytes, NpgsqlDbType = NpgsqlDbType.Bigint });
        try
        {
            var messages = new List<Message>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                messages.Add(A2AJson.Deserialize<Message>(reader.GetString(0)));
            return messages;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UntranslatableCharacter)
        {
            // "unsupported Unicode escape sequence": a message holds \u0000, which the JSON functions of PostgreSQL refuse to read.
            return await base.ReadContextMessagesAsync(query, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One indexed query (<c>a2a_contexts_owner_ix</c>). Lists every context of the owner, also one with no tasks.</summary>
    /// <inheritdoc />
    public override async ValueTask<ContextPage> ListContextsAsync(ContextQuery query,
        CancellationToken cancellationToken = default)
    {
        ValidateContextQuery(query);
        var cursor = PageCursor.Decode(query.PageToken);
        var args = new List<NpgsqlParameter> { TextParam(query.Tenant), TextParam(query.Owner) };
        var where = "tenant = $1 AND owner = $2";
        if (cursor is { } c)
        {
            args.Add(Long(c.Ticks));
            args.Add(TextParam(c.Id));
            where += " AND (created_ticks < $3 OR (created_ticks = $3 AND context_id > $4))";
        }

        args.Add(new NpgsqlParameter<int> { TypedValue = checked(query.PageSize + 1) });
        await using var cmd = _db.CreateCommand(
            $"SELECT context_id, created_ticks, summary FROM {_contexts} WHERE {where} ORDER BY created_ticks DESC, context_id LIMIT ${args.Count}");
        foreach (var a in args) cmd.Parameters.Add(a);
        var records = new List<ContextRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            records.Add(new ContextRecord(query.Tenant, query.Owner, reader.GetString(0),
                new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero))
            {
                Summary = reader.IsDBNull(2) ? null : reader.GetString(2)
            });
        var hasMore = records.Count > query.PageSize;
        if (hasMore) records.RemoveAt(records.Count - 1);
        return new ContextPage(records, hasMore ? PageCursor.Encode(records[^1]) : string.Empty);
    }

    /// <inheritdoc />
    public override async ValueTask<bool> DeleteAsync(TaskKey key, CancellationToken cancellationToken = default)
    {
        await using var cmd =
            _db.CreateCommand($"DELETE FROM {_tasks} WHERE tenant = $1 AND task_id = $2 AND owner = $3");
        cmd.Parameters.Add(TextParam(key.Tenant));
        cmd.Parameters.Add(TextParam(key.TaskId));
        cmd.Parameters.Add(TextParam(key.Owner));
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<TaskKey>> ListExpiredAsync(DateTimeOffset terminalBefore, int max,
        CancellationToken cancellationToken = default)
    {
        return ListByStateAsync(TerminalStates, terminalBefore, max, cancellationToken);
    }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<TaskKey>> ListInterruptedBeforeAsync(DateTimeOffset before, int max,
        CancellationToken cancellationToken = default)
    {
        return ListByStateAsync(InterruptedStates, before, max, cancellationToken);
    }

    private static string StateList(params TaskState[] states)
    {
        return string.Join(", ",
            states.Select(s => ((int)s).ToString(CultureInfo.InvariantCulture)));
    }

    private async ValueTask<IReadOnlyList<TaskKey>> ListByStateAsync(string states, DateTimeOffset before, int max,
        CancellationToken cancellationToken)
    {
        await using var cmd = _db.CreateCommand(
            $"""
             SELECT tenant, owner, task_id FROM {_tasks}
              WHERE state IN ({states}) AND status_ticks < $1
              ORDER BY status_ticks, tenant, task_id
              LIMIT $2
             """);
        cmd.Parameters.Add(Long(before.UtcTicks));
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = Math.Max(0, max) });
        var keys = new List<TaskKey>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            keys.Add(new TaskKey(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return keys;
    }

    /// <inheritdoc />
    public override async ValueTask<bool> DeleteContextAsync(string tenant, string contextId,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = _db.CreateCommand(
            $"""
             DELETE FROM {_contexts} c WHERE c.tenant = $1 AND c.context_id = $2
                AND NOT EXISTS (SELECT 1 FROM {_tasks} t WHERE t.tenant = c.tenant AND t.context_id = c.context_id)
             """);
        cmd.Parameters.Add(TextParam(tenant));
        cmd.Parameters.Add(TextParam(contextId));
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<ContextRecord>> ListOrphanContextsAsync(DateTimeOffset createdBefore,
        int max, CancellationToken cancellationToken = default)
    {
        await using var cmd = _db.CreateCommand(
            $"""
             SELECT c.tenant, c.owner, c.context_id, c.created_ticks FROM {_contexts} c
              WHERE c.created_ticks < $1
                AND NOT EXISTS (SELECT 1 FROM {_tasks} t WHERE t.tenant = c.tenant AND t.context_id = c.context_id)
              ORDER BY c.created_ticks, c.tenant, c.context_id
              LIMIT $2
             """);
        cmd.Parameters.Add(Long(createdBefore.UtcTicks));
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = Math.Max(0, max) });
        var list = new List<ContextRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(new ContextRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero)));
        return list;
    }

    private static (string Json, TaskRecord Stored) ForCreate(TaskRecord record, bool withoutParts)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Version != 0) throw new ArgumentException("A new record must have Version 0.", nameof(record));
        return Snapshot(record with { Version = 1 }, withoutParts);
    }

    private (string Json, TaskRecord Stored) ForUpdate(TaskRecord record, long expectedVersion, bool withoutParts)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Snapshot(record with { Version = expectedVersion + 1, UpdatedAt = _time.GetUtcNow() }, withoutParts);
    }

    /// <summary>
    ///     Converts up to <paramref name="batchSize" /> tasks whose artifact parts are in rows
    ///     (<see cref="PostgresStoreOptions.ArtifactRows" />) back to parts in the task, and deletes their rows. Returns how
    ///     many it converted: call it until it returns 0. The task's version does not change, because its content does not.
    ///     Turn <see cref="PostgresStoreOptions.ArtifactRows" /> off on every instance first, then run this before
    ///     downgrading to a version without the option.
    /// </summary>
    public async ValueTask<int> InlineArtifactPartsAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        await using var conn = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var keys = new List<(string Tenant, string TaskId)>();
        await using (var pick = new NpgsqlCommand(
                         $"SELECT tenant, task_id FROM {_tasks} WHERE parts_layout = {PostgresArtifactRows.RowsLayout} LIMIT $1", conn))
        {
            pick.Parameters.Add(new NpgsqlParameter<int> { TypedValue = batchSize });
            await using var reader = await pick.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                keys.Add((reader.GetString(0), reader.GetString(1)));
        }

        var converted = 0;
        foreach (var (tenant, taskId) in keys)
        {
            await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            AgentTask task;
            await using (var read = new NpgsqlCommand(
                             $"SELECT task FROM {_tasks} WHERE tenant = $1 AND task_id = $2 AND parts_layout = {PostgresArtifactRows.RowsLayout} FOR UPDATE",
                             conn, tx))
            {
                read.Parameters.Add(TextParam(tenant));
                read.Parameters.Add(TextParam(taskId));
                if (await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string json) continue;
                task = A2AJson.Deserialize<AgentTask>(json)!;
            }

            await _artifactRows.FillAsync(conn, tx, _db, tenant,
                new Dictionary<string, AgentTask>(StringComparer.Ordinal) { [taskId] = task }, cancellationToken)
                .ConfigureAwait(false);
            await using var batch = new NpgsqlBatch(conn, tx);
            var update = new NpgsqlBatchCommand(
                $"UPDATE {_tasks} SET task = $3, parts_layout = {PostgresArtifactRows.InlineLayout} WHERE tenant = $1 AND task_id = $2");
            update.Parameters.Add(TextParam(tenant));
            update.Parameters.Add(TextParam(taskId));
            update.Parameters.Add(TextParam(A2AJson.Serialize(task)));
            var delete = new NpgsqlBatchCommand(
                $"DELETE FROM {_artifactRows.Table} WHERE tenant = $1 AND task_id = $2");
            delete.Parameters.Add(TextParam(tenant));
            delete.Parameters.Add(TextParam(taskId));
            batch.BatchCommands.Add(update);
            batch.BatchCommands.Add(delete);
            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            converted++;
        }

        return converted;
    }

    /// <summary>The <c>parts_layout</c> a commit writes: rows with <see cref="PostgresStoreOptions.ArtifactRows" />, else inline.</summary>
    private short Layout => _useArtifactRows ? PostgresArtifactRows.RowsLayout : PostgresArtifactRows.InlineLayout;

    /// <summary>
    ///     The artifact changes of one event: the parts an append adds (in order, per artifact), or an artifact that is
    ///     replaced. Other events change no parts.
    /// </summary>
    private static void Changes(StreamResponse evt, Dictionary<string, List<Part>> appended, HashSet<string> replaced)
    {
        if (evt is not ArtifactUpdateEvent { ArtifactUpdate: { } update }) return;
        var id = update.Artifact.ArtifactId;
        if (!update.Append)
        {
            replaced.Add(id);
            return;
        }

        if (!appended.TryGetValue(id, out var list)) appended[id] = list = [];
        list.AddRange(update.Artifact.Parts);
    }

    private async ValueTask<TaskRecord?> ReadOneAsync(NpgsqlCommand cmd, string tenant,
        CancellationToken cancellationToken)
    {
        TaskRecord record;
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            record = ReadRecord(reader, tenant);
            if (reader.GetInt16(5) != PostgresArtifactRows.RowsLayout) return record;
        }

        await _artifactRows.FillAsync(null, null, _db, tenant,
            new Dictionary<string, AgentTask>(StringComparer.Ordinal) { [record.Task.Id] = record.Task },
            cancellationToken).ConfigureAwait(false);
        return record;
    }

    private static void AddInsertParameters(NpgsqlCommand cmd, TaskRecord stored, string json, short layout)
    {
        cmd.Parameters.Add(TextParam(stored.Tenant));
        cmd.Parameters.Add(TextParam(stored.Task.Id));
        cmd.Parameters.Add(TextParam(stored.Owner));
        cmd.Parameters.Add(TextParam(stored.Task.ContextId ?? string.Empty));
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = (int)stored.Task.Status.State });
        cmd.Parameters.Add(Long(StatusTicks(stored)));
        cmd.Parameters.Add(Long(stored.CreatedAt.UtcTicks));
        cmd.Parameters.Add(Long(stored.UpdatedAt.UtcTicks));
        cmd.Parameters.Add(TextParam(json));
        cmd.Parameters.Add(new NpgsqlParameter<short> { TypedValue = layout });
    }

    private static void AddUpdateParameters(NpgsqlCommand cmd, TaskRecord stored, string json, long expectedVersion,
        short layout)
    {
        cmd.Parameters.Add(TextParam(stored.Tenant));
        cmd.Parameters.Add(TextParam(stored.Task.Id));
        cmd.Parameters.Add(TextParam(stored.Owner));
        cmd.Parameters.Add(TextParam(stored.Task.ContextId ?? string.Empty));
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = (int)stored.Task.Status.State });
        // Without a status timestamp the sort key falls back to the creation time, which only the database row has.
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)stored.Task.Status.Timestamp?.UtcTicks ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint
        });
        cmd.Parameters.Add(Long(stored.UpdatedAt.UtcTicks));
        cmd.Parameters.Add(TextParam(json));
        cmd.Parameters.Add(Long(expectedVersion));
        cmd.Parameters.Add(new NpgsqlParameter<short> { TypedValue = layout });
    }

    /// <summary>
    ///     Serializes the task once and returns both the JSON and a record rebuilt from it, so what is returned matches
    ///     what is stored.
    /// </summary>
    private static (string Json, TaskRecord Stored) Snapshot(TaskRecord record, bool withoutParts)
    {
        if (!withoutParts)
        {
            var full = A2AJson.Serialize(record.Task);
            return (full, record with { Task = A2AJson.Deserialize<AgentTask>(full) });
        }

        // ArtifactRows: the task row holds no parts. The returned copy gets them back from the record (the parts are
        // shared; nobody changes them).
        var json = A2AJson.Serialize(PostgresArtifactRows.WithoutParts(record.Task));
        var task = A2AJson.Deserialize<AgentTask>(json)!;
        PostgresArtifactRows.CopyParts(record.Task, task);
        return (json, record with { Task = task });
    }

    private static TaskRecord ReadRecord(NpgsqlDataReader reader, string tenant)
    {
        return new TaskRecord(
            tenant,
            reader.GetString(0),
            A2AJson.Deserialize<AgentTask>(reader.GetString(4)),
            reader.GetInt64(1),
            new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
            new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero));
    }

    private static long StatusTicks(TaskRecord r)
    {
        return (r.Task.Status.Timestamp ?? r.CreatedAt).UtcTicks;
    }

    /// <summary>The WHERE clause for a query; adds its parameters (positional, $1..) to <paramref name="args" />.</summary>
    private static string Filter(TaskQuery query, List<NpgsqlParameter> args)
    {
        args.Add(TextParam(query.Tenant));
        args.Add(TextParam(query.Owner));
        var filter = "tenant = $1 AND owner = $2";
        if (query.ContextId is not null)
        {
            args.Add(TextParam(query.ContextId));
            filter += $" AND context_id = ${args.Count}";
        }

        if (query.Status is { } status)
        {
            args.Add(new NpgsqlParameter<int> { TypedValue = (int)status });
            filter += $" AND state = ${args.Count}";
        }

        if (query.StatusTimestampAfter is { } after)
        {
            args.Add(Long(after.UtcTicks));
            filter += $" AND status_ticks >= ${args.Count}";
        }

        return filter;
    }
}