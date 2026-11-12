# ADR-003: How a task is written on each event

**Status:** Accepted (2026-11-12; Option C was chosen for build by the owner)
**Date:** 2026-11-12
**Deciders:** Repository owner

## Context

ADR-001 left this open under "To revisit": *"appending deltas instead of rewriting the whole task on each event. `TaskCommit` already carries `Event`, so the write side needs no contract change. The read side may."*

How a task is written today:

- **Every event rewrites the whole task.**
  - The engine applies the event to a copy of the task (`AgentTask.CopyForUpdate`). It then commits the copy, the event and its push deliveries as one `TaskCommit`.
  - `PostgresTaskStore` turns the whole record into JSON (`ForUpdate` → `Snapshot`) and writes it with `UPDATE … SET … task = $8`. The `task` column is `text`.
  - So each commit writes as many bytes as the whole task holds, whatever the event added.
- **An append to an artifact copies that artifact's part list.** `Artifact.CopyWithParts` creates a new `Parts` list with the old parts and the new ones. The in-memory store keeps instances and serializes nothing, but it still pays for this copy.
- **What already limits the cost:**
  - Chunks that queue up are committed as one batch (`IBatchTaskCommit`). That means one task write per batch of up to `MaxCommitBatch` = 64 events (`TaskExecutionEngine.cs`).
  - Artifacts above `ArtifactStorageOptions.InlineThresholdBytes` (default 256 KiB) can be moved to an artifact object store, so the task keeps a link instead of the bytes.
  - `A2ALimits.MaxTaskBytes` caps a task at 64 MiB by default.
- **The event log is already stored separately.** `a2a_events` keeps the last `ReplayBufferSize` events per task (default 1,000), so the deltas exist already. They are trimmed, though, so they cannot replace the task row on their own.

So for an artifact streamed in *n* chunks that are not batched, the bytes written grow with *n*² on Postgres: chunk *k* rewrites the *k* chunks before it. Batching divides that by up to 64. Offloading keeps the big artifacts out of the task.

**What is not known:**
- How much this costs at realistic sizes. The only streaming benchmark, `StreamingTaskBenchmark.ThousandEvents` (1,000 artifact appends), runs on the in-memory stores. Its baseline is a mean of about 10.5 ms and 5.67 MB allocated (`tests/A2A.Benchmarks/baseline.json`).
- What it costs on Postgres. There is no Postgres benchmark.
- No user has reported it as a problem.

## Decision

1. **Measure before changing anything.** Add a Postgres benchmark of a long chunked artifact, and record the bytes written per commit and the time per chunk. Cover batching on and off, and a few task sizes.
2. **If the cost matters, change how the Postgres store writes, not the contract (Option C).** The store already receives the event with each commit. For an append to an artifact, it can add the new parts to that artifact's row instead of rewriting the whole task. `GetAsync` assembles the task from its rows. The `TaskStore` contract, the engine and other stores stay as they are.
3. **Do not change the contract to delta storage (Option B)** unless Option C turns out not to be enough.

## Options considered

### Option A: Keep rewriting the whole task

| Dimension | Assessment |
|---|---|
| Complexity | None: it is the current code |
| Cost | Bytes written per commit grow with task size; with batching, up to 64 times fewer commits |
| Scalability | Fine for small tasks; poor for long unbatched chunk streams on Postgres |
| Team familiarity | High |

**Pros:** It is simple and already tested. A read is one row. A failed write leaves no partial state.

**Cons:** The write cost grows with the task. The in-memory store still copies an artifact's part list on every append.

### Option B: Delta storage in the contract

The task row would hold a snapshot taken every so often. A read would apply the events after the snapshot to it. A new capability interface would let the engine commit only the event.

| Dimension | Assessment |
|---|---|
| Complexity | High: a read must replay events exactly as the engine applied them |
| Cost | A contract change, a capability interface (ADR-001 rule 2), and a snapshot policy tied to `ReplayBufferSize` |
| Scalability | The best write cost: each commit writes only the event |
| Team familiarity | Low: nothing in the code replays events into a task today |

**Pros:** Each commit writes only what changed.

**Cons:**
- A read costs more, and correctness rests on replay matching the engine's apply logic. That includes size caps, sealing, generations, and failure events that replace an oversized event.
- Events are trimmed after `ReplayBufferSize`, so a snapshot must be written before that happens.
- Every third-party store would have to implement it, or keep the old path.

### Option C: Per-artifact rows inside the Postgres store (chosen, if measurement justifies it)

| Dimension | Assessment |
|---|---|
| Complexity | Medium: a new table, a migration, and an incremental write for appends |
| Cost | Postgres only. No contract change, and the engine is unchanged |
| Scalability | An append writes its new parts plus a small task header, not the whole task |
| Team familiarity | Medium: the store already uses migrations and batches |

**Pros:**
- The contract stays as it is, as ADR-001 expected ("the write side needs no contract change").
- Other stores are not affected.
- It can be done after the release without breaking anyone.

**Cons:**
- A read joins rows.
- The store must keep the row form and the task JSON consistent. For example, the engine's size checks still see the whole task.
- It needs a migration, and a test that a task read back equals the task committed.
- The in-memory copy of the part list is not addressed. That copy is in the engine, not the store.

## Trade-off analysis

- **The cost is real but unmeasured.** Batching and offloading already remove most of it for the common case: fast chunk streams and big artifacts. Building Option B or C without numbers would add risk to the code that holds task state, for a gain nobody has shown.
- **Option B is the strongest on paper and the riskiest.** It moves the engine's apply logic into reads, ties correctness to the replay buffer, and changes the contract for every store.
- **Option C reaches most of the same write saving for Postgres.** It changes neither the contract nor the engine, so it can come later if the benchmark shows it is needed.

## Consequences

- **Easier:** nothing changes until there are numbers. The contract stays as ADR-001 froze it.
- **Harder:** a user with a long, slow chunk stream on Postgres keeps paying the current cost until Option C is built. Offloading the artifact (`ArtifactStorageOptions`) is the workaround now.
- **To revisit:**
  - Option C, after the benchmark.
  - The in-memory part-list copy, if the in-memory store is used in production (the guides call it a development store).

## Action items

1. [x] Add `PostgresStreamingBenchmark` (Testcontainers Postgres). It covers a 1,000-chunk artifact, with and without batching, at three artifact sizes, and reports the bytes written per commit (`pg_stat_statements` or the parameter size) and the mean time per chunk.

   Done as `tests/A2A.Benchmarks.Postgres`, a console measurement outside the CI benchmark gate. It measures the WAL written (`pg_current_wal_insert_lsn` before and after) rather than the parameter size, so the event log, TOAST and indexes are counted too. One run on 2026-11-12, `postgres:16-alpine` in Docker on the session's container:

   | Chunks | Chunk bytes | Batch commits | Commits | Total ms | ms / chunk | WAL MB | WAL / payload |
   |---:|---:|---|---:|---:|---:|---:|---:|
   | 1000 | 100 | on | 21 | 1527 | 1.53 | 1.9 | 19.9 |
   | 1000 | 100 | off | 1003 | 5609 | 5.61 | 60.7 | 636.9 |
   | 1000 | 1000 | on | 21 | 1662 | 1.66 | 12.8 | 13.4 |
   | 1000 | 1000 | off | 1003 | 20029 | 20.03 | 534.5 | 560.4 |
   | 250 | 10000 | on | 9 | 855 | 3.42 | 14.6 | 6.1 |
   | 250 | 10000 | off | 253 | 10870 | 43.48 | 334.7 | 140.4 |

   What it shows:
   - **With batching, the cost is small.** 1,000 chunks took 21 commits and wrote 13–20 times the payload in WAL.
   - **Without batching, it is quadratic.**
     - The same 1,000 chunks of 1,000 bytes wrote 534.5 MB of WAL for 1 MB of payload (560 times).
     - It took 20 ms per chunk, against 1.7 ms with batching.
     - The time per chunk grows with the artifact: 5.6 ms at 100 bytes, 20 ms at 1,000 bytes, 43 ms at 10,000 bytes.
   - **"Off" is also what a slow producer gets with batching on.** Batching only merges chunks that queue up behind a commit. An agent that streams tokens slower than a commit takes, for example a model at tens of tokens per second, gets one commit per chunk. So its long artifacts pay the "off" row. The latency fits between tokens, but the WAL, replication and storage load stays quadratic.
   - **Limits of this measurement:**
     - It is a single run on one machine, so the times are rough. The WAL figures depend less on timing.
     - Real model text compresses differently from these random letters, and TOAST compresses the `task` column.
2. [x] Decide from the numbers (owner). The owner chose to build Option C on 2026-11-12. Design for Option C: `docs/superpowers/specs/2026-11-12-postgres-artifact-rows-design.md`. The "off" row is the slow-producer case, and it costs 140–640 times the payload in WAL, against 6–20 times with batching. That is the factor Option C would remove for appends. If a long chunk stream costs more than its batched equivalent by a factor worth fixing, design Option C in its own short spec: the table, the migration, and a round-trip test.
3. [x] Until then, say in `docs/site/guides/durable-stores.md` that each commit rewrites the whole task, and that long artifacts should be offloaded.

   Done with Option C: the guide's "Artifact rows" item says both, and describes `PostgresStoreOptions.ArtifactRows`.

## Results with Option C

The same benchmark with `PostgresStoreOptions.ArtifactRows` (one run, 2026-11-12, same setup as above):

| Chunks | Chunk bytes | Batch commits | Artifact rows | Commits | ms / chunk | WAL MB | WAL / payload |
|---:|---:|---|---|---:|---:|---:|---:|
| 1000 | 100 | off | off | 1003 | 4.98 | 60.7 | 636.9 |
| 1000 | 100 | off | on | 1003 | 3.50 | 2.1 | 22.3 |
| 1000 | 1000 | off | off | 1003 | 19.26 | 534.5 | 560.5 |
| 1000 | 1000 | off | on | 1003 | 3.85 | 5.3 | 5.5 |
| 250 | 10000 | off | off | 253 | 54.14 | 334.7 | 140.4 |
| 250 | 10000 | off | on | 253 | 6.25 | 5.6 | 2.4 |
| 1000 | 1000 | on | off | 21 | 1.33 | 12.8 | 13.4 |
| 1000 | 1000 | on | on | 21 | 1.20 | 2.5 | 2.7 |

- **One commit per chunk** (the slow-producer case) no longer grows with the artifact. WAL per payload byte drops from 140–637 to 2.4–22.3. The time per chunk drops from 5.0–54.1 ms to 3.5–6.3 ms.
- **With batching,** it improves too: 13.4 times the payload becomes 2.7.
- **What is left:** at 100-byte chunks the fixed cost of each commit dominates (22 times the payload). That cost is the task header, the event row and the indexes.
