using Condux.Core.Plans;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// What an org may keep of the artifacts CI uploads (source maps and native debug files), as rows. Each
/// statement here removes rows and queues their object keys in <c>object_deletions</c> in one go, and
/// <see cref="ObjectDeletionQueue"/> deletes the objects later. A release token is one free signup away,
/// so without this a stranger chose how much storage the service kept, for ever.
/// The age of an artifact is its last upload: re-uploading the same key refreshes it.
/// </summary>
public sealed class UploadStorage(string connectionString)
{
    // Both kinds, across every project of the org, in one ordering, newest first, so the running total
    // says which artifacts fit. The quota is the org's because an org may create any number of projects,
    // so a per-project quota multiplied with each one. The id breaks ties, and is time-ordered (UUIDv7),
    // so one upload's order is stable.
    private const string ArtifactsSql = """
        WITH org_projects AS (SELECT id FROM projects WHERE org_id = @org),
        artifacts AS (
            SELECT 's' AS kind, id, byte_size, created_at FROM sourcemap_artifacts
            WHERE project_id IN (SELECT id FROM org_projects)
            UNION ALL
            SELECT 'd', id, byte_size, created_at FROM debug_files
            WHERE project_id IN (SELECT id FROM org_projects)),
        victims AS (
            SELECT kind, id, created_at FROM (
                SELECT kind, id, created_at, SUM(byte_size) OVER (ORDER BY created_at DESC, id DESC) AS kept
                FROM artifacts) ranked
            WHERE kept > @quota),
        """;

    // The created_at match is what spares a row CI re-uploaded while this ran: a re-upload keeps the
    // row's id and stamps it now, and Postgres re-checks the join against the row it blocked on, so an id
    // match alone would evict the newest artifact the org has. Expiry's cutoff is re-checked the same
    // way, which is why it needs nothing extra.
    private const string EvictSql = ArtifactsSql + """
        gone_maps AS (
            DELETE FROM sourcemap_artifacts s USING victims v
            WHERE v.kind = 's' AND s.id = v.id AND s.created_at = v.created_at
            RETURNING s.object_key),
        gone_files AS (
            DELETE FROM debug_files d USING victims v
            WHERE v.kind = 'd' AND d.id = v.id AND d.created_at = v.created_at
            RETURNING d.object_key)
        INSERT INTO object_deletions (object_key)
        SELECT object_key FROM gone_maps UNION SELECT object_key FROM gone_files
        ON CONFLICT (object_key) DO NOTHING;
        """;

    private const string TierSql = """
        SELECT o.id, o.tier FROM projects p JOIN orgs o ON o.id = p.org_id WHERE p.id = @project;
        """;

    // One tier at a time, since retention is the tier's and lives in PlanCatalog, not in SQL. A stored
    // tier the enum does not know expires with Free's, the tier PlanCatalog.For falls back to, so an org
    // the catalog cannot place is not kept for ever.
    private const string ExpireSql = """
        WITH tiered AS (
            SELECT p.id FROM projects p JOIN orgs o ON o.id = p.org_id
            WHERE o.tier = @tier OR (@fallback AND o.tier <> ALL(@known))),
        gone_maps AS (
            DELETE FROM sourcemap_artifacts s USING tiered t
            WHERE s.project_id = t.id AND s.created_at < @cutoff
            RETURNING s.object_key),
        gone_files AS (
            DELETE FROM debug_files d USING tiered t
            WHERE d.project_id = t.id AND d.created_at < @cutoff
            RETURNING d.object_key)
        INSERT INTO object_deletions (object_key)
        SELECT object_key FROM gone_maps UNION SELECT object_key FROM gone_files
        ON CONFLICT (object_key) DO NOTHING;
        """;

    /// <summary>Evicts the oldest artifacts of the org that owns <paramref name="projectId"/>, from any of
    /// its projects, until what the org keeps fits its tier's quota, and returns how many it queued. The
    /// newest upload is never evicted, since one upload is far below any quota.</summary>
    public async Task<int> EvictBeyondQuotaAsync(long projectId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        long orgId;
        short stored;
        await using (var tier = new NpgsqlCommand(TierSql, conn))
        {
            tier.Parameters.AddWithValue("project", projectId);
            await using var reader = await tier.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return 0;
            }
            orgId = reader.GetInt64(0);
            stored = reader.GetInt16(1);
        }
        var limits = PlanCatalog.For((Tier)stored);
        if (limits.UnlimitedUploads)
        {
            return 0;
        }
        await using var evict = new NpgsqlCommand(EvictSql, conn);
        evict.Parameters.AddWithValue("org", orgId);
        evict.Parameters.AddWithValue("quota", limits.UploadBytes);
        return await evict.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Expires every artifact older than its tier's retention, as of <paramref name="now"/>, and
    /// returns how many it queued.</summary>
    public async Task<int> ExpireAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        var queued = 0;
        var known = Enum.GetValues<Tier>().Select(t => (short)t).ToArray();
        foreach (var tier in Enum.GetValues<Tier>())
        {
            await using var cmd = new NpgsqlCommand(ExpireSql, conn);
            cmd.Parameters.AddWithValue("tier", (short)tier);
            cmd.Parameters.AddWithValue("fallback", tier == PlanCatalog.FallbackTier);
            cmd.Parameters.AddWithValue("known", known);
            cmd.Parameters.AddWithValue("cutoff", now.AddDays(-PlanCatalog.For(tier).RetentionDays));
            queued += await cmd.ExecuteNonQueryAsync(ct);
        }
        return queued;
    }
}
