-- Issue assignment (#108): who owns the triage. NULL = unassigned; a deleted user unassigns rather
-- than blocking the delete.
ALTER TABLE issues ADD COLUMN IF NOT EXISTS assignee_user_id BIGINT REFERENCES users(id) ON DELETE SET NULL;
