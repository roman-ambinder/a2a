-- PostgresStoreOptions.ArtifactRows (ADR-003, docs/superpowers/specs/2026-11-12-postgres-artifact-rows-design.md): the parts
-- of a task's artifacts in rows, so an append writes its new parts instead of rewriting the whole task.
-- parts_layout 0: the parts are in a2a_tasks.task, as before. 1: every artifact in a2a_tasks.task has "parts": [], and its
-- parts are its rows here, in ordinal order (an artifact with no rows has no parts).
CREATE TABLE IF NOT EXISTS {s}.a2a_artifact_parts
(
    tenant      text COLLATE "C" NOT NULL,
    task_id     text COLLATE "C" NOT NULL,
    artifact_id text COLLATE "C" NOT NULL,
    ordinal     integer NOT NULL,
    part_count  integer NOT NULL,
    parts       text NOT NULL,
    PRIMARY KEY (tenant, task_id, artifact_id, ordinal),
    FOREIGN KEY (tenant, task_id) REFERENCES {s}.a2a_tasks (tenant, task_id) ON DELETE CASCADE
);
ALTER TABLE {s}.a2a_tasks ADD COLUMN IF NOT EXISTS parts_layout smallint NOT NULL DEFAULT 0;
