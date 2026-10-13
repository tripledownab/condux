using Condux.Core.Plans;
using Condux.Core.SourceMaps;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>What one sweep did: objects listed, orphans found and queued, objects too recent to judge, and
/// keys left alone for not following the layout.</summary>
public sealed record SweepResult(int Listed, int Orphans, int Queued, int Recent, int Unrecognised);

/// <summary>
/// Finds uploaded objects that no row names and queues them for <see cref="ObjectDeletionQueue"/>. An upload
/// writes its row before its object, so an object with no row is one whose row has gone (evicted, expired,
/// or deleted with its project) without the delete reaching the object. This only queues: whether a key may
/// go is decided by the drain's claim, under the key's lock, so an artifact uploaded again after this pass
/// looked keeps its object.
///
/// "No row" is also what a deployment pointed at the wrong database sees: an empty one, one restored from a
/// backup, or another environment's. So the sweep judges only objects older than the longest retention any
/// tier has, plus a margin. Expiry removes every artifact older than its tier's retention, and an upload
/// stamps its row no later than its object, so an object that old still has a live row only when its last
/// upload wrote the row and then failed to store the object, leaving older bytes behind. Short of that,
/// whatever database the sweep reads, it can only queue what the deployment the bucket belongs to has
/// expired or is due to expire. That holds while the owner's own expiry runs, while both read the same
/// retentions, and while their clocks are within the margin of each other; the margin also covers a drain
/// that is behind. The age is fixed rather than a parameter, because a smaller one is the one setting that
/// would take the bound away.
/// </summary>
public sealed class OrphanObjectSweep(string connectionString, IObjectStoreCleanup store)
{
    // Read from the catalog rather than fixed, so a tier given a longer retention moves this with it.
    private static readonly TimeSpan MinAge =
        TimeSpan.FromDays(Enum.GetValues<Tier>().Max(t => PlanCatalog.For(t).RetentionDays) + 14);

    // Matched on the project id as well as the key: neither table indexes object_key alone, and both tables
    // have an index that leads with project_id, so the planner has one to use for each lookup.
    private const string FindOrphansSql = """
        SELECT k.object_key FROM unnest(@keys, @projects) AS k(object_key, project_id)
        WHERE NOT EXISTS (SELECT 1 FROM sourcemap_artifacts s
                          WHERE s.project_id = k.project_id AND s.object_key = k.object_key)
          AND NOT EXISTS (SELECT 1 FROM debug_files d
                          WHERE d.project_id = k.project_id AND d.object_key = k.object_key);
        """;

    private const string QueueSql = """
        INSERT INTO object_deletions (object_key) SELECT unnest(@keys) ON CONFLICT (object_key) DO NOTHING;
        """;

    /// <summary>Lists every object under every upload root and queues those no row names. Objects written
    /// after <paramref name="now"/> less the minimum age are not judged.</summary>
    public async Task<SweepResult> SweepAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        var orphans = new List<string>();
        int listed = 0, recent = 0, unrecognised = 0;
        var cutoff = now - MinAge;
        foreach (var root in ProjectObjectKeys.Roots)
        {
            await foreach (var page in store.ListAsync(root, ct))
            {
                listed += page.Count;
                var keys = new List<string>(page.Count);
                var projects = new List<long>(page.Count);
                foreach (var stored in page)
                {
                    if (ProjectObjectKeys.ToProjectId(root, stored.Key) is not { } projectId)
                    {
                        unrecognised++;
                    }
                    else if (stored.LastModified is not { } written || written > cutoff)
                    {
                        recent++;
                    }
                    else
                    {
                        keys.Add(stored.Key);
                        projects.Add(projectId);
                    }
                }
                if (keys.Count > 0)
                {
                    orphans.AddRange(await FindOrphansAsync(conn, keys, projects, ct));
                }
            }
        }
        var queued = 0;
        foreach (var batch in orphans.Chunk(1000))
        {
            await using var queue = new NpgsqlCommand(QueueSql, conn);
            queue.Parameters.AddWithValue("keys", batch);
            queued += await queue.ExecuteNonQueryAsync(ct);
        }
        return new SweepResult(listed, orphans.Count, queued, recent, unrecognised);
    }

    private static async Task<List<string>> FindOrphansAsync(
        NpgsqlConnection conn, List<string> keys, List<long> projects, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(FindOrphansSql, conn);
        cmd.Parameters.AddWithValue("keys", keys.ToArray());
        cmd.Parameters.AddWithValue("projects", projects.ToArray());
        var found = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            found.Add(reader.GetString(0));
        }
        return found;
    }
}
