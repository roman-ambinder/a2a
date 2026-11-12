using A2A.Json;
using A2A.Server.Storage;
using Npgsql;
using static A2A.Server.Postgres.Sql;

namespace A2A.Server.Postgres;

/// <summary>
///     The parts of a task's artifacts in rows of <c>a2a_artifact_parts</c> (<see cref="PostgresStoreOptions.ArtifactRows" />,
///     migration 0007). A task with <c>parts_layout = 1</c> is stored with <c>"parts": []</c> in every artifact, and each
///     artifact's parts are its rows in <c>ordinal</c> order. An append inserts one row with the new parts. Any other change
///     to an artifact rewrites its rows as one row.
/// </summary>
internal sealed class PostgresArtifactRows(string parts, string tasks, int compactAt)
{
    /// <summary>The quoted name of the table.</summary>
    public string Table => parts;

    /// <summary>The <c>parts_layout</c> of a task whose parts are in rows.</summary>
    public const short RowsLayout = 1;

    /// <summary>The <c>parts_layout</c> of a task whose parts are in its JSON, as before migration 0007.</summary>
    public const short InlineLayout = 0;

    /// <summary>A copy of <paramref name="task" /> with every artifact's parts left out (other fields shared).</summary>
    public static AgentTask WithoutParts(AgentTask task)
    {
        if (task.Artifacts is not { Count: > 0 }) return task;
        var copy = task.CopyForUpdate();
        for (var i = 0; i < copy.Artifacts!.Count; i++) copy.Artifacts[i] = WithoutParts(copy.Artifacts[i]);
        return copy;
    }

    /// <summary>
    ///     A shallow copy of <paramref name="artifact" /> with no parts. Every other property is kept. It does not read
    ///     <see cref="Artifact.Parts" />, so it does not build the list (ADR-004).
    /// </summary>
    internal static Artifact WithoutParts(Artifact artifact)
    {
        return new Artifact
        {
            ArtifactId = artifact.ArtifactId,
            Name = artifact.Name,
            Description = artifact.Description,
            Parts = [],
            Metadata = artifact.Metadata,
            Extensions = artifact.Extensions,
            StartGeneration = artifact.StartGeneration,
            EndGeneration = artifact.EndGeneration,
            Role = artifact.Role
        };
    }

    /// <summary>
    ///     Puts the parts of <paramref name="source" /> back into <paramref name="stored" /> (parsed without them). Each
    ///     artifact of <paramref name="stored" /> is replaced by one with its parsed properties and the parts of the source
    ///     artifact, shared with <see cref="ModelCopies.CopyWithParts" /> rather than read (ADR-004): O(1) per artifact when
    ///     the source's list was never built, and a single copy when it was, as before.
    /// </summary>
    public static void CopyParts(AgentTask source, AgentTask stored)
    {
        if (source.Artifacts is not { } from || stored.Artifacts is not { } to) return;
        for (var i = 0; i < to.Count && i < from.Count; i++)
        {
            var parsed = to[i];
            var copy = from[i].CopyWithParts([]);
            copy.ArtifactId = parsed.ArtifactId;
            copy.Name = parsed.Name;
            copy.Description = parsed.Description;
            copy.Metadata = parsed.Metadata;
            copy.Extensions = parsed.Extensions;
            copy.StartGeneration = parsed.StartGeneration;
            copy.EndGeneration = parsed.EndGeneration;
            copy.Role = parsed.Role;
            to[i] = copy;
        }
    }

    /// <summary>What a task's rows hold, per artifact: its parts, its rows and its highest ordinal.</summary>
    public sealed record Stored(short Layout, Dictionary<string, (int Parts, int Rows, int MaxOrdinal)> Artifacts);

    /// <summary>
    ///     Reads the task's layout and its rows per artifact, in the commit's transaction, before its compare-and-set. One
    ///     statement, so the layout and the rows come from the same snapshot. Every write that changes the layout or the
    ///     rows also changes the version (the commits, <see cref="PostgresTaskStore.UpdateAsync" /> and
    ///     <see cref="PostgresTaskStore.InlineArtifactPartsAsync" />). So when a write commits after the read the commit is
    ///     based on, the compare-and-set fails and this result is unused; when it succeeds, this is the state it replaces.
    /// </summary>
    public async ValueTask<Stored> ReadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TaskKey key,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
             SELECT t.parts_layout, p.artifact_id, p.part_count, p.row_count, p.max_ordinal
               FROM {tasks} t
               LEFT JOIN LATERAL (
                   SELECT artifact_id, sum(part_count)::int AS part_count, count(*)::int AS row_count, max(ordinal) AS max_ordinal
                     FROM {parts}
                    WHERE tenant = t.tenant AND task_id = t.task_id AND t.parts_layout = {RowsLayout}
                    GROUP BY artifact_id
               ) p ON true
              WHERE t.tenant = $1 AND t.task_id = $2
             """, conn, tx);
        cmd.Parameters.Add(TextParam(key.Tenant));
        cmd.Parameters.Add(TextParam(key.TaskId));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var found = InlineLayout;
        var artifacts = new Dictionary<string, (int, int, int)>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            found = reader.GetInt16(0);
            if (!reader.IsDBNull(1))
                artifacts[reader.GetString(1)] = (reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
        }

        return new Stored(found, artifacts);
    }

    /// <summary>
    ///     Brings the rows of <paramref name="task" /> in line with it, after its compare-and-set, in the same transaction.
    ///     <paramref name="appended" /> holds the parts this commit appended per artifact, in order; <paramref name="replaced" />
    ///     the artifacts it replaced. An artifact that this commit did not touch, and whose rows hold as many parts as it has,
    ///     is left alone: only artifact events change parts. It counts and reads the parts with
    ///     <see cref="ModelCopies.CountParts" /> and <see cref="ModelCopies.CopyLastParts" />, never
    ///     <see cref="Artifact.Parts" />, so an append costs O(the parts it adds), not O(the artifact) (ADR-004).
    /// </summary>
    public async ValueTask WriteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TaskKey key, AgentTask task,
        Stored? stored, IReadOnlyDictionary<string, List<Part>> appended, IReadOnlySet<string> replaced,
        CancellationToken cancellationToken)
    {
        await using var batch = new NpgsqlBatch(conn, tx);
        if (stored is not { Layout: RowsLayout })
        {
            // A create, or a task stored with its parts inline (or written by an instance with the option off): start over.
            batch.BatchCommands.Add(Delete(key, null));
            foreach (var artifact in task.Artifacts ?? [])
                if (artifact.CountParts() is var all and > 0)
                    batch.BatchCommands.Add(Insert(key, artifact.ArtifactId, 0, artifact.CopyLastParts(all)));
        }
        else
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var artifact in task.Artifacts ?? [])
            {
                var id = artifact.ArtifactId;
                present.Add(id);
                var count = artifact.CountParts();
                var have = stored.Artifacts.TryGetValue(id, out var s) ? s : (Parts: 0, Rows: 0, MaxOrdinal: -1);
                if (!replaced.Contains(id) && appended.TryGetValue(id, out var added) && added.Count > 0
                    && have.Parts + added.Count == count && have.Rows < compactAt && EndsWith(artifact, added))
                {
                    batch.BatchCommands.Add(Insert(key, id, have.MaxOrdinal + 1, added));
                    continue;
                }

                if (!replaced.Contains(id) && !appended.ContainsKey(id) && have.Parts == count) continue; // untouched

                batch.BatchCommands.Add(Delete(key, id));
                if (count > 0) batch.BatchCommands.Add(Insert(key, id, 0, artifact.CopyLastParts(count)));
            }

            foreach (var id in stored.Artifacts.Keys)
                if (!present.Contains(id))
                    batch.BatchCommands.Add(Delete(key, id)); // an artifact the task no longer has
        }

        if (batch.BatchCommands.Count > 0) await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     A select-list expression for a task row aliased <paramref name="alias" />: null for a task stored inline, else
    ///     the <c>parts</c> of its rows as a <c>text[]</c>, in <c>artifact_id, ordinal</c> order. It is part of the
    ///     statement that reads the task row, so it reads the rows from the same snapshot: the parts always belong to the
    ///     version read. Pass the value to <see cref="Fill" />.
    /// </summary>
    public string PartsColumn(string alias) =>
        $"CASE WHEN {alias}.parts_layout = {RowsLayout} THEN ARRAY(SELECT p.parts FROM {parts} p WHERE p.tenant = {alias}.tenant AND p.task_id = {alias}.task_id ORDER BY p.artifact_id, p.ordinal) END";

    /// <summary>
    ///     Puts the parts of <paramref name="rows" /> (the value of <see cref="PartsColumn" />) back into the artifacts of
    ///     <paramref name="task" />. Null, for a task stored inline, leaves it as it is. Each row names its artifact.
    /// </summary>
    public static void Fill(AgentTask task, string[]? rows)
    {
        if (rows is null) return;
        var found = new Dictionary<string, List<Part>>(StringComparer.Ordinal);
        foreach (var json in rows)
        {
            var row = A2AJson.Deserialize<Artifact>(json)!;
            if (!found.TryGetValue(row.ArtifactId, out var list)) found[row.ArtifactId] = list = [];
            list.AddRange(row.Parts);
        }

        foreach (var artifact in task.Artifacts ?? [])
            artifact.Parts = found.TryGetValue(artifact.ArtifactId, out var list) ? list : [];
    }

    /// <summary>
    ///     The parts of the task's rows, read in <paramref name="tx" />, as for <see cref="Fill" />. The caller holds the
    ///     task row lock (<c>FOR UPDATE</c>), which every write of the rows takes first, and this statement starts after it,
    ///     so the rows are those of the locked version.
    /// </summary>
    public async ValueTask<string[]> ReadPartsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string tenant,
        string taskId, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT parts FROM {parts} WHERE tenant = $1 AND task_id = $2 ORDER BY artifact_id, ordinal", conn, tx);
        cmd.Parameters.Add(TextParam(tenant));
        cmd.Parameters.Add(TextParam(taskId));
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) list.Add(reader.GetString(0));
        return list.ToArray();
    }

    private NpgsqlBatchCommand Insert(TaskKey key, string artifactId, int ordinal, List<Part> list)
    {
        var cmd = new NpgsqlBatchCommand(
            $"INSERT INTO {parts} (tenant, task_id, artifact_id, ordinal, part_count, parts) VALUES ($1, $2, $3, $4, $5, $6)");
        cmd.Parameters.Add(TextParam(key.Tenant));
        cmd.Parameters.Add(TextParam(key.TaskId));
        cmd.Parameters.Add(TextParam(artifactId));
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = ordinal });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = list.Count });
        cmd.Parameters.Add(TextParam(RowJson(artifactId, list)));
        return cmd;
    }

    private NpgsqlBatchCommand Delete(TaskKey key, string? artifactId)
    {
        var cmd = new NpgsqlBatchCommand(artifactId is null
            ? $"DELETE FROM {parts} WHERE tenant = $1 AND task_id = $2"
            : $"DELETE FROM {parts} WHERE tenant = $1 AND task_id = $2 AND artifact_id = $3");
        cmd.Parameters.Add(TextParam(key.Tenant));
        cmd.Parameters.Add(TextParam(key.TaskId));
        if (artifactId is not null) cmd.Parameters.Add(TextParam(artifactId));
        return cmd;
    }

    /// <summary>A row's parts, as an artifact with only its id and parts (the type the JSON context knows).</summary>
    private static string RowJson(string artifactId, IEnumerable<Part> list) =>
        A2AJson.Serialize(new Artifact { ArtifactId = artifactId, Parts = list.ToList() });

    /// <summary>True when the last parts of <paramref name="artifact" /> are <paramref name="tail" />, compared as JSON.</summary>
    private static bool EndsWith(Artifact artifact, List<Part> tail)
    {
        if (tail.Count > artifact.CountParts()) return false;
        return string.Equals(RowJson("", artifact.CopyLastParts(tail.Count)), RowJson("", tail),
            StringComparison.Ordinal);
    }
}
