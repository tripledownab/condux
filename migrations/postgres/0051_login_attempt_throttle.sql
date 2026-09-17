-- Per-account throttling of password guesses.
--
-- Nothing counted a wrong password before this, at either of the two places one is verified: signing in,
-- and re-authenticating for a sensitive action such as enrolling a second factor. The only thing bounding
-- an attacker was the cost of Argon2id, which has no notion of an account and so never gets slower for
-- the address under attack.
--
-- These columns live on users rather than beside the second-factor counter in user_mfa, because a
-- user_mfa row exists only for an account that enrolled a factor. Read from there, a password throttle
-- would silently do nothing for every account without one. The two share their STATEMENT instead, built
-- once in FailureCooldownSql.
alter table users add column if not exists failed_logins int not null default 0;

-- Deliberately a COOLDOWN, not a lock, for the same reason as user_mfa.locked_until: a lock keyed on an
-- account is a denial-of-service primitive, since anyone who knows an email address could hold its owner
-- out at will. The count is windowed, so a lapsed cooldown starts again from the next failure rather than
-- leaving the account one typo from being locked again for ever.
alter table users add column if not exists login_locked_until timestamptz;
