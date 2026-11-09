-- PushOutbox.LeaseDueCappedAsync counts the in-flight rows of each owner: finds them without a table scan.
CREATE INDEX IF NOT EXISTS a2a_push_outbox_leased_ix ON {s}.a2a_push_outbox (owner) WHERE lease_ticks IS NOT NULL;
