-- ITaskCommitLookup: the mutation id of the commit that stored each event, so the engine can tell whether a commit whose
-- answer was lost was stored after all. Null for events stored without one (and by instances older than this migration).
ALTER TABLE {s}.a2a_events ADD COLUMN IF NOT EXISTS mutation_id text NULL;
