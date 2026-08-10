-- The Fixes section (#118): per-user viewed tracking for the needs-attention badge, and a soft
-- archive so handled fixes drop out of the active views without destroying the audit artifact.

-- One row per (fix, user) the user has opened. Absence = unviewed. Cascades with the fix and the user.
CREATE TABLE IF NOT EXISTS fix_views (
    fix_id    UUID        NOT NULL REFERENCES fix_suggestions (id) ON DELETE CASCADE,
    user_id   BIGINT      NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    viewed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (fix_id, user_id)
);

-- Soft archive: a dismissed fix is hidden from active views and the badge, still readable under Archived.
ALTER TABLE fix_suggestions ADD COLUMN IF NOT EXISTS archived_at TIMESTAMPTZ;
