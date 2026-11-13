# ADR-005: History and timeline rows for Postgres

**Status:** Proposed (2026-11-13). Built behind an opt-in option, off by default; the owner decides whether to accept it.
**Date:** 2026-11-13
**Deciders:** Repository owner

## Context

[ADR-004](0004-task-copies-per-event.md) made the engine O(1) in memory per event. Its "To revisit" lists: *"History and timeline rows for Postgres (the same idea as ADR-003 option C), if status-heavy tasks show up in WAL."* This ADR is about that follow-up.

How the Postgres store writes a task today:

- **Every commit rewrites the whole task JSON**, with or without `PostgresStoreOptions.ArtifactRows`. `PostgresTaskStore.Snapshot` serializes the task, and the compare-and-set writes it with `UPDATE … SET … task = $8`. With `ArtifactRows` the artifact parts are left out ([ADR-003](0003-task-writes-per-event.md) option C, `a2a_artifact_parts`), but `History` and `Timeline` stay in the JSON.
- **A status update adds to both lists.** The engine (`ApplyToCopy`) appends the status message, if any, to `History` and a status entry to `Timeline`. A follow-up message (`A2ARequestHandler`) appends the message to both. A timeline entry carries the whole status, message included, so a status update with a message adds that message twice to the task JSON.
- **Status updates are never batched** (`IsBatchable` takes only artifact appends). Each one is its own commit.
- **The history has no count cap**; it grows until `A2ALimits.MaxTaskBytes` (64 MiB). The timeline keeps its newest `A2ALimits.MaxTimelineEntries` (1,000) entries.

So a task with *n* status messages writes O(*n*) bytes per update and O(*n*²) in all: WAL, replication, TOAST and the serializer's own allocations.

### Measurements

`tests/A2A.Benchmarks.Postgres -- status` (added with this ADR). An in-process server on the Postgres stores runs an agent that sends N status updates (state working), each with a message of B random letters, or none ("bare"), then completes. One blocking SendMessage per row; WAL from `pg_current_wal_insert_lsn` before and after; allocation is `GC.GetTotalAllocatedBytes` of the whole process (server, client, Npgsql). One run each, on main before this change (`dbab291`), PostgreSQL 16.15 on the session's 4-core container, not `postgres:16-alpine` in Docker:

| Updates | Message bytes | Commits | Total ms | ms / update | WAL MB | WAL KB / update | Allocated MB | KB / update |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1000 | 100 | 1002 | 14462 | 14.46 | 173.0 | 177.1 | 2299.8 | 2355.0 |
| 2000 | 100 | 2002 | 38721 | 19.36 | 614.9 | 314.8 | 7729.8 | 3957.7 |
| 4000 | 100 | 4002 | 121905 | 30.48 | 2033.0 | 520.5 | 24590.5 | 6295.2 |
| 1000 | 1000 | 1002 | 46572 | 46.57 | 1365.9 | 1398.7 | 5755.5 | 5893.6 |
| 2000 | bare | 2002 | 7462 | 3.73 | 15.6 | 8.0 | 847.5 | 433.9 |

What it shows:

- **It is quadratic.** Doubling the updates multiplies the WAL by 3.3–3.6 and the time by 2.7–3.1. 4,000 status messages of 100 bytes (about 0.4 MB of text) wrote 2.0 GB of WAL and allocated 24.6 GB, and took 2 minutes.
- **Longer messages make it worse in proportion.** 1,000 messages of 1,000 bytes wrote 1.37 GB of WAL, 1.4 MB per update.
- **Bare status updates are bounded**, because the timeline cap keeps the task at about 1,000 entries, but each still rewrites them: 8 KB of WAL per update.
- The in-memory engine no longer adds to this (ADR-004); the cost is the store's serialization and write.

## Decision

1. **Store `History` and `Timeline` in rows of one table, `a2a_task_entries` (option C), behind `PostgresStoreOptions.HistoryRows`, off by default.** A status update inserts its message and its timeline entry. Once the timeline is at its cap, the same commit deletes the oldest timeline row. The task JSON keeps `"history": []` and `"timeline": []`.
2. **Mirror ADR-003 option C in everything else:** a layout column per task (`entries_layout`), an idempotent migration (0008) under the existing migration lock, reads in one statement, the compare-and-set unchanged, a downgrade method that moves the version (`InlineHistoryAndTimelineAsync`), and rows deleted with their task (`ON DELETE CASCADE`).
3. **No change to the `TaskStore` contract, the engine, the model or other stores.**
4. **Do not rebuild the lists from the event log (option D).**

## Options considered

### Option A: Keep the lists in the task JSON

| Dimension | Assessment |
|---|---|
| Complexity | None |
| Cost | O(n) bytes per status update, O(n²) per task (the table above) |
| Scalability | Poor for agents that report progress often |
| Team familiarity | High |

**Pros:** one row per task, a read is one column, nothing to keep consistent.

**Cons:** the measured quadratic cost. `ArtifactRows` does not help: it removes only the parts.

### Option B: Two tables, `a2a_task_history` and `a2a_task_timeline`

The same design as C, with one table per list.

| Dimension | Assessment |
|---|---|
| Complexity | Medium: two tables, two foreign keys, two of every statement |
| Cost | Same writes as C |
| Scalability | Same as C |
| Team familiarity | Medium |

**Pros:** each table has a narrower key, and could be tuned or dropped alone.

**Cons:** twice the schema objects, cascade deletes and lock targets, for no gain: both lists change in the same commit and are read together. A `list` column in the key costs two bytes per row.

### Option C: One entries table with a `list` column (chosen)

```sql
CREATE TABLE IF NOT EXISTS {s}.a2a_task_entries
(
    tenant text COLLATE "C" NOT NULL, task_id text COLLATE "C" NOT NULL,
    list smallint NOT NULL,      -- 0 history, 1 timeline
    ordinal bigint NOT NULL,     -- place in the list; contiguous per list
    entry text NOT NULL,         -- one Message or TimelineEntry, A2A wire JSON
    PRIMARY KEY (tenant, task_id, list, ordinal),
    FOREIGN KEY (tenant, task_id) REFERENCES {s}.a2a_tasks (tenant, task_id) ON DELETE CASCADE
);
ALTER TABLE {s}.a2a_tasks ADD COLUMN IF NOT EXISTS entries_layout smallint NOT NULL DEFAULT 0;
```

| Dimension | Assessment |
|---|---|
| Complexity | Medium: one table, one migration, an incremental write, a read that joins |
| Cost | Postgres only; no contract, engine or model change |
| Scalability | A status update writes its message, its timeline entry and a task row without the lists |
| Team familiarity | Medium: the same shape as `a2a_artifact_parts` |

**Pros:**
- The write no longer grows with the task.
- One row per entry, so the timeline cap is a `DELETE … WHERE ordinal <= $n` and no compaction is needed, unlike artifact rows: the history is bounded only by `MaxTaskBytes`, as it is in the JSON, and the timeline by its cap.
- `ordinal` is contiguous per list, so the store reads a list's range as `min` and `max` over the primary key: O(log n) per commit, not a count of the rows.

**Cons:**
- A read joins rows (in the same statement, so it is never torn).
- The store must recognize an append. It does so from the commit's event (a status update adds its message, if any, and one timeline entry; a message update adds its message and one entry) and checks that the committed lists end with those items and that the counts fit. Anything else rewrites the list from the task. This relies on the same engine invariant as `ArtifactRows`: only status and message updates change the history and the timeline.
- The conversation memory query (`ReadContextMessagesAsync`) reads the history from the JSON in SQL, so it gains a branch that reads the rows.
- The store still builds the committed task's `History` and `Timeline` lists in memory once per commit (O(n) references, not O(n) bytes): `A2A.Protocol` has no accessor that counts them or reads their newest items without building them, as `ModelCopies.CountParts` and `CopyLastParts` do for parts. See "Consequences".

### Option D: Rebuild the lists from the event log

`a2a_events` already holds every status and message update.

**Cons:** it keeps only the last `ReplayBufferSize` events per task, so it cannot be the source of the history. Rebuilding is ADR-003's option B (replay the engine's apply logic in reads), which ADR-003 rejected.

## Trade-off analysis

- **A** leaves a measured O(n²) on every status-heavy task.
- **B and C** write the same bytes; C has half the schema and the same reads.
- **D** cannot work with a trimmed log.
- **C behind an option** keeps the default path as it is, and the rollout and rollback rules of `ArtifactRows`, which shipped and were reviewed, apply as they are.

## Results

The same benchmark after the change, with the option off (the default, unchanged path) and on. One run, same machine:

| Updates | Message bytes | History rows | Commits | Total ms | ms / update | WAL MB | WAL KB / update | Allocated MB | KB / update |
|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|
| 1000 | 100 | off | 1002 | 12947 | 12.95 | 175.9 | 180.1 | 2296.2 | 2351.3 |
| 1000 | 100 | on | 1002 | 2971 | 2.97 | 3.1 | 3.2 | 54.1 | 55.4 |
| 2000 | 100 | off | 2002 | 35986 | 17.99 | 606.6 | 310.6 | 7740.4 | 3963.1 |
| 2000 | 100 | on | 2002 | 7424 | 3.71 | 6.5 | 3.3 | 116.5 | 59.7 |
| 4000 | 100 | off | 4002 | 132244 | 33.06 | 2023.5 | 518.0 | 24608.4 | 6299.8 |
| 4000 | 100 | on | 4002 | 20898 | 5.22 | 13.2 | 3.4 | 264.9 | 67.8 |
| 1000 | 1000 | off | 1002 | 41917 | 41.92 | 1363.5 | 1396.2 | 5765.6 | 5904.0 |
| 1000 | 1000 | on | 1002 | 2652 | 2.65 | 6.6 | 6.7 | 85.8 | 87.9 |
| 2000 | bare | off | 2002 | 5719 | 2.86 | 15.8 | 8.1 | 848.3 | 434.3 |
| 2000 | bare | on | 2002 | 4882 | 2.44 | 3.4 | 1.7 | 66.4 | 34.0 |

- **The WAL per update is flat with the option on:** 3.2–3.4 KB for 100-byte messages at 1,000 to 4,000 updates, against 180–518 KB with it off. 4,000 updates write 13.2 MB instead of 2.0 GB (153 times less). With 1,000-byte messages, 6.7 KB per update instead of 1.4 MB.
- **With messages, allocation drops 42–93 times** (24.6 GB to 265 MB at 4,000), and the time per update 4.4–16 times.
- **What still grows:** the allocation per update goes from 55 KB to 68 KB between 1,000 and 4,000 updates, and the time per update from 3.0 to 5.2 ms. That is the store building the history and timeline lists once per commit (above), plus the reads of growing lists. It is linear per update, but small next to the write it replaced.
- **Bare updates** were already bounded by the timeline cap; they drop from 8.1 to 1.7 KB of WAL per update, and from 848 MB to 66 MB of allocation (the time barely moves: 2.86 to 2.44 ms).
- **The option off** matches the numbers before the change (WAL within 2%; times within run-to-run noise).

## Consequences

- **Easier:** agents that report progress often (status messages, follow-up turns) no longer write the whole conversation on every update. Long multi-turn tasks become cheap to keep on Postgres.
- **Harder:**
  - A second row form to keep consistent with the task JSON. The tests read each task back after every commit and compare it with the committed task, and with the same task stored inline, byte for byte.
  - Rolling upgrade and downgrade follow the `ArtifactRows` rules: turn it on only when every instance has the option; before a downgrade turn it off and run `InlineHistoryAndTimelineAsync` until it returns 0.
- **To revisit:**
  - **Model accessors**, so the store need not build the lists: `ModelCopies.CountHistory(this AgentTask)`, `CopyLastHistory(this AgentTask, int count)`, `CountTimeline(this AgentTask)` and `CopyLastTimeline(this AgentTask, int count)` (the last two experimental, like `TimelineEntry`), and a null check that does not build the list (`HasHistory`/`HasTimeline`, or a count of -1 for null). `LazyList<T>` already has `Count` and `GetLast`; they would wrap them, as `CountParts` and `CopyLastParts` wrap the parts. With them the commit would cost O(the entries it adds) in memory too. This needs a change in `A2A.Protocol`, outside this work.
  - **The default.** Off in the release that ships it, as with `ArtifactRows`. Both could turn on together for new databases later.
  - **The fixed cost per commit** (the task row, the event row, indexes): about 1.7 KB of WAL for a bare update.

## Action items

1. [x] Add the status-heavy case to `tests/A2A.Benchmarks.Postgres` (`-- status`) and measure before.
2. [x] Build option C behind `PostgresStoreOptions.HistoryRows`. Design as built: `docs/superpowers/specs/2026-11-13-postgres-history-rows-design.md`.
3. [x] Tests: the `TaskStoreContract` with the option on (and with `ArtifactRows` too); append, trim, rewrite on mismatch, toggling, the downgrade, the race cases, untorn reads, identical read-back against inline storage, cascade, the migration, and the engine end to end.
4. [x] Document the option in `docs/site/guides/durable-stores.md`.
5. [ ] Owner: accept or reject; decide on the model accessors above.
6. [ ] Run the stores tests and the benchmark on CI's `postgres:16-alpine` (only a local PostgreSQL 16.15 was used).
