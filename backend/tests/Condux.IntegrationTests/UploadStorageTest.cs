using Condux.Core.Plans;
using Condux.Core.SourceMaps;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// What an org may keep of its CI uploads, against real Postgres: eviction past the tier's quota,
/// expiry past the tier's retention, and the prefixes a deleted project leaves queued. Rows only; the
/// objects are <see cref="ObjectDeletionQueueTest"/>'s.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UploadStorageTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly long FreeQuota = PlanCatalog.For(Tier.Free).UploadBytes;

    [Fact]
    public async Task Past_the_quota_the_oldest_artifacts_go_and_the_newest_stay()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var oldest = await InsertMapAsync(projectId, FreeQuota / 2, Now.AddDays(-3));
        var middle = await InsertDebugFileAsync(projectId, FreeQuota / 2, Now.AddDays(-2));
        var newest = await InsertMapAsync(projectId, FreeQuota / 4, Now.AddDays(-1));

        Assert.Equal(1, await new UploadStorage(pg.ConnectionString).EvictBeyondQuotaAsync(projectId));

        Assert.Equal([middle, newest], await KeysAsync(projectId));
        Assert.Equal([oldest], await QueuedAsync(oldest));
    }

    [Fact]
    public async Task The_quota_is_the_orgs_so_its_projects_share_it_and_other_orgs_are_untouched()
    {
        // Each project alone is within the quota and the two together are not, so a per-project count
        // would evict nothing. Both victims sit in the sibling project, one of each kind, so each table's
        // half of the statement has to reach across the org. The other org holds one artifact of each kind,
        // newer than all of this org's and past the quota together, so an eviction that counted it, in either
        // table, would take more of this org's than two, and one that deleted from it would take its map.
        var (orgId, first) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var second = (await new ProjectRepository(pg.ConnectionString).CreateAsync(orgId, "Second", "other")).Id;
        var stranger = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        // The call passes a project id and the statement takes an org id, so they must differ here or
        // passing one as the other would go unnoticed.
        Assert.NotEqual(orgId, second);
        var otherOrgsMap = await InsertMapAsync(stranger, FreeQuota, Now.AddHours(-6));
        var otherOrgsFile = await InsertDebugFileAsync(stranger, FreeQuota / 2, Now.AddHours(-3));
        var oldestFile = await InsertDebugFileAsync(first, FreeQuota / 4, Now.AddDays(-4));
        var oldestMap = await InsertMapAsync(first, FreeQuota / 2, Now.AddDays(-3));
        var middle = await InsertDebugFileAsync(second, FreeQuota / 2, Now.AddDays(-2));
        var newest = await InsertMapAsync(second, FreeQuota / 4, Now.AddDays(-1));

        Assert.Equal(2, await new UploadStorage(pg.ConnectionString).EvictBeyondQuotaAsync(second));

        Assert.Empty(await KeysAsync(first));
        Assert.Equal([middle, newest], await KeysAsync(second));
        Assert.Equal(new[] { oldestFile, oldestMap }.Order(), await QueuedAsync(oldestFile, oldestMap));
        Assert.Equal([otherOrgsMap, otherOrgsFile], await KeysAsync(stranger));
    }

    [Fact]
    public async Task Within_the_quota_nothing_goes()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        await InsertMapAsync(projectId, FreeQuota / 2, Now.AddDays(-2));
        await InsertDebugFileAsync(projectId, FreeQuota / 2, Now.AddDays(-1));

        Assert.Equal(0, await new UploadStorage(pg.ConnectionString).EvictBeyondQuotaAsync(projectId));
        Assert.Equal(2, (await KeysAsync(projectId)).Count);
    }

    [Fact]
    public async Task An_unlimited_tier_keeps_everything()
    {
        var (orgId, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        await SetTierAsync(orgId, Tier.Enterprise);
        await InsertMapAsync(projectId, FreeQuota * 3, Now.AddDays(-2));
        await InsertMapAsync(projectId, FreeQuota * 3, Now.AddDays(-1));

        Assert.Equal(0, await new UploadStorage(pg.ConnectionString).EvictBeyondQuotaAsync(projectId));
        Assert.Equal(2, (await KeysAsync(projectId)).Count);
    }

    [Fact]
    public async Task Expiry_follows_each_tiers_retention()
    {
        var free = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var (teamOrg, team) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        await SetTierAsync(teamOrg, Tier.Team);
        var freeRetention = PlanCatalog.For(Tier.Free).RetentionDays;
        var expired = await InsertMapAsync(free, 10, Now.AddDays(-freeRetention - 1));
        var freeKept = await InsertDebugFileAsync(free, 10, Now.AddDays(-freeRetention + 1));
        var teamKept = await InsertMapAsync(team, 10, Now.AddDays(-freeRetention - 1));
        var expiredFile = await InsertDebugFileAsync(free, 10, Now.AddDays(-freeRetention - 2));

        await new UploadStorage(pg.ConnectionString).ExpireAsync(Now);

        Assert.Equal([freeKept], await KeysAsync(free));
        Assert.Equal([teamKept], await KeysAsync(team));
        Assert.Equal(new[] { expired, expiredFile }.Order(), await QueuedAsync(expired, expiredFile));
    }

    [Theory]
    [InlineData("sourcemap_artifacts")]
    [InlineData("debug_files")]
    public async Task An_artifact_re_uploaded_while_an_eviction_runs_is_not_evicted(string table)
    {
        // The re-upload keeps the row's id and stamps it now. Holding its write open forces the eviction to
        // block on that row and then re-check it against the committed version, which is the interleaving
        // two parallel CI uploads produce. An id match alone would evict the newest artifact there is.
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var stale = table == "debug_files"
            ? await InsertDebugFileAsync(projectId, FreeQuota, Now.AddDays(-2))
            : await InsertMapAsync(projectId, FreeQuota, Now.AddDays(-2));
        await InsertMapAsync(projectId, FreeQuota / 2, Now.AddDays(-1));

        await using var reupload = new NpgsqlConnection(pg.ConnectionString);
        await reupload.OpenAsync();
        await using var tx = await reupload.BeginTransactionAsync();
        await using (var touch = new NpgsqlCommand(
            $"UPDATE {table} SET created_at = now() WHERE object_key = '{stale}'", reupload, tx))
        {
            await touch.ExecuteNonQueryAsync();
        }

        var eviction = new UploadStorage(pg.ConnectionString).EvictBeyondQuotaAsync(projectId);
        await WaitUntilBlockedAsync();
        await tx.CommitAsync();
        await eviction;

        Assert.Contains(stale, await KeysAsync(projectId));
        Assert.Empty(await QueuedAsync(stale));
    }

    [Fact]
    public async Task A_tier_this_version_does_not_know_expires_with_the_fallback_tiers_retention()
    {
        var (orgId, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        await ExecuteAsync($"UPDATE orgs SET tier = 99 WHERE id = {orgId}");
        var retention = PlanCatalog.For(PlanCatalog.FallbackTier).RetentionDays;
        var expired = await InsertMapAsync(projectId, 10, Now.AddDays(-retention - 1));

        await new UploadStorage(pg.ConnectionString).ExpireAsync(Now);

        Assert.Empty(await KeysAsync(projectId));
        Assert.Equal([expired], await QueuedAsync(expired));
    }

    // Until another session waits on a lock, which is the eviction blocked on the held row.
    private async Task WaitUntilBlockedAsync()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var conn = new NpgsqlConnection(pg.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND datname = current_database()",
                conn);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0)
            {
                return;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("the eviction never blocked on the re-uploaded row");
    }

    [Fact]
    public async Task Uploading_a_map_again_makes_it_new_so_it_is_the_last_to_go()
    {
        // Its age is its last upload, as a debug file's already was, so a release still being uploaded
        // is not the first evicted or expired.
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var key = await InsertMapAsync(projectId, 10, Now.AddDays(-400));

        await new SourceMapArtifactRepository(pg.ConnectionString)
            .RecordAsync(projectId, "1.0.0", null, null, "app.js", key, 10);

        await new UploadStorage(pg.ConnectionString).ExpireAsync(DateTimeOffset.UtcNow);
        Assert.Equal([key], await KeysAsync(projectId));
    }

    [Fact]
    public async Task A_deleted_project_queues_one_prefix_per_root_the_uploads_write_under()
    {
        // The trigger spells the roots in SQL, which cannot read ProjectObjectKeys, so this is what holds the
        // two together: a root added in C# and not in the trigger leaves that kind's objects for ever.
        var (orgId, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var prefixes = ProjectObjectKeys.Roots.Select(root => ProjectObjectKeys.ToPrefix(root, projectId)).ToArray();

        await ExecuteAsync($"DELETE FROM orgs WHERE id = {orgId}");

        Assert.Equal(prefixes.Order(), await QueuedAsync(prefixes));
        foreach (var prefix in prefixes)
        {
            Assert.True(await IsPrefixAsync(prefix));
        }
    }

    private async Task<string> InsertMapAsync(long projectId, long size, DateTimeOffset at)
    {
        var key = ProjectObjectKeys.ToPrefix(ProjectObjectKeys.SourceMaps, projectId) + $"by-debug-id/{Guid.NewGuid():N}";
        await ExecuteAsync(
            "INSERT INTO sourcemap_artifacts (id, project_id, release, filename, object_key, byte_size, created_at) "
            + $"VALUES ('{Guid.CreateVersion7()}', {projectId}, '1.0.0', 'app.js', '{key}', {size}, '{at:O}')");
        return key;
    }

    private async Task<string> InsertDebugFileAsync(long projectId, long size, DateTimeOffset at)
    {
        var build = Guid.NewGuid().ToString("N");
        var key = ProjectObjectKeys.ToPrefix(ProjectObjectKeys.DebugFiles, projectId) + build;
        await ExecuteAsync(
            "INSERT INTO debug_files (id, project_id, build_id, debug_id, object_key, byte_size, created_at) "
            + $"VALUES ('{Guid.CreateVersion7()}', {projectId}, '{build}', '{build}', '{key}', {size}, '{at:O}')");
        return key;
    }

    private Task SetTierAsync(long orgId, Tier tier) =>
        ExecuteAsync($"UPDATE orgs SET tier = {(short)tier} WHERE id = {orgId}");

    // Oldest first, matching how the tests list what they expect to stay.
    private async Task<List<string>> KeysAsync(long projectId) => await ReadKeysAsync(
        $"""
        SELECT object_key FROM (
          SELECT object_key, created_at FROM sourcemap_artifacts WHERE project_id = {projectId}
          UNION ALL SELECT object_key, created_at FROM debug_files WHERE project_id = {projectId}) a
        ORDER BY created_at
        """);

    private async Task<List<string>> QueuedAsync(params string[] keys) => await ReadKeysAsync(
        "SELECT object_key FROM object_deletions WHERE object_key = ANY(ARRAY['"
        + string.Join("','", keys) + "']) ORDER BY object_key");

    private async Task<bool> IsPrefixAsync(string key)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT is_prefix FROM object_deletions WHERE object_key = @k", conn);
        cmd.Parameters.AddWithValue("k", key);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<List<string>> ReadKeysAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var keys = new List<string>();
        while (await reader.ReadAsync())
        {
            keys.Add(reader.GetString(0));
        }
        return keys;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
