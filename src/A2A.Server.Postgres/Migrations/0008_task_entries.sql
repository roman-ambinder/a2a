-- PostgresStoreOptions.HistoryRows (ADR-005, docs/superpowers/specs/2026-11-13-postgres-history-rows-design.md): a task's
-- history messages and A2A 1.1 timeline entries in rows, so a status update writes its new message and timeline entry
-- instead of rewriting the whole task.
-- list 0: one History message per row; list 1: one Timeline entry per row. ordinal is the entry's place in its list. The
-- ordinals of a list are contiguous: an append takes the next one, and the timeline cap (A2ALimits.MaxTimelineEntries)
-- deletes the lowest.
-- entries_layout 0: History and Timeline are in a2a_tasks.task, as before. 1: a2a_tasks.task has "history": [] and
-- "timeline": [] where the task has them (absent where it has none), and their items are the rows here, in ordinal order.
CREATE TABLE IF NOT EXISTS {s}.a2a_task_entries
(
    tenant  text COLLATE "C" NOT NULL,
    task_id text COLLATE "C" NOT NULL,
    list    smallint NOT NULL,
    ordinal bigint NOT NULL,
    entry   text NOT NULL,
    PRIMARY KEY (tenant, task_id, list, ordinal),
    FOREIGN KEY (tenant, task_id) REFERENCES {s}.a2a_tasks (tenant, task_id) ON DELETE CASCADE
);
ALTER TABLE {s}.a2a_tasks ADD COLUMN IF NOT EXISTS entries_layout smallint NOT NULL DEFAULT 0;
