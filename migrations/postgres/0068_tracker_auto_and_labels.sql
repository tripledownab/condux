-- Linear tickets, part two (ADR-0053): labels on every ticket a project opens, and opening one
-- automatically when a new error arrives.
--
-- label_ids and label_names travel together, the names kept as Linear gave them when chosen so the
-- dashboard can show them without asking Linear, like target_name beside target_id.
ALTER TABLE project_tracker_mappings ADD COLUMN IF NOT EXISTS auto_create BOOLEAN NOT NULL DEFAULT false;
ALTER TABLE project_tracker_mappings ADD COLUMN IF NOT EXISTS label_ids TEXT[] NOT NULL DEFAULT '{}';
ALTER TABLE project_tracker_mappings ADD COLUMN IF NOT EXISTS label_names TEXT[] NOT NULL DEFAULT '{}';

-- Whether a person or the auto-create setting reserved the ticket, so a ticket nobody asked for can be
-- told apart from one somebody did.
ALTER TABLE issue_tickets ADD COLUMN IF NOT EXISTS automatic BOOLEAN NOT NULL DEFAULT false;

-- Whether not_before is the tracker holding the org off (a 429, or the tracker not answering) rather than
-- a row waiting for its own reason, like an automatic ticket letting its first event settle. Only the
-- first kind pauses the org's other rows: a settling ticket pausing them would stall every click on a
-- project that keeps having new errors.
ALTER TABLE issue_tickets ADD COLUMN IF NOT EXISTS held_by_tracker BOOLEAN NOT NULL DEFAULT false;

-- The daily ceiling on automatic tickets per project. Anyone holding a DSN can mint new issues, so without
-- it a stranger could fill a customer's Linear workspace. A counter taken by a guarded upsert, because a
-- count of existing rows is passed by two reservations made at once. Days are UTC dates.
CREATE TABLE IF NOT EXISTS tracker_auto_tickets_daily (
    project_id BIGINT  NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    day        DATE    NOT NULL,
    created    INTEGER NOT NULL,
    PRIMARY KEY (project_id, day)
);
