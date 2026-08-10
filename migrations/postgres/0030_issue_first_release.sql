-- Release attribution (option B): the release version the issue was first seen in, so the detail can show
-- "first seen in <release>". The consumer's upsert fills it from the event's release on the first
-- occurrence that carries one (COALESCE keeps the earliest). Nullable — events with no release leave it
-- NULL, and the UI simply omits the line. Idempotent.
ALTER TABLE issues ADD COLUMN IF NOT EXISTS first_release text;
