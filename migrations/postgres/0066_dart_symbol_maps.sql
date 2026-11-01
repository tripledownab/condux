-- A Flutter build's obfuscation map, held for the debug file of the same build, so a read can put an
-- obfuscated class name back in an exception's type and message. Keyed by the build because the map names
-- no build of its own, and a rebuild may rename differently.
--
-- One row per name, under the debug file it belongs to, so a read looks up the few names an event carries
-- through the primary key. A map kept as one JSONB value was tried first: Postgres decompresses the whole
-- value for every name asked, so the cost of a read grew with the names an event's sender chose times the
-- size of the map.
--
-- debug_files.dart_symbol_map_bytes is what the map costs, the upload and Postgres's row and index per
-- name (DartSymbolMapRepository), which the upload quota counts beside byte_size; without it a map would
-- be storage no quota reached. Eviction and expiry delete the debug file
-- row, and its names with it, through the cascade.
--
-- The column is an addition with a constant default and the table is new, so neither rewrites anything.
-- This file runs again whenever the migration files change other than by new ones sorting last
-- (deploy/migrations/apply-postgres.sh), and ADD COLUMN IF NOT EXISTS takes the table's exclusive lock
-- even when the column is there, so it would queue behind a map upload's open transaction and stall every
-- read of debug_files behind it. The catalog is asked first, so a replay takes no lock on the table.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = current_schema() AND table_name = 'debug_files'
          AND column_name = 'dart_symbol_map_bytes') THEN
        ALTER TABLE debug_files ADD COLUMN dart_symbol_map_bytes BIGINT NOT NULL DEFAULT 0;
    END IF;
END
$$;

CREATE TABLE IF NOT EXISTS dart_symbol_names (
    debug_file_id UUID NOT NULL REFERENCES debug_files (id) ON DELETE CASCADE,
    obfuscated    TEXT NOT NULL,
    original      TEXT NOT NULL,
    PRIMARY KEY (debug_file_id, obfuscated)
);
