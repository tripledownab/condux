-- Issue notes (collaborative triage): free-form notes members leave on an issue. The id is a UUID
-- (non-enumerable, like release_tokens); issue_id cascades so notes die with the issue; a deleted author
-- nulls out rather than blocking the delete (the note text stays as history).
CREATE TABLE IF NOT EXISTS issue_notes (
    id             UUID        PRIMARY KEY,
    issue_id       BIGINT      NOT NULL REFERENCES issues (id) ON DELETE CASCADE,
    author_user_id BIGINT      REFERENCES users (id) ON DELETE SET NULL,
    body           TEXT        NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS issue_notes_issue_idx ON issue_notes (issue_id, created_at);
