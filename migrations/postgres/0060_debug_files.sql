-- Native debug files: the DWARF symbols a native build leaves behind (a Flutter build's .symbols file is
-- one), uploaded by CI so a frame that carries only an instruction address can later be resolved to a
-- function, file and line. The file itself lives in object storage under object_key; this table indexes it
-- by the build it belongs to. build_id is the ELF GNU build ID read from the file, never a value the
-- uploader names, and debug_id is the form an event's debug image carries for the same build, which is
-- what a lookup from an event has to hand. Uploaded through the scoped release token, like source maps.
CREATE TABLE IF NOT EXISTS debug_files (
    id          UUID        PRIMARY KEY,
    project_id  BIGINT      NOT NULL REFERENCES projects (id) ON DELETE CASCADE,
    build_id    TEXT        NOT NULL,
    debug_id    TEXT        NOT NULL,
    object_key  TEXT        NOT NULL,
    byte_size   BIGINT      NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (project_id, build_id)
);

CREATE INDEX IF NOT EXISTS debug_files_debug_idx ON debug_files (project_id, debug_id);
