-- Re-check a proved SSO domain claim on a timer (ADR-0043 slice 2).
--
-- Migration 0052 made a claim prove itself once. A claim true in March should not still route in
-- December: domains change hands, and the org that proved one may no longer hold it. The control-plane
-- therefore re-reads the TXT record daily, and clears routing after a grace period of consecutive
-- failures rather than on the first one, so a transient DNS problem does not take an enterprise's
-- single sign-on away.
--
-- verification_checked_at is what makes that affordable and safe to run on several replicas. It is the
-- claim as much as the record: the worker stamps it in the same guarded UPDATE that picks the row, so a
-- row is handed to exactly one replica per day, a restart neither loses a day nor re-reads everything,
-- and the DNS lookups are paced by the same statement.
--
-- NULL means never checked, and the worker takes those first, so every row proved before this migration
-- is re-read on the first tick after deploy. That includes the rows 0052 grandfathered without any DNS
-- proof at all. It is deliberate and it is the point of re-verification, but it is also the one change
-- here that can take a working installation's SSO away: a self-hoster on an internal domain must set
-- CONDUX_SSO_SKIP_DOMAIN_VERIFICATION, which answers the check for every row, including on this path.
ALTER TABLE sso_configs
    ADD COLUMN IF NOT EXISTS verification_checked_at TIMESTAMPTZ;

-- Only verified rows are ever re-checked, so the index covers only those. A provisional claim routes
-- nothing, and re-reading DNS for one would spend lookups on every squatter's claim for ever while
-- changing no outcome: the org's own Verify button is what promotes it.
CREATE INDEX IF NOT EXISTS sso_configs_recheck_due_idx
    ON sso_configs (verification_checked_at NULLS FIRST) WHERE verified_at IS NOT NULL;
