-- The work a leased job carries, and the only thing that makes a run leasable at all (ADR-0033 slice 4b).
--
-- Slice 4a could claim any run that was pending or running, which is every fix the hosted Conductor is in
-- the middle of executing: it never takes a lease, so its rows look free for the whole run. A runner
-- polling the same org would claim work already under way and open a second draft pull request for one
-- issue. Presence of this column is what separates the two paths, and a hosted run leaves it null, so the
-- exclusion holds by construction rather than by everyone remembering it.
--
-- It carries the context because a runner has no other way to get it. The prompt is assembled and
-- double-scrubbed at request time and is otherwise deliberately not persisted (fix_audit stores only its
-- hash). Storing it here is scoped to exactly the runs that leave our infrastructure anyway, so the
-- hosted path keeps the property it has today.
ALTER TABLE fix_suggestions
    ADD COLUMN IF NOT EXISTS job_context JSONB;

-- Replaces the slice-4a index, which covered every pending or running row. A leasable job is now a far
-- smaller set, and the index should describe it rather than the whole fix table.
DROP INDEX IF EXISTS fix_suggestions_claimable_idx;

CREATE INDEX IF NOT EXISTS fix_suggestions_claimable_idx
    ON fix_suggestions (created_at)
    WHERE job_context IS NOT NULL AND status IN (1, 2);
