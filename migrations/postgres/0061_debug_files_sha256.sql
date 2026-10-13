-- The SHA-256 of each debug file, which is the name the symbolizer holds it under. Not the build ID: a
-- build ID is a note the file's author writes, and the symbolizer is shared by every project, so a cache
-- keyed on it would let one project's file answer for another project's build (ADR-0049, amendment).
-- Nullable because files uploaded before this column have no hash recorded; the first read that needs
-- one computes it from the stored file and writes it back.
ALTER TABLE debug_files ADD COLUMN IF NOT EXISTS sha256 TEXT;
