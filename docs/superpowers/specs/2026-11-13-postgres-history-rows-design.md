# Postgres history rows: status updates without rewriting the task

Status: implemented, opt-in (`PostgresStoreOptions.HistoryRows`). Option C of ADR-005
(`docs/adr/0005-history-and-timeline-rows.md`), the follow-up ADR-004 lists under "To revisit". It mirrors the artifact
rows of ADR-003 (`2026-11-12-postgres-artifact-rows-design.md`).

## Why

`PostgresTaskStore` writes the whole task JSON on every commit, `History` and `Timeline` included, also with
`ArtifactRows` on. A status update with a message adds that message to the history and a status entry (which holds the
message again) to the timeline, and status updates are never batched. So a task with *n* status messages writes O(*n*)
bytes per update. The ADR-005 benchmark (`tests/A2A.Benchmarks.Postgres -- status`) measured 4,000 updates of 100 bytes
at 2.0 GB of WAL, 24.6 GB allocated and 122 s.

## Goals

1. A status update or a follow-up message writes its new history message and timeline entry, and a task row without
   the lists, not the whole task.
2. The timeline cap (`A2ALimits.MaxTimelineEntries`) holds in the rows too: the rows are the task's timeline.
3. No change to the `TaskStore` contract, the engine, the model, the wire format or other stores.
4. A task reads back identical to the task committed, and to the same task stored inline, byte for byte in its JSON.
5. A rolling upgrade never shows a task with a missing history to an older instance; a downgrade loses nothing.

## Non-goals

- **Model accessors.** The store reads `History` and `Timeline`, which builds the committed task's lists once per
  commit (O(n) references). Counting accessors in `A2A.Protocol` would remove that; ADR-005 lists what they would be.
- **Fewer event-log or outbox bytes.** Each status update still goes once into `a2a_events`, and once per webhook into
  `a2a_push_outbox`.
- **A history cap.** The history stays uncapped, as in the JSON (bounded by `MaxTaskBytes`).

## Design

### Storage

Migration `0008_task_entries.sql` (idempotent, applied under the existing advisory lock like every migration):

```sql
CREATE TABLE IF NOT EXISTS {s}.a2a_task_entries
(
    tenant  text COLLATE "C" NOT NULL,
    task_id text COLLATE "C" NOT NULL,
    list    smallint NOT NULL,   -- 0: History, 1: Timeline
    ordinal bigint NOT NULL,     -- place in the list
    entry   text NOT NULL,       -- one Message or TimelineEntry, A2A wire JSON
    PRIMARY KEY (tenant, task_id, list, ordinal),
    FOREIGN KEY (tenant, task_id) REFERENCES {s}.a2a_tasks (tenant, task_id) ON DELETE CASCADE
);
ALTER TABLE {s}.a2a_tasks ADD COLUMN IF NOT EXISTS entries_layout smallint NOT NULL DEFAULT 0;
```

- **`entries_layout`:**
  - **0 (inline):** as before.
  - **1 (rows):** the task JSON has `"history": []` where the task has a history and no `history` where it has none (the
    same for `timeline`), and the items are the rows of that list in `ordinal` order. So a null list and an empty list
    read back as they were.
- **One row per item.** The ordinals of a list are contiguous: an append takes the next ones, and the cap deletes the
  lowest. No compaction is needed: the timeline is bounded by its cap and the history by `MaxTaskBytes`.
- **`ON DELETE CASCADE`** covers every path that deletes a task: `DeleteAsync`, the retention sweeps and
  `PostgresMaintenance`.
- **Independent of `parts_layout`.** A task can have its parts in rows, its lists in rows, both or neither.

### Write path

Only `CommitAsync` and `CommitBatchAsync` write rows, and only with the option on. `CreateAsync` and `UpdateAsync` write
inline (`entries_layout = 0`), and so does every write with the option off; rows they leave behind are ignored, then
rewritten by the next commit with the option on.

1. **Before the compare-and-set**, in the commit's transaction and in one statement: the task's `entries_layout`, and
   `min(ordinal)` and `max(ordinal)` of each list. PostgreSQL answers each from an end of the primary key: O(log n), not
   a count. Every write that changes the layout or the rows also changes the version, so a write that lands between this
   read and the compare-and-set makes the compare-and-set fail, and nothing uses the stale range. A test hook
   (`BeforeCompareAndSet`) runs here.
2. **The task row** is written from a copy with empty lists (`PostgresEntryRows.ForRow`), by the same compare-and-set
   `UPDATE` as before, with `entries_layout = 1`.
3. **The rows, after the compare-and-set**, per list:
   - What the commit's events add, as the engine applies them: a status update adds its message (if any) to the history
     and its status to the timeline; a message update adds its message to both; other events add nothing.
   - **Append** when the task was in rows, the events add *k* > 0 items, the newest *k* items of the committed list are
     those (compared as JSON: the message, or the entry's status or message), and the count fits: for the history,
     stored + *k*; for the timeline, between *k* and stored + *k* (the cap dropped the difference). It inserts the *k*
     rows after the highest ordinal (one `INSERT … SELECT … FROM unnest($5::text[]) WITH ORDINALITY`), then, when the cap
     dropped entries, deletes the rows up to the newest ordinal minus the committed count.
   - **Untouched** when the events add nothing and the count did not change: only status and message updates change
     these lists, as the engine is written (`ApplyToCopy`, `A2ARequestHandler`). Batch commits are artifact appends
     (`IsBatchable`), so they always take this path.
   - **Rewrite** otherwise: delete the list's rows and insert the committed list from ordinal 0. That covers a create,
     a task stored inline (lazy conversion on its first commit with the option on), and a commit whose lists do not
     match its rows.
4. **The stored copy the commit returns** is a `CopyForUpdate` of the committed task, taken before the lists are read,
   so it shares them (O(1), ADR-004), with every other property from the task row as written and read back.

What a status update costs: its message and its timeline entry, once each; a task row whose size no longer includes the
lists; one `DELETE` of one row once the timeline is at its cap.

### Read path

- **`GetAsync`, `GetForMaintenanceAsync`, `ListAsync`:** the task row and, for layout 1, the entries of each list in the
  same statement: a scalar `ARRAY(SELECT entry … ORDER BY ordinal)` per list inside a `CASE` on `entries_layout`, as
  `ArtifactRows` reads parts. A layout-0 task costs nothing more. `ListAsync` applies its `LIMIT` first. One statement
  means one snapshot: the entries always belong to the version read, also while `InlineHistoryAndTimelineAsync` runs.
- **`ReadContextMessagesAsync`** (conversation memory) cut the history in SQL from `task::json -> 'history'`. It now
  takes, per task, either the JSON array (layout 0) or the history rows (layout 1, `UNION ALL`). A row holds the same
  text as the element of the inline array, so the byte budget cuts at the same place. The rows are not parsed as JSON in
  SQL, so a message with `\u0000` in a task in rows no longer needs the fallback that loads whole tasks.

### Rolling upgrade and rollback

As for `ArtifactRows`:

- Migration 0008 only adds a table and a column with a default, so older instances keep working against the new schema.
- The option defaults to `false`. Turn it on only after every instance runs a release that has it: an older instance
  reads a task in rows with an empty history and timeline.
- **Downgrade:** turn the option off, then call `PostgresTaskStore.InlineHistoryAndTimelineAsync` until it returns 0. It
  locks each task row (`FOR UPDATE`), reads the rows, writes the lists back into the JSON with `entries_layout = 0`, moves
  the version up by one (no event; `updated_ticks` stays), and deletes the rows. Parts in rows stay where they are: run
  `InlineArtifactPartsAsync` for those.
- **Why the version moves:** a commit based on a read from before the conversion must fail its compare-and-set. Else an
  instance with the option still on, whose pre-compare-and-set read saw the rows just before they were deleted, would set
  the layout back to rows and insert only its new entries: every older message would read back gone.
- `PostgresSchema.VerifySchemaAsync` keeps refusing a database newer than the package.

## Verification

`tests/A2A.Stores.Tests`:

1. **Contract.** `PostgresHistoryRowsTaskStoreTests` runs the whole `TaskStoreContract` with the option on, and
   `PostgresAllRowsTaskStoreTests` with `ArtifactRows` on too. The existing Postgres contract runs with it off.
2. **Round trip after every commit** (`PostgresHistoryRowsTests`): each test commit is read back and compared with the
   committed task (canonical JSON), and so is the copy the commit returns.
3. **Identical to inline:** the same sequence of status updates and follow-ups, some with `\u0000`, committed to a store
   with the option and one without, reads back byte for byte the same through `GetAsync`, `ListAsync` and
   `GetForMaintenanceAsync`, and `ReadContextMessagesAsync` returns the same messages at three byte budgets.
4. **Append and trim:** one row per message and entry; with a cap of 4 the timeline rows stay at 4 with contiguous
   ordinals; a smaller cap drops two at once, a larger one none; the history is never trimmed.
5. **Null and empty lists** read back as they were.
6. **Rewrite on mismatch:** a commit whose history does not end with the event's message, or whose count does not fit,
   rewrites the list.
7. **Toggling:** off writes inline and either store reads it; on again converts it on the next commit; `UpdateAsync`
   writes inline.
8. **Downgrade:** `InlineHistoryAndTimelineAsync` converts the task, moves the version and keeps `UpdatedAt` and the
   parts rows; then `InlineArtifactPartsAsync` keeps the lists inline.
9. **Races:** a commit based on a read from before the conversion (whole or torn) fails its compare-and-set from either
   instance; the conversion forced between a commit's read and its compare-and-set (hook) fails that commit, and its
   retry keeps every message.
10. **Untorn reads:** the lock technique of `PostgresArtifactRowsTests`. Another transaction locks `a2a_task_entries`,
    the read waits on it, the transaction writes version N + 1 (an appended status, or the conversion) and commits; the
    read returns N + 1 whole, for `GetAsync` and `ListAsync`.
11. **Cascade:** deleting the task deletes its rows.
12. **Model guard:** `ForRow` and `Restore` keep every `AgentTask` property but the two lists (reflection, so a new
    property fails the test).
13. **Engine end to end:** an agent sends 2 × 30 status updates around an input-required turn and a follow-up, with
    artifacts in between and `MaxTimelineEntries = 10`; the stored history has every message, the timeline its newest 10,
    and the task equals itself after `InlineHistoryAndTimelineAsync`.
14. **Migration:** 0008 runs twice on a database that has it and keeps its rows; the new tables are in the
    fresh-schema check.

`tests/A2A.Benchmarks.Postgres -- status` measures WAL, time and allocation per update with the option on and off
(ADR-005, "Results").
