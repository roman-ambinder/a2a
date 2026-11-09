-- The rolling summary of a conversation. Written by the agent; null when there is none.
ALTER TABLE {s}.a2a_contexts ADD COLUMN IF NOT EXISTS summary text NULL;

-- ListContextsAsync: one owner's contexts, newest first, keyset paging on (created_ticks, context_id).
CREATE INDEX IF NOT EXISTS a2a_contexts_owner_ix ON {s}.a2a_contexts (tenant, owner, created_ticks DESC, context_id);
