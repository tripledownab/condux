-- Issue tracker tickets (ADR-0036, amended by ADR-0053): an org's connection to a tracker workspace, a
-- project's choice of where its tickets land, and the tickets themselves.
--
-- tracker_connections holds one grant per org per provider. The tokens are sealed with SecretBox, like the
-- BYO model key, and both are rewritten on every refresh because Linear replaces the refresh token each
-- time. broken_at is set when a refresh is refused, which the dashboard shows as "Reconnect".
CREATE TABLE IF NOT EXISTS tracker_connections (
    org_id                  BIGINT      NOT NULL REFERENCES orgs(id) ON DELETE CASCADE,
    provider                SMALLINT    NOT NULL,
    workspace_id            TEXT        NOT NULL,
    workspace_name          TEXT        NOT NULL,
    access_token_encrypted  BYTEA       NOT NULL,
    refresh_token_encrypted BYTEA       NOT NULL,
    access_expires_at       TIMESTAMPTZ NOT NULL,
    broken_at               TIMESTAMPTZ,
    connected_by            BIGINT      REFERENCES users(id) ON DELETE SET NULL,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (org_id, provider)
);

-- Where a project's tickets land: for Linear a team id, kept with the name it had when chosen so the
-- dashboard can show it without asking Linear. needs_attention_at is set when Linear refuses a ticket for
-- that team, which is how a team the app lost access to surfaces on the project rather than the org.
CREATE TABLE IF NOT EXISTS project_tracker_mappings (
    project_id          BIGINT      NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    provider            SMALLINT    NOT NULL,
    target_id           TEXT        NOT NULL,
    target_name         TEXT        NOT NULL,
    needs_attention_at  TIMESTAMPTZ,
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (project_id, provider)
);

-- One row per ticket, reserved before the tracker is called. id doubles as the idempotency id sent to
-- Linear as the issue's own id, so it is a UUID v4 (Linear's IssueCreateInput.id requires that format),
-- unlike the UUIDv7 public ids elsewhere. The unique key is what stops a second ticket: two tabs, a click
-- racing an alert or a retry all lose the insert and read the row that won. workspace_id is part of it so
-- reconnecting to another workspace does not leave old links blocking new tickets.
--
-- state: 1 pending, 2 claimed, 3 created, 4 failed, 5 uncertain (Condux.Core.Tickets.TicketState).
CREATE TABLE IF NOT EXISTS issue_tickets (
    id            UUID        PRIMARY KEY,
    issue_id      BIGINT      NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
    provider      SMALLINT    NOT NULL,
    workspace_id  TEXT        NOT NULL,
    state         SMALLINT    NOT NULL,
    failure       SMALLINT,
    ticket_key    TEXT,
    ticket_url    TEXT,
    created_by    BIGINT      REFERENCES users(id) ON DELETE SET NULL,
    claimed_at    TIMESTAMPTZ,
    -- A pending row the tracker asked us to hold off on is not claimed before this.
    not_before    TIMESTAMPTZ,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (issue_id, provider, workspace_id)
);

-- The worker's claim reads only pending rows, oldest first.
CREATE INDEX IF NOT EXISTS issue_tickets_pending ON issue_tickets (created_at) WHERE state = 1;
