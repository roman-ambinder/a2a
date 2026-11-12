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
