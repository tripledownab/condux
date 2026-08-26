-- TOTP multi-factor authentication for dashboard accounts (RFC 6238, ADR-0039).
--
-- The shared secret has to be recoverable, because the server recomputes the expected code on every
-- verification, so unlike a password or a session token it cannot be hashed. It is therefore SEALED
-- rather than stored: SecretBox (AES-256-GCM), the same seam the BYO-key registry and the SSO client
-- secrets use, keyed by CONDUX_SECRET_KEY. Storing it plaintext and relying on encrypted backups covers
-- backup theft but not a read-only database compromise or SQL injection, which is the likelier way a
-- table leaks.
create table if not exists user_mfa (
    user_id         bigint primary key references users(id) on delete cascade,
    secret_encrypted bytea not null,        -- SecretBox blob: version(1) || nonce || tag || ciphertext
    confirmed_at    timestamptz,            -- null = enrolled but never proven, so NOT yet enforced
    -- Replay guard. RFC 6238 section 5.2: a code must not be accepted twice. Without this, a code seen
    -- over a shoulder or through a phishing proxy stays usable for the rest of its 30-second window.
    -- Compared with < rather than <>, because with a one-step drift window equality alone would still
    -- admit the previous step's code.
    last_used_step  bigint,
    -- Per-account brute-force control. A per-IP limiter cannot see an attack spread across a botnet,
    -- and the one we have fails open when Valkey is unavailable, which would silently remove MFA's only
    -- protection while login kept working. This lives in Postgres so it cannot fail open.
    failed_attempts int not null default 0,
    -- Deliberately a COOLDOWN, not a lock. A lock keyed on an account is a denial-of-service primitive:
    -- anyone who knows an email address could lock its owner out at will.
    locked_until    timestamptz,
    created_at      timestamptz not null default now()
);

-- Single-use recovery codes, so losing the authenticator does not lock the account out forever. Hashed
-- like every other credential we hold; the raw codes are shown once, at confirmation, and never again.
create table if not exists user_mfa_recovery_codes (
    user_id   bigint not null references users(id) on delete cascade,
    code_hash text not null,
    used_at   timestamptz,
    primary key (user_id, code_hash)
);

-- A session that has passed the password but not the second factor. Marking the existing session rather
-- than inventing a second token type means the ONE session resolver can refuse it in one place, so every
-- authenticated endpoint is gated by default instead of each having to remember. The false default is
-- what makes this migration safe for sessions that already exist.
alter table sessions add column if not exists mfa_pending boolean not null default false;

-- Per-session attempt counter. Exhausting it kills the pending session, so the attacker has to re-enter
-- the password to get another, which costs a legitimate user one retype.
alter table sessions add column if not exists mfa_attempts int not null default 0;
