# Postgres artifact rows: append chunks without rewriting the task

Status: implemented, opt-in (`PostgresStoreOptions.ArtifactRows`). Option C of ADR-003 (`docs/adr/0003-task-writes-per-event.md`), designed after its benchmark.

## Why

`PostgresTaskStore` stores a task as one JSON `text` column, `a2a_tasks.task`, and rewrites the whole column on every commit:

- `Snapshot(record)` serializes the task with `A2AJson.Serialize`, and then deserializes it again for the stored copy that is returned.
- The update is `UPDATE … SET … task = $8`.

So every artifact chunk rewrites every chunk before it. The ADR-003 benchmark (`tests/A2A.Benchmarks.Postgres`) measured this. With one commit per chunk, 1,000 chunks of 1,000 bytes wrote 534.5 MB of WAL for 1 MB of payload (560 times the payload), at 20 ms per chunk. With batch commits, the same artifact wrote 12.8 MB (13.4 times) in 21 commits.

Batching merges only the chunks that queue up behind a commit. So an agent that streams slower than a commit takes gets the one-commit-per-chunk row. A model streaming tokens is the common case. For those streams the write cost stays quadratic.

## Goals

1. An append to an artifact writes its new parts and a small task header, not the whole task.
2. No change to the `TaskStore` contract, the engine, the wire format or other stores. As ADR-001 expected, "the write side needs no contract change".
3. A task read back is identical to the task committed, byte for byte in its JSON.
4. A rolling upgrade never shows a task with missing parts to an older instance.

## Non-goals

- **The in-memory store.** `Artifact.CopyWithParts` copies the part list on every append, and that copy is in the engine. The guides call the in-memory store a development store.
- **History and metadata.** They stay in the task JSON. A long multi-turn history is still rewritten on each commit, but it grows per turn, not per chunk.
- **Fewer event-log or outbox bytes.** Each chunk still goes once into `a2a_events`, and once per webhook into `a2a_push_outbox`.

## Design

### Storage

Migration `0007_artifact_parts.sql` (idempotent, as `Migrations/README.md` requires):

```sql
CREATE TABLE IF NOT EXISTS {s}.a2a_artifact_parts
(
    tenant      text COLLATE "C" NOT NULL,
    task_id     text COLLATE "C" NOT NULL,
    artifact_id text COLLATE "C" NOT NULL,
    ordinal     integer NOT NULL,          -- order of the chunk within the artifact, from 0
    part_count  integer NOT NULL,          -- parts in this row
    parts       text NOT NULL,             -- JSON array of the parts, A2A wire form
    PRIMARY KEY (tenant, task_id, artifact_id, ordinal),
    FOREIGN KEY (tenant, task_id) REFERENCES {s}.a2a_tasks (tenant, task_id) ON DELETE CASCADE
);
ALTER TABLE {s}.a2a_tasks ADD COLUMN IF NOT EXISTS parts_layout smallint NOT NULL DEFAULT 0;
```

- **`parts_layout`** says how the parts of each task are stored:
  - **0 (inline):** as today.
  - **1 (rows):** every artifact in the `task` JSON has `"parts": []`, and its parts are the rows of `a2a_artifact_parts` for that artifact, in `ordinal` order.
- **`ON DELETE CASCADE`** covers every path that deletes a task: `DeleteAsync`, the retention sweeps and `PostgresMaintenance`. No delete query has to change.

### Write path

The incremental path runs only when every one of these holds:

1. `PostgresStoreOptions.ArtifactRows` is on (see "Rolling upgrade").
2. The commit's event is an `ArtifactUpdateEvent` with `Append = true` for artifact *X*.
3. *X* already has rows, and their summed `part_count` plus the event's part count equals the part count of *X* in the committed record.
4. The last parts of *X* in the record serialize to the same JSON as the event's parts.

The incremental path then does the following, in the commit's transaction, after the compare-and-set update of the task row:

- It inserts one row for *X*, with `ordinal` set to one past the highest and `parts` set to the event's parts.
- It writes the task row from a copy of the record whose artifacts have `"parts": []`. That copy keeps the artifact's name, description, metadata (the `a2a.artifact.sealed` flag included) and its generation fields, so those still change with each event.

Any other commit takes the **full path**: it rewrites the parts of every artifact as rows (delete, then insert one row per artifact) and writes the task row without parts. That covers:
- a create;
- an artifact replace (`Append = false`);
- a status event;
- a commit whose record does not match the stored rows, for example after a commit was lost and the record was rebuilt from a fresh read.

Status events are the most frequent full-path commits and change no artifact. So the full path skips an artifact whose summed `part_count` already equals the record's, but only when the event is not an `ArtifactUpdateEvent`. The engine changes artifacts only through artifact events (see "Verification" below).

**Batch commits** (`CommitBatchAsync`) are artifact appends by construction (`IsBatchable`). Every event of the batch appends to *X* in order, so the batch inserts one row per event, or one row for the whole batch. The checks above apply to the batch's total.

**What it costs:** the incremental path writes the event's parts once, plus a task row whose size no longer includes any parts. The task row also carries status, history and metadata, so it is small for a chunk stream and does not grow with the artifact.

**Bounding the rows:** a long stream makes one row per chunk, and the rows are trimmed nowhere. They live as long as the task. To bound the row count, the full path can compact an artifact into one row once its row count passes a threshold (`ArtifactRowsCompactAt`, default 256). The cost of compaction is spread out: one artifact rewrite every 256 appends.

### Read path

- **`GetAsync` and `GetForMaintenanceAsync`:** for a layout-1 row, read the task and the parts in one statement, a `LEFT JOIN` aggregated with `json_agg` ordered by `artifact_id, ordinal`. Then put each artifact's parts back into the deserialized task. (As built: a scalar subquery instead; see "As built".)
- **`ListAsync`:** the same, for the page's tasks, with one extra query keyed by the page's task ids.
- **Layout-0 rows** are read exactly as today.
- **The stored copy that `CommitAsync` returns** is still built from the JSON that was written, with the record's parts put back. Callers see no difference.

### Rolling upgrade and rollback

Migration 0007 only adds a table and a column with a default, so older instances keep working against the new schema. The danger is reading: an older instance that reads a layout-1 row returns a task with empty parts.

So the layout is opt-in:

- `PostgresStoreOptions.ArtifactRows` defaults to `false` in the release that ships this. With it off, the store writes layout 0 and reads both layouts.
- The guide says to turn it on only after every instance runs that release.
- A later release may change the default.
- **Lazy conversion:** a layout-0 task is converted on its first full-path commit with the option on. Old finished tasks stay inline, which costs nothing, because they are never written again.
- **Rollback to an older package:** turn the option off, then run `PostgresMaintenance.InlineArtifactPartsAsync` (new). It rewrites layout-1 tasks back to layout 0 in batches. Only then can the package be downgraded.
- **`PostgresSchema.VerifySchemaAsync`** keeps refusing a database newer than the package, as it does today. So an older package started with `CreateSchemaOnStartup = false` fails loudly instead of misreading.

## Verification

The risk is a task read back differently from how it was committed. The tests below target that.

1. **Round trip after every commit.** A test-only `PostgresStoreOptions.VerifyArtifactRows` reads the task back after each commit and compares its JSON with the committed record's. The comparison is `JsonCanonicalizer`, the same canonical form the idempotency hash uses. On a difference it throws. The store contract tests and the engine scenario tests (`AmbiguousCommitTests`, `CreateCommitLostAnswerTests`, the `many-chunks` and `chunks` scenarios, the artifact offload tests) run against Postgres with the option on and verification on.
2. **Engine invariant.** A test that drives every event kind through `ApplyToCopy`, and asserts that a non-artifact event leaves every artifact's parts unchanged. The full path's skip rule relies on this, so it must fail loudly if the engine ever changes.
3. **Contract.** `TaskStoreContract` gains a Postgres variant with `ArtifactRows = true`.
4. **Mixed layouts.** A layout-0 task is read, appended to with the option on (and so converted), and read again. Then `InlineArtifactPartsAsync` converts it back, and it is read as layout 0.
5. **Cascade.** Each delete path removes the task's part rows.
6. **Migration.** `PostgresMigrationTests` covers 0007 on an empty database and on a database at 0006.
7. **Benchmark.** `tests/A2A.Benchmarks.Postgres` gains a third mode, artifact rows on with batching off. The goal is that its WAL per payload byte stays flat as the chunk count grows, instead of growing with it.

## Rollout

The work fits one PR, test-first, behind the option. It has three parts:
- the migration and the read path, which handles both layouts;
- the write path and compaction;
- `InlineArtifactPartsAsync`, the guide section in `durable-stores.md`, the CHANGELOG, and the ADR-003 decision set to Accepted with a link here.

## Open questions

1. **Rows per append, or one row per batch:** one row per event keeps `ordinal` aligned with the event log, which helps debugging. One row per batch writes fewer rows. Proposed: one row per batch.
2. **Compaction threshold:** 256 is a guess. The benchmark run with the option on should settle it.
3. **Default of `ArtifactRows`:** off in the first release that ships it (proposed), or on for new databases only, detected by an empty `a2a_tasks` at startup.

## As built (2026-11-12)

Where the code differs from the design above:

- **No `VerifyArtifactRows` option.** The tests read each task back after every commit and compare its canonical JSON
  with the committed task (`PostgresArtifactRowsTests`). The whole `TaskStoreContract` also runs with the option on
  (`PostgresArtifactRowsTaskStoreTests`). The engine runs end to end on Postgres with the option on: 100 chunks, some
  batched, some not.
- **`CreateAsync` and `UpdateAsync` write the parts inline (`parts_layout = 0`).** Only `CommitAsync` and
  `CommitBatchAsync` write rows. The next commit of a task written inline rewrites its rows from scratch. A write with
  the option off also sets layout 0, so rows it left behind are ignored, then rewritten when the option is on again.
- **The layout and the row counts are read before the compare-and-set,** in the same transaction and in one statement
  (a `LEFT JOIN LATERAL` of the counts per artifact), so they come from one snapshot. Every write that changes the
  layout or the rows also changes the version: the commits, `UpdateAsync` and `InlineArtifactPartsAsync`. So a write
  that commits after the read the commit is based on makes the compare-and-set fail, and nothing uses the stale counts.
- **Reads are one statement.** `GetAsync`, `GetForMaintenanceAsync` and `ListAsync` read the task row and, for layout 1,
  its parts in the same statement: a scalar subquery `ARRAY(SELECT parts … ORDER BY artifact_id, ordinal)` in the select
  list, inside a `CASE` on `parts_layout`, so a layout-0 task costs nothing more. Not `json_agg` as designed: the rows'
  text goes to the client as it is stored, with no JSON work in the database, and each row's JSON names its artifact.
  `ListAsync` applies its `LIMIT` in a subquery first, so only the page's tasks have their parts read. A first version
  read the parts in a second statement. That could return version *N* with the parts of *N* + 1, or, during
  `InlineArtifactPartsAsync`, with no parts at all.
- **`InlineArtifactPartsAsync` moves the version up by one** (no event, as `UpdateAsync`; `updated_ticks` stays). It
  locks the task row (`FOR UPDATE`) and then reads the rows. A first version kept the version. Then, during the
  documented downgrade, a commit based on a read from before the conversion still passed its compare-and-set. With a
  torn read, it stored every artifact with no parts. From an instance that still had the option on, it set the layout
  back to rows and wrote only its new parts. Both lost parts for good.
  `PostgresArtifactRowsTests` forces both interleavings: one with a hook between the read and the compare-and-set, the
  other with a lock on `a2a_artifact_parts` that holds a read while another transaction commits.
- **Compaction happens on the append itself.** An append to an artifact that already has `ArtifactRowsCompactAt` rows
  rewrites that artifact as one row.
- **A row stores its parts as an artifact** with only its id and parts (`{"artifactId": …, "parts": […]}`). That is a
  type the source-generated JSON context already knows.
- **Open questions:** a batch writes one row per artifact for all its appended parts (question 1: one row per batch).
  256 stays the compaction default (question 2). The option is off by default (question 3).
