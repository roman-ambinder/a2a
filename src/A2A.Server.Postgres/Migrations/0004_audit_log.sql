-- The audit trail (PostgresAuditSink). Append only: the SDK never updates or deletes a row, and nothing here expires them.
-- A fail-closed action has two rows with the same id: Attempted, then its final outcome. No content is ever stored: only
-- ids, an error code name or a fixed word as reason, and the caller's address.
CREATE TABLE IF NOT EXISTS {s}.a2a_audit_log (
    seq            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    id             text COLLATE "C" NOT NULL,
    time_ticks     bigint NOT NULL,
    action         text   NOT NULL,
    outcome        text   NOT NULL,
    actor          text COLLATE "C" NOT NULL,
    tenant         text COLLATE "C" NOT NULL,
    task_id        text COLLATE "C" NULL,
    resource       text COLLATE "C" NULL,
    operation      text   NULL,
    reason         text   NULL,
    binding        text   NULL,
    remote_address text   NULL,
    trace_id       text   NULL
);
CREATE INDEX IF NOT EXISTS a2a_audit_log_tenant_time_ix ON {s}.a2a_audit_log (tenant, time_ticks DESC);
CREATE INDEX IF NOT EXISTS a2a_audit_log_task_ix ON {s}.a2a_audit_log (tenant, task_id, time_ticks) WHERE task_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS a2a_audit_log_actor_ix ON {s}.a2a_audit_log (tenant, actor, time_ticks);
