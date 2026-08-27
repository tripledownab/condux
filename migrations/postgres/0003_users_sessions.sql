-- Auth: local user accounts and server-side sessions (#47).
-- Local email+password accounts; opaque session tokens are stored only as a hash (never in
-- plaintext) and carried in a first-party HttpOnly cookie for the same-origin dashboard
-- (the dashboard and the API share one origin, which is what lets the cookie be first-party).
-- Idempotent (IF NOT EXISTS), applied in filename order after 0002_orgs_projects_dsns.sql.
-- (Org membership + RBAC land next, in #48.)

CREATE TABLE IF NOT EXISTS users (
    id            BIGSERIAL   PRIMARY KEY,
    email         TEXT        NOT NULL UNIQUE,   -- stored normalized (trimmed + lower-cased)
    password_hash TEXT        NOT NULL,          -- encoded PBKDF2 (Condux.Core.Auth.PasswordHasher)
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Opaque session tokens. Only the SHA-256 hash of the token is stored; the raw token lives
-- solely in the client's cookie, so a database leak can't be replayed to mint sessions.
-- A session is valid while revoked_at IS NULL and expires_at is in the future.
CREATE TABLE IF NOT EXISTS sessions (
    id         BIGSERIAL   PRIMARY KEY,
    user_id    BIGINT      NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash TEXT        NOT NULL UNIQUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS sessions_user_idx ON sessions (user_id);
