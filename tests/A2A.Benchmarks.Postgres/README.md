# Postgres write-cost benchmark

A one-shot measurement for ADR-003 (`docs/adr/0003-task-writes-per-event.md`): what one long chunked artifact costs on
the Postgres stores, with and without batch commits.

It is not part of the CI benchmark gate (`tests/A2A.Benchmarks`), because that job runs without Docker. CI only builds
this project.

Run (from the repo root; needs Docker, or a server in `A2A_BENCH_POSTGRES`):

    dotnet run -c Release --project tests/A2A.Benchmarks.Postgres

For each case, an in-process server with the Postgres stores runs an agent that streams one artifact in N chunks of B
bytes, and the client sends one blocking SendMessage. It prints a Markdown table with:

- **Commits:** task-store commits (single and batch).
- **Total ms** and **ms / chunk:** wall time of the SendMessage.
- **WAL MB:** WAL written during the run (`pg_current_wal_insert_lsn` before and after). This counts every byte the
  commits wrote: the task row, the event log, TOAST and indexes.
- **WAL / payload:** that WAL divided by the artifact's own bytes (N × B).

"Batch commits off" wraps the same `PostgresTaskStore` without `IBatchTaskCommit`, so every event is its own commit.
Each case uses a fresh schema, which is dropped afterwards.

## Status-heavy tasks: WAL and memory

    dotnet run -c Release --project tests/A2A.Benchmarks.Postgres -- status

For ADR-005 (`docs/adr/0005-history-and-timeline-rows.md`). The agent sends N status updates, each with a message of B
random letters (or none: "bare"), then completes. Status updates are never batched, so each one is its own commit. The
table shows the commits, the wall time, the WAL written, and the bytes the process allocated, in total and per update,
with `HistoryRows` on and off. With it off, each commit rewrites the whole history and timeline, so the WAL per update
grows with the task. With it on, a status update inserts its message and its timeline entry, so the WAL per update stays
flat. The run checks that the stored task has every message (the response returns only the newest
`MaxHistoryLengthReturned`, so it reads the store).

## Paced appends: memory

    dotnet run -c Release --project tests/A2A.Benchmarks.Postgres -- paced

The agent waits 1 ms before each append, so chunks rarely queue into batches (as with a model streaming tokens), and
appends N parts of 100 bytes to one artifact. The table shows the bytes the whole process allocated during the
SendMessage (`GC.GetTotalAllocatedBytes`: server, client and Npgsql), in total and per append, with `ArtifactRows` on
and off. With it on, the store counts the parts and reads only the new ones (`ModelCopies.CountParts`,
`CopyLastParts`), so the allocation per append stays flat as the artifact grows. With it off, each commit serializes
the whole task.
