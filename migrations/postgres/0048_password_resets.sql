-- Password reset tokens. Without these a forgotten password was terminal: there is no change-password
-- for someone who cannot sign in, and signup refuses an address that already exists, so the account and
-- its email were lost for good.
--
-- Shaped like org_invites, deliberately, because it is the same problem: an opaque token mailed to an
-- address, redeemed once, expiring on its own. Only the SHA-256 hash is stored, as with sessions and
-- invites, so a leak of this table cannot be replayed to take over an account.
--
-- Idempotent (IF NOT EXISTS), applied in filename order after 0047_weekly_summary_opt_out.sql.

CREATE TABLE IF NOT EXISTS password_resets (
    id         BIGSERIAL   PRIMARY KEY,
    user_id    BIGINT      NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash TEXT        NOT NULL UNIQUE,        -- SHA-256 of the raw token, hex, like sessions
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at TIMESTAMPTZ NOT NULL,
    used_at    TIMESTAMPTZ
);

-- Redemption looks up by hash alone, which the UNIQUE constraint already indexes. This index serves the
-- other two reads: how many resets an address asked for recently (the throttle), and invalidating a
-- user's outstanding tokens when one is redeemed or the password changes by another route.
CREATE INDEX IF NOT EXISTS password_resets_user_idx ON password_resets (user_id, created_at DESC);
