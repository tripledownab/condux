-- Uploaded artifacts (source maps, native debug files) are deleted from object storage through this
-- queue, never inline. A row leaves its index table and its key arrives here in the same statement, so a
-- crash between the two cannot lose track of an object, and the control-plane's worker deletes the
-- object later, retrying until it can. Three things queue keys: eviction past an org's storage quota,
-- expiry past its tier's retention, and deleting the project itself.
CREATE TABLE IF NOT EXISTS object_deletions (
    object_key  TEXT        PRIMARY KEY,
    -- A prefix stands for every object under it: a deleted project's whole directory of one kind.
    is_prefix   BOOLEAN     NOT NULL DEFAULT FALSE,
    enqueued_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS object_deletions_enqueued_idx ON object_deletions (enqueued_at);

-- A trigger rather than a step in the delete endpoint, because projects are also deleted by cascade when
-- their org goes, which no endpoint sees. The roots are Condux.Core.SourceMaps.ProjectObjectKeys.Roots,
-- pinned by UploadStorageTest, since SQL cannot read the C# list.
CREATE OR REPLACE FUNCTION queue_deleted_project_objects() RETURNS trigger AS $$
BEGIN
    INSERT INTO object_deletions (object_key, is_prefix)
    VALUES ('sourcemaps/' || OLD.id || '/', TRUE), ('debug-files/' || OLD.id || '/', TRUE)
    ON CONFLICT (object_key) DO NOTHING;
    RETURN OLD;
END;
$$ LANGUAGE plpgsql;

-- Dropped and created in one transaction, because a deploy runs this file while the previous release is
-- still serving (on first apply, and again whenever the migration files change other than by new ones
-- sorting last), and a project deleted between the two statements would queue nothing.
BEGIN;
DROP TRIGGER IF EXISTS projects_queue_objects ON projects;
CREATE TRIGGER projects_queue_objects AFTER DELETE ON projects
    FOR EACH ROW EXECUTE FUNCTION queue_deleted_project_objects();
COMMIT;
