-- Why a fix run failed, as a closed set of reasons the dashboard can explain.
--
-- A failed run used to show only "Failed". The error text behind it stays in fix_audit for operators and
-- is deliberately never returned by the API, because several writers put free text there, a provider's
-- error body among them. This column holds a FixFailureReason value instead (Condux.Core.FixEngine), which
-- the API returns and the dashboard maps to an explanation and a next step.
--
-- NULL for every run that did not fail, and for runs that failed before this column existed: those have
-- no reason recorded, and inventing one would claim more than is known. The numbers are stored, so a value
-- is never reused for a different reason.
ALTER TABLE fix_suggestions ADD COLUMN IF NOT EXISTS failure_reason SMALLINT;
ALTER TABLE cve_fix_runs ADD COLUMN IF NOT EXISTS failure_reason SMALLINT;
