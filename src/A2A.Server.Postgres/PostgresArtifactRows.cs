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

    /// <summary>A shallow copy of <paramref name="artifact" /> with no parts. Every other property is kept.</summary>
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

    /// <summary>Puts the parts of <paramref name="source" /> back into <paramref name="stored" /> (parsed without them).</summary>
    public static void CopyParts(AgentTask source, AgentTask stored)
    {
        if (source.Artifacts is not { } from || stored.Artifacts is not { } to) return;
        for (var i = 0; i < to.Count && i < from.Count; i++) to[i].Parts = new List<Part>(from[i].Parts);
    }

    /// <summary>What a task's rows hold, per artifact: its parts, its rows and its highest ordinal.</summary>
    public sealed record Stored(short Layout, Dictionary<string, (int Parts, int Rows, int MaxOrdinal)> Artifacts);

    /// <summary>
    ///     Reads the task's layout and its rows per artifact, in the commit's transaction, before its compare-and-set. A
    ///     concurrent write in between changes the version, so the compare-and-set that follows fails and this is unused.
    /// </summary>
    public async ValueTask<Stored> ReadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TaskKey key,
        CancellationToken cancellationToken)
    {
        await using var batch = new NpgsqlBatch(conn, tx);
        var layout = new NpgsqlBatchCommand($"SELECT parts_layout FROM {tasks} WHERE tenant = $1 AND task_id = $2");
        layout.Parameters.Add(TextParam(key.Tenant));
        layout.Parameters.Add(TextParam(key.TaskId));
        var rows = new NpgsqlBatchCommand(
            $"SELECT artifact_id, sum(part_count)::int, count(*)::int, max(ordinal) FROM {parts} WHERE tenant = $1 AND task_id = $2 GROUP BY artifact_id");
        rows.Parameters.Add(TextParam(key.Tenant));
        rows.Parameters.Add(TextParam(key.TaskId));
        batch.BatchCommands.Add(layout);
        batch.BatchCommands.Add(rows);
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var found = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? reader.GetInt16(0) : InlineLayout;
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var artifacts = new Dictionary<string, (int, int, int)>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            artifacts[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
        return new Stored(found, artifacts);
    }

    /// <summary>
    ///     Brings the rows of <paramref name="task" /> in line with it, after its compare-and-set, in the same transaction.
    ///     <paramref name="appended" /> holds the parts this commit appended per artifact, in order; <paramref name="replaced" />
    ///     the artifacts it replaced. An artifact that this commit did not touch, and whose rows hold as many parts as it has,
    ///     is left alone: only artifact events change parts.
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
                if (artifact.Parts.Count > 0)
                    batch.BatchCommands.Add(Insert(key, artifact.ArtifactId, 0, artifact.Parts));
        }
        else
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var artifact in task.Artifacts ?? [])
            {
                var id = artifact.ArtifactId;
                present.Add(id);
                var count = artifact.Parts.Count;
                var have = stored.Artifacts.TryGetValue(id, out var s) ? s : (Parts: 0, Rows: 0, MaxOrdinal: -1);
                if (!replaced.Contains(id) && appended.TryGetValue(id, out var added) && added.Count > 0
                    && have.Parts + added.Count == count && have.Rows < compactAt && EndsWith(artifact.Parts, added))
                {
                    batch.BatchCommands.Add(Insert(key, id, have.MaxOrdinal + 1, added));
                    continue;
                }

                if (!replaced.Contains(id) && !appended.ContainsKey(id) && have.Parts == count) continue; // untouched

                batch.BatchCommands.Add(Delete(key, id));
                if (count > 0) batch.BatchCommands.Add(Insert(key, id, 0, artifact.Parts));
            }

            foreach (var id in stored.Artifacts.Keys)
                if (!present.Contains(id))
                    batch.BatchCommands.Add(Delete(key, id)); // an artifact the task no longer has
        }

        if (batch.BatchCommands.Count > 0) await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Puts the parts in rows back into the artifacts of each task in <paramref name="byTask" /> (task id → task), all of
    ///     one tenant, with one query.
    /// </summary>
    public async ValueTask FillAsync(NpgsqlConnection? conn, NpgsqlTransaction? tx, NpgsqlDataSource db, string tenant,
        IReadOnlyDictionary<string, AgentTask> byTask, CancellationToken cancellationToken)
    {
        if (byTask.Count == 0) return;
        var sql =
            $"SELECT task_id, artifact_id, parts FROM {parts} WHERE tenant = $1 AND task_id = ANY($2) ORDER BY task_id, artifact_id, ordinal";
        await using var cmd = conn is null ? db.CreateCommand(sql) : new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.Add(TextParam(tenant));
        cmd.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = byTask.Keys.ToArray() });
        var found = new Dictionary<(string Task, string Artifact), List<Part>>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var k = (reader.GetString(0), reader.GetString(1));
                if (!found.TryGetValue(k, out var list)) found[k] = list = [];
                list.AddRange(A2AJson.Deserialize<Artifact>(reader.GetString(2))!.Parts);
            }
        }

        foreach (var (taskId, task) in byTask)
        foreach (var artifact in task.Artifacts ?? [])
            artifact.Parts = found.TryGetValue((taskId, artifact.ArtifactId), out var list) ? list : [];
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

    /// <summary>True when the last parts of <paramref name="all" /> are <paramref name="tail" />, compared as JSON.</summary>
    private static bool EndsWith(List<Part> all, List<Part> tail)
    {
        if (tail.Count > all.Count) return false;
        return string.Equals(RowJson("", all.GetRange(all.Count - tail.Count, tail.Count)), RowJson("", tail),
            StringComparison.Ordinal);
    }
}
