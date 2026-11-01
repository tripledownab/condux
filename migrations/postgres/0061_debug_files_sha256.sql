-- The SHA-256 of each debug file, which is the name the symbolizer holds it under. Not the build ID: a
-- build ID is a note the file's author writes, and the symbolizer is shared by every project, so a cache
-- keyed on it would let one project's file answer for another project's build (ADR-0049, amendment).
-- Nullable because files uploaded before this column have no hash recorded; the first read that needs
-- one computes it from the stored file and writes it back.
--
-- This file runs again whenever the migration files change other than by new ones sorting last
-- (deploy/migrations/apply-postgres.sh), and ADD COLUMN IF NOT EXISTS takes the table's exclusive lock
-- even when the column is there, so it would queue behind any open transaction on debug_files (an
-- obfuscation map upload holds one for seconds) and stall every read of the table behind it. The catalog
-- is asked first, so a replay takes no lock on the table.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = current_schema() AND table_name = 'debug_files' AND column_name = 'sha256') THEN
        ALTER TABLE debug_files ADD COLUMN sha256 TEXT;
    END IF;
END
$$;
